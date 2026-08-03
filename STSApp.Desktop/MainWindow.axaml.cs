using Avalonia.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using STSApp.Contracts.Enums;
using STSApp.Contracts.Events;
using STSApp.Contracts.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace STSApp.Desktop;

public partial class MainWindow : Window
{
    // REST API、SignalR、録音、音声再生は役割を分けています。
    // MainWindowにすべて直接書くと見通しが悪くなるため、外部とのやり取りは小さなクラスへ逃がしています。
    private readonly BackendApiClient _backendApiClient;
    private readonly ConversationHubClient _conversationHubClient;
    private readonly VoiceInputSessionController _voiceInputController = new();
    private readonly AudioPlaybackService _audioPlaybackService = new();
    private readonly ChatMessageListController _chatMessages;
    // SignalR通知とREST完了応答を同じ順序付きキューへ集め、音声の重複・並行再生を防ぎます。
    private readonly HashSet<Guid> _completedAudioPlaybackIds = [];
    private readonly Dictionary<Guid, AudioTurnPlaybackState> _audioTurnPlaybackStates = [];
    private readonly object _audioPlaybackQueueGate = new();
    private readonly Dictionary<Guid, long> _transcriptionDisplayedTimestamps = [];
    private readonly Dictionary<Guid, long> _speechEndedTimestamps = [];
    private readonly HashSet<Guid> _latencyRecordedTurnIds = [];
    private readonly HashSet<Guid> _observedTurnIds = [];
    // 起動処理と履歴更新が同時に会話を作ろうとしても、作成APIは1本ずつ実行します。
    // 1画面で2つの会話IDが競合し、通知先と保存先が分かれることを防ぐためです。
    private readonly SemaphoreSlim _conversationCreationGate = new(1, 1);

    // Windowを閉じた時に、実行中のHTTP通信やSignalR接続へキャンセルを伝えるためのものです。
    // 非同期処理が画面破棄後も残ると、例外や不要な通信の原因になります。
    private readonly CancellationTokenSource _windowClosingTokenSource = new();
    private CancellationTokenSource? _playbackCancellationSource;
    private bool _isAudioPlaybackActive;
    private bool _isCloseConfirmationOpen;
    private bool _allowWindowClose;
    private long? _latestSpeechEndedTimestamp;

    // Backendで作られた会話セッションIDです。
    // 音声アップロード、履歴取得、SignalR通知のフィルタリングで同じIDを使います。
    private Guid? _conversationId;
    private bool _isBackendReady;

    public MainWindow()
        : this(DesktopAppSettings.Load())
    {
    }

    public MainWindow(DesktopAppSettings settings)
    {
        InitializeComponent();

        _backendApiClient = new BackendApiClient(settings.BackendBaseUrl);
        _conversationHubClient = new ConversationHubClient(settings.BackendBaseUrl);
        _chatMessages = new ChatMessageListController(
            MessagesPanel,
            MessagesScrollViewer);
        _voiceInputController.StateChanged += ApplyVoiceInputState;
        _voiceInputController.ActivityChanged += VoiceInputController_ActivityChanged;
        _voiceInputController.AudioReady += VoiceInputController_AudioReady;
        _voiceInputController.ErrorOccurred += VoiceInputController_ErrorOccurred;
        ApplyVoiceInputState(VoiceInputState.Ready);

        // Windowが表示された後にBackend接続を始めます。
        // コンストラクタ内で待ち処理をすると、画面表示が遅くなるためです。
        RegisterSignalREvents();
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        try
        {
            // SignalRを先に接続してから会話を作ります。
            // これにより、後続の音声処理でBackendから送られる状態通知を受け取りやすくします。
            await StartSignalRAsync();
            await CreateConversationAsync();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Window startup failed: {exception.GetType().Name}");
            AddErrorMessage("アプリの初期化に失敗しました。Backendの状態を確認して再起動してください。");
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        // 削除通知と録音・再生の停止は、Closingイベントで完了させています。
        // Closedでは画面破棄後に必要なリソースだけを解放します。
        TryCleanupOnClose(_windowClosingTokenSource.Cancel);
        TryCleanupOnClose(_chatMessages.Dispose);
        TryCleanupOnClose(() => _voiceInputController.StateChanged -= ApplyVoiceInputState);
        TryCleanupOnClose(() => _voiceInputController.ActivityChanged -= VoiceInputController_ActivityChanged);
        TryCleanupOnClose(() => _voiceInputController.AudioReady -= VoiceInputController_AudioReady);
        TryCleanupOnClose(() => _voiceInputController.ErrorOccurred -= VoiceInputController_ErrorOccurred);
        TryCleanupOnClose(_voiceInputController.Dispose);
        TryCleanupOnClose(_backendApiClient.Dispose);

        try
        {
            await _conversationHubClient.DisposeAsync();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"SignalR disposal failed: {exception.GetType().Name}");
        }
        finally
        {
            TryCleanupOnClose(_windowClosingTokenSource.Dispose);
        }
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowWindowClose)
        {
            return;
        }

        // 終了前に確認を出すため、いったん通常の終了処理を止めます。
        e.Cancel = true;
        if (_isCloseConfirmationOpen)
        {
            return;
        }

        _isCloseConfirmationOpen = true;
        try
        {
            var shouldClose = await new CloseConfirmationDialog().ShowDialog<bool>(this);
            if (!shouldClose)
            {
                return;
            }

            await PrepareForWindowCloseAsync();
            _allowWindowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            // async voidイベントから例外を外へ出すと、AvaloniaのUIスレッドが
            // 未処理例外としてプロセスを終了するため、画面へ戻して再操作可能にします。
            Trace.WriteLine($"Window closing failed: {exception.GetType().Name}");
            StatusText.Text = "終了処理に失敗しました";
            InputHintText.Text = "もう一度終了してください。繰り返す場合はアプリを再起動してください。";
        }
        finally
        {
            _isCloseConfirmationOpen = false;
        }
    }

    private async Task PrepareForWindowCloseAsync()
    {
        // 音声処理と再生を先に止めます。
        // その後に削除することで、再生中の音声ファイルを削除処理と競合させません。
        _playbackCancellationSource?.Cancel();
        _voiceInputController.StopSessionAndDiscard();

        Task[] playbackTasks;
        lock (_audioPlaybackQueueGate)
        {
            playbackTasks = _audioTurnPlaybackStates.Values
                .Select(state => state.RunningTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        }
        if (playbackTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(playbackTasks);
            }
            catch
            {
                // 終了時は再生エラーを画面へ追加できないため、終了処理を続けます。
            }
        }

        if (_conversationId is not Guid conversationId)
        {
            return;
        }

        try
        {
            // Backendが応答しない場合もアプリ終了を妨げないよう、待機時間を限定します。
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _backendApiClient.DeleteConversationAudioAsync(
                conversationId,
                cleanupTimeout.Token);
        }
        catch
        {
            // 通信できない場合は、次回Backend起動時の整理に任せます。
        }
    }

    private async void RefreshButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 会話を同期は「サーバーに保存された現在の状態」を取り直す操作です。
        // SignalR通知を取り逃した場合でも、このボタンでサーバー側の最終状態を確認できます。
        await RefreshConversationTurnsAsync();
    }

    private async void ConversationHistoryButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_isBackendReady || _voiceInputController.State == VoiceInputState.Processing)
        {
            return;
        }

        try
        {
            ConversationHistoryButton.IsEnabled = false;
            StatusText.Text = "会話履歴を取得中";
            InputHintText.Text = "保存された会話を読み込んでいます。";

            var conversations = await _backendApiClient.ListConversationsAsync(
                _windowClosingTokenSource.Token);
            var dialog = new ConversationHistoryDialog(conversations);
            var selectedConversation = await dialog.ShowDialog<ConversationDto?>(this);

            if (selectedConversation is not null)
            {
                await SelectConversationAsync(selectedConversation);
            }
            else
            {
                StatusText.Text = "サーバー接続済み";
                InputHintText.Text = "音声入力開始を押して、話しかけてください。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "会話履歴を取得できませんでした";
            InputHintText.Text = "サーバーの状態を確認してから、もう一度お試しください。";
            AddErrorMessage($"会話履歴を取得できませんでした: {ex.Message}");
        }
        finally
        {
            ConversationHistoryButton.IsEnabled = _isBackendReady
                && _voiceInputController.State != VoiceInputState.Processing;
        }
    }

    private async Task SelectConversationAsync(ConversationDto conversation)
    {
        // 選択した会話IDを以降の音声送信・履歴取得・SignalR通知の基準にします。
        // これにより、過去の会話を表示した後も、その会話へ続けて発話できます。
        _conversationId = conversation.Id;
        _completedAudioPlaybackIds.Clear();
        _audioTurnPlaybackStates.Clear();
        _chatMessages.ResetStreamingState();

        await TryJoinConversationNotificationsAsync(conversation.Id);
        var synchronized = await RefreshConversationTurnsAsync(showSyncMessage: false);
        if (!synchronized)
        {
            return;
        }

        StatusText.Text = "会話を表示中";
        InputHintText.Text = "この会話へ続けて話しかけられます。";
    }

    private void VoiceInputButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_isAudioPlaybackActive)
            {
                _playbackCancellationSource?.Cancel();
                StatusText.Text = "返答音声を停止しました";
                InputHintText.Text = "次の発話を受け付ける準備をしています。";
                return;
            }

            if (_voiceInputController.State == VoiceInputState.Processing)
            {
                return;
            }

            if (_voiceInputController.SessionEnabled)
            {
                _voiceInputController.StopSessionAndDiscard();
                return;
            }

            _voiceInputController.StartSession();
        }
        catch (Exception exception)
        {
            // UIイベントから未処理例外を出さず、利用者が再起動できる状態を保ちます。
            Trace.WriteLine($"Voice input button operation failed: {exception.GetType().Name}");
            StatusText.Text = "音声入力操作失敗";
            InputHintText.Text = "音声入力を操作できませんでした。アプリを再起動してください。";
            AddErrorMessage("音声入力の開始または停止に失敗しました。");
        }
    }

    private void VoiceInputController_ActivityChanged(VoiceInputSessionActivity activity)
    {
        switch (activity)
        {
            case VoiceInputSessionActivity.ListeningStarted:
                _chatMessages.ClearDesktopErrors();
                StatusText.Text = "音声入力待機中";
                InputHintText.Text = "話しかけてください。話し終えると自動で送信します。";
                break;
            case VoiceInputSessionActivity.SpeechStarted:
                StatusText.Text = "発話を検知しました";
                InputHintText.Text = "話し終えると自動で送信します。";
                break;
            case VoiceInputSessionActivity.SpeechEnded:
                _latestSpeechEndedTimestamp = Stopwatch.GetTimestamp();
                StatusText.Text = "終話を検知しました";
                InputHintText.Text = "音声を送信しています...";
                break;
            case VoiceInputSessionActivity.ListeningStopped:
                StatusText.Text = "音声入力停止";
                InputHintText.Text = "音声入力開始を押して、話しかけてください。";
                break;
        }
    }

    private void VoiceInputController_AudioReady(RecordedAudio audio)
    {
        _ = SendRecordedAudioAsync(audio);
    }

    private void VoiceInputController_ErrorOccurred(VoiceInputSessionError error)
    {
        if (error.Kind == VoiceInputSessionErrorKind.Stop)
        {
            StatusText.Text = "音声入力停止失敗";
            InputHintText.Text = "音声入力を停止できませんでした。Desktopアプリを再起動してください。";
            AddErrorMessage($"音声入力を停止する際に問題が起きました: {error.Message}");
            return;
        }

        switch (error.Kind)
        {
            case VoiceInputSessionErrorKind.Start:
                StatusText.Text = "音声入力開始失敗";
                InputHintText.Text = "macOSのマイク権限と入力デバイスを確認し、Desktopアプリを再起動してください。";
                AddErrorMessage($"音声入力の待機を開始できませんでした: {error.Message}");
                break;
            case VoiceInputSessionErrorKind.NoAudioFrame:
                StatusText.Text = "マイク入力開始失敗";
                InputHintText.Text = "入力デバイスを確認して、音声入力をもう一度開始してください。";
                AddErrorMessage($"マイク入力を開始できませんでした: {error.Message}");
                break;
            case VoiceInputSessionErrorKind.CaptureStart:
                StatusText.Text = "録音保存開始失敗";
                InputHintText.Text = "マイク権限と入力デバイスを確認してから、音声入力をもう一度開始してください。";
                AddErrorMessage($"発話音声を保存できませんでした。マイク権限と入力デバイスを確認してください: {error.Message}");
                break;
            case VoiceInputSessionErrorKind.Finalize:
                StatusText.Text = "録音保存失敗";
                InputHintText.Text = "マイク権限と入力デバイスを確認してから、音声入力をもう一度開始してください。";
                AddErrorMessage($"録音データを確定できませんでした。マイク権限と入力デバイスを確認してください: {error.Message}");
                break;
        }
    }

    private async Task SendRecordedAudioAsync(RecordedAudio audio)
    {
        var uploadWasAttempted = false;
        // 送信前から存在していたターンと、今回Backendが新しく作ったターンを区別します。
        // 単に履歴に失敗があるかを見るだけでは、過去の失敗を今回の失敗と誤認するためです。
        var turnIdsBeforeUpload = _observedTurnIds.ToHashSet();

        try
        {
            if (_conversationId is null)
            {
                await CreateConversationAsync();
            }

            if (_conversationId is null)
            {
                throw new InvalidOperationException("会話セッションがないため、音声を送信できませんでした。");
            }

            // 起動時にSignalR接続へ失敗していても、発話のたびに再接続とグループ参加を試します。
            // ここで失敗してもRESTの音声処理と、完了後の履歴・音声取得は続けられます。
            await TryJoinConversationNotificationsAsync(_conversationId.Value);

            // 音声ファイルはRESTでBackendへ送ります。
            // 文字起こし結果やAI返答など、処理途中の変化はSignalRで別途受け取ります。
            uploadWasAttempted = true;
            var result = await _backendApiClient.UploadAudioTurnAsync(
                _conversationId.Value,
                audio,
                _windowClosingTokenSource.Token);

            StatusText.Text = "音声送信完了";
            InputHintText.Text = "サーバーへ音声を送信しました。";

            // SignalRを取り逃した場合、チャット本文はDB履歴から、返答音声はREST応答のIDから復元します。
            // 先に履歴を表示してから再生することで、「テキストだけ先に表示」の順序も維持します。
            // これは内部的な同期です。利用者が押す「会話を同期」の完了文言を、
            // AI返答の処理中に表示しないため、同期完了メッセージは出しません。
            await RefreshConversationTurnsAsync(showSyncMessage: false);
            QueueCompletedAudioSequence(result.TurnId, result.OutputAudioIds);
        }
        catch (Exception ex)
        {
            _voiceInputController.SetExternalFailure();
            StatusText.Text = "サーバー処理失敗";
            InputHintText.Text = uploadWasAttempted
                ? "「会話を同期」を押してサーバー側の状態を確認してください。"
                : "サーバーへ音声を送信できませんでした。サーバーの状態を確認してください。";

            var backendFailureWasStored = uploadWasAttempted
                && await TryRefreshTurnsAfterUploadFailureAsync(turnIdsBeforeUpload);

            // Backendが新しい失敗ターンを保存できた場合は、そのDBエラーを表示します。
            // 通信切断などでターン自体が作られなかった場合だけ、Desktop側の通信エラーを残します。
            if (!backendFailureWasStored)
            {
                AddErrorMessage($"サーバーへ音声を送信できませんでした。サーバーの状態を確認してください: {ex.Message}");
            }
        }
    }

    private async Task CreateConversationAsync()
    {
        try
        {
            await _conversationCreationGate.WaitAsync(_windowClosingTokenSource.Token);
        }
        catch (OperationCanceledException) when (_windowClosingTokenSource.IsCancellationRequested)
        {
            // 画面終了中は新しい会話を作る必要がありません。
            // 直列化待ちのキャンセルを終了時エラーとして画面へ返さず、そのまま処理を終えます。
            return;
        }

        try
        {
            if (_conversationId is not null)
            {
                _isBackendReady = true;
                ApplyVoiceInputState(_voiceInputController.State);
                return;
            }

            SetBusyState("サーバー接続中...");

            // このアプリでは認証がないため、起動時に新しい会話セッションを作成します。
            // 返ってきたconversationIdを以降のREST/SignalR表示フィルタに使います。
            _conversationId = await _backendApiClient.CreateConversationAsync(
                "Avalonia音声対話",
                _windowClosingTokenSource.Token);

            await TryJoinConversationNotificationsAsync(_conversationId.Value);

            _isBackendReady = true;
            _chatMessages.ClearDesktopErrors();
            ApplyVoiceInputState(_voiceInputController.State);
            StatusText.Text = "サーバー接続済み";
            InputHintText.Text = "音声入力開始を押して、話しかけてください。";
        }
        catch (Exception ex)
        {
            _isBackendReady = false;
            ApplyVoiceInputState(_voiceInputController.State);
            StatusText.Text = "サーバー接続失敗";
            InputHintText.Text = "サーバーを起動してから「会話を同期」を押してください。";
            AddErrorMessage($"サーバーへ接続できませんでした: {ex.Message}");
        }
        finally
        {
            _conversationCreationGate.Release();
        }
    }

    private async Task TryJoinConversationNotificationsAsync(Guid conversationId)
    {
        try
        {
            // BackendのSignalR通知は会話単位のグループへ送られます。
            // 接続が切れた後も現在の会話へ入り直し、途中経過を再び受け取れるようにします。
            await _conversationHubClient.JoinConversationAsync(
                conversationId,
                _windowClosingTokenSource.Token);
        }
        catch (Exception ex)
        {
            // SignalRは途中経過の通知経路です。RESTが利用できる時まで音声対話を止める必要はありません。
            // 完了結果は音声送信のREST応答と履歴取得から復元します。
            AddErrorMessage($"リアルタイム通知を利用できません。サーバーを再起動した場合は、会話を同期してください: {ex.Message}");
        }
    }

    private async Task StartSignalRAsync()
    {
        try
        {
            SetBusyState("SignalR接続中...");

            // SignalRはBackendからAvaloniaへ「今どの処理中か」を届けるための接続です。
            // RESTのようにAvaloniaから毎回問い合わせるのではなく、Backend側から通知が届きます。
            await _conversationHubClient.StartAsync(_windowClosingTokenSource.Token);
            _chatMessages.ClearDesktopErrors();
        }
        catch (Exception ex)
        {
            AddErrorMessage($"リアルタイム通知を利用できません。会話の完了結果は会話を同期して確認できます: {ex.Message}");
        }
    }

    private void HandleSignalRReconnecting(Exception? exception)
    {
        if (_windowClosingTokenSource.IsCancellationRequested)
        {
            return;
        }

        _isBackendReady = false;
        ApplyVoiceInputState(_voiceInputController.State);
        StatusText.Text = "サーバー再接続中";
        InputHintText.Text = "サーバーへの再接続を試みています。";
    }

    private async Task HandleSignalRReconnectedAsync()
    {
        if (_windowClosingTokenSource.IsCancellationRequested || _conversationId is null)
        {
            return;
        }

        _isBackendReady = true;
        ApplyVoiceInputState(_voiceInputController.State);
        _chatMessages.ClearDesktopErrors();
        StatusText.Text = "サーバー再接続完了";
        InputHintText.Text = "会話を同期しています。";

        // 再接続中に取り逃した通知があっても、DBに保存済みの状態を読み直して補正します。
        var synchronized = await RefreshConversationTurnsAsync(showSyncMessage: false);
        if (!synchronized)
        {
            return;
        }

        StatusText.Text = "サーバー接続済み";
        InputHintText.Text = "音声入力開始を押して、話しかけてください。";
    }

    private void HandleSignalRClosed(Exception? exception)
    {
        if (_windowClosingTokenSource.IsCancellationRequested)
        {
            return;
        }

        _isBackendReady = false;
        ApplyVoiceInputState(_voiceInputController.State);
        StatusText.Text = "サーバー接続切断";
        InputHintText.Text = "サーバーを確認してから、会話を同期してください。";
        AddErrorMessage("サーバーとの接続が切断されました。サーバーを確認してください。");
    }

    private void RegisterSignalREvents()
    {
        // SignalRのイベントはUIスレッドとは別の場所で呼ばれることがあります。
        // Avaloniaの画面要素を更新する時は、RunOnUiThreadを通してUIスレッドへ戻します。
        _conversationHubClient.TurnStatusChanged += value =>
        {
            RunOnUiThread(() => HandleTurnStatusChanged(value));
        };

        _conversationHubClient.TranscriptionCompleted += value =>
        {
            RunOnUiThread(() =>
            {
                if (!IsCurrentConversation(value.ConversationId))
                {
                    return;
                }

                _observedTurnIds.Add(value.TurnId);
                AddUserMessage(value.UserText, value.TurnId);
                _transcriptionDisplayedTimestamps[value.TurnId] = Stopwatch.GetTimestamp();
                if (_latestSpeechEndedTimestamp is long speechEndedTimestamp)
                {
                    _speechEndedTimestamps[value.TurnId] = speechEndedTimestamp;
                }
                StatusText.Text = "文字起こし完了";
                InputHintText.Text = "AI返答を生成しています。";
            });
        };

        _conversationHubClient.AssistantTextChunkGenerated += value =>
        {
            RunOnUiThread(() =>
            {
                if (!IsCurrentConversation(value.ConversationId))
                {
                    return;
                }

                _observedTurnIds.Add(value.TurnId);
                _chatMessages.AppendAssistantMessageChunk(value.TurnId, value.Sequence, value.Text);
                StatusText.Text = "AI返答を受信中";
                InputHintText.Text = "返答音声を準備しています。";
            });
        };

        _conversationHubClient.AssistantTextCompleted += value =>
        {
            RunOnUiThread(() =>
            {
                if (!IsCurrentConversation(value.ConversationId))
                {
                    return;
                }

                _observedTurnIds.Add(value.TurnId);
                AddAssistantMessage(value.AssistantText, value.TurnId, value.AnswerBasis);
                StatusText.Text = "AI返答テキスト生成完了";
                InputHintText.Text = "返答音声を生成しています。";
            });
        };

        _conversationHubClient.SpeechSynthesisCompleted += value =>
        {
            RunOnUiThread(() =>
            {
                if (!IsCurrentConversation(value.ConversationId))
                {
                    return;
                }

                _observedTurnIds.Add(value.TurnId);
                StatusText.Text = "返答音声生成完了";
                InputHintText.Text = "返答音声を再生しています。";

                QueueCompletedAudioSequence(value.TurnId, value.AudioIds);
            });
        };

        _conversationHubClient.SpeechSynthesisChunkCompleted += value =>
        {
            RunOnUiThread(() =>
            {
                if (!IsCurrentConversation(value.ConversationId))
                {
                    return;
                }

                _observedTurnIds.Add(value.TurnId);
                StatusText.Text = "返答音声を受信中";
                InputHintText.Text = "返答音声を再生しています。";
                QueueAudioChunk(value.TurnId, value.Sequence, value.AudioId);
            });
        };

        _conversationHubClient.TurnFailed += value =>
        {
            RunOnUiThread(() =>
            {
                if (!IsCurrentConversation(value.ConversationId))
                {
                    return;
                }

                _observedTurnIds.Add(value.TurnId);
                AddErrorMessage(
                    $"{FormatStage(value.Stage)} / {value.Message}",
                    value.TurnId);
                _voiceInputController.SetExternalFailure();
                StatusText.Text = "処理失敗";
                InputHintText.Text = "「会話を同期」を押してサーバー側の状態を確認してください。";
            });
        };

        _conversationHubClient.Reconnecting += exception =>
        {
            RunOnUiThread(() => HandleSignalRReconnecting(exception));
        };

        _conversationHubClient.Reconnected += () =>
        {
            RunOnUiThread(() => _ = HandleSignalRReconnectedAsync());
        };

        _conversationHubClient.ConnectionClosed += exception =>
        {
            RunOnUiThread(() => HandleSignalRClosed(exception));
        };
    }

    private void HandleTurnStatusChanged(TurnStatusChangedEvent value)
    {
        if (!IsCurrentConversation(value.ConversationId))
        {
            return;
        }

        _observedTurnIds.Add(value.TurnId);
        // Backendは upload / stt / rag / gemini / tts のような細かい状態を通知します。
        // 途中経過をチャット履歴へ混ぜると履歴更新時に消えて見えるため、現在状態の表示だけを更新します。
        var stageText = FormatStage(value.Stage);
        var eventText = FormatEventType(value.EventType);
        var message = string.IsNullOrWhiteSpace(value.Message)
            ? $"{stageText}: {eventText}"
            : $"{stageText}: {eventText} - {value.Message}";

        StatusText.Text = message;
        InputHintText.Text = stageText switch
        {
            "STT" => "音声を文字起こししています。",
            "RAG" => "関連資料を検索しています。",
            "Gemini" => "AI返答を生成しています。",
            "TTS" => "返答音声を生成しています。",
            _ => message
        };

    }

    private void QueueCompletedAudioSequence(Guid turnId, IReadOnlyList<Guid> audioIds)
    {
        lock (_audioPlaybackQueueGate)
        {
            var state = GetOrCreateAudioTurnPlaybackState(turnId);
            state.Buffer.Restore(
                audioIds,
                audioId => _backendApiClient.DownloadAudioAsync(
                    audioId,
                    _windowClosingTokenSource.Token));
        }

        StartAudioTurnQueueIfNeeded(turnId);
    }

    private void QueueAudioChunk(Guid turnId, int sequence, Guid audioId)
    {
        lock (_audioPlaybackQueueGate)
        {
            var state = GetOrCreateAudioTurnPlaybackState(turnId);
            state.Buffer.Add(
                sequence,
                audioId,
                id => _backendApiClient.DownloadAudioAsync(
                    id,
                    _windowClosingTokenSource.Token));
        }

        StartAudioTurnQueueIfNeeded(turnId);
    }

    private AudioTurnPlaybackState GetOrCreateAudioTurnPlaybackState(Guid turnId)
    {
        if (!_audioTurnPlaybackStates.TryGetValue(turnId, out var state))
        {
            state = new AudioTurnPlaybackState();
            _audioTurnPlaybackStates.Add(turnId, state);
        }

        return state;
    }

    private void StartAudioTurnQueueIfNeeded(Guid turnId)
    {
        AudioTurnPlaybackState state;
        lock (_audioPlaybackQueueGate)
        {
            state = GetOrCreateAudioTurnPlaybackState(turnId);
            if (state.IsRunning)
            {
                return;
            }

            if (state.Buffer.IsCancelled)
            {
                return;
            }

            if (!state.Buffer.HasNext && !state.Buffer.IsComplete)
            {
                return;
            }

            state.IsRunning = true;
        }

        var runningTask = ProcessAudioTurnQueueAsync(turnId, state);
        lock (_audioPlaybackQueueGate)
        {
            state.RunningTask = runningTask;
        }
    }

    private async Task ProcessAudioTurnQueueAsync(Guid turnId, AudioTurnPlaybackState state)
    {
        var completedNormally = false;
        try
        {
            while (true)
            {
                BufferedAudioChunk<Task<byte[]>>? chunk;
                lock (_audioPlaybackQueueGate)
                {
                    if (!state.Buffer.TryTakeNext(out chunk))
                    {
                        completedNormally = state.Buffer.IsComplete;
                        return;
                    }
                }

                if (!await DownloadAndPlayAudioWithRetryAsync(turnId, chunk))
                {
                    lock (_audioPlaybackQueueGate)
                    {
                        state.Buffer.Cancel();
                    }

                    return;
                }

                _completedAudioPlaybackIds.Add(chunk.AudioId);
            }
        }
        finally
        {
            var shouldRestart = false;
            lock (_audioPlaybackQueueGate)
            {
                state.IsRunning = false;
                state.RunningTask = null;
                shouldRestart = state.Buffer.HasNext;
                completedNormally = completedNormally || state.Buffer.IsComplete;
            }

            if (completedNormally)
            {
                CompleteAudioTurnPlayback();
            }
            else if (shouldRestart)
            {
                StartAudioTurnQueueIfNeeded(turnId);
            }
        }
    }

    private async Task<bool> DownloadAndPlayAudioWithRetryAsync(
        Guid turnId,
        BufferedAudioChunk<Task<byte[]>> queuedChunk)
    {
        Exception? lastException = null;
        using var playbackCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            _windowClosingTokenSource.Token);
        _playbackCancellationSource = playbackCancellationSource;

        try
        {
            // SignalRとRESTのどちらが先に届いても、同じ共有Taskを待ちます。
            // 一時的な取得失敗で音声を永久に失わないよう、画面に再生ボタンを戻さず1回だけ自動再試行します。
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var audioBytes = attempt == 1
                        ? await queuedChunk.Value.WaitAsync(playbackCancellationSource.Token)
                        : await _backendApiClient.DownloadAudioAsync(
                            queuedChunk.AudioId,
                            playbackCancellationSource.Token);

                    // 音声ファイルの取得が終わり、実際の再生を開始する直前にだけ
                    // 停止操作を有効にします。TTS生成中の中断は今回の対象外です。
                    _isAudioPlaybackActive = true;
                    ApplyVoiceInputState(VoiceInputState.Processing);
                    RecordFirstAudioLatency(turnId);
                    await _audioPlaybackService.PlayWavAsync(
                        audioBytes,
                        playbackCancellationSource.Token);

                    _chatMessages.ClearDesktopErrors();
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                    if (attempt == 1)
                    {
                        try
                        {
                            await Task.Delay(250, playbackCancellationSource.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            ResumeAfterPlaybackCancellation();
                            return false;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 利用者の停止操作とWindow終了は、通常の再生エラーとして表示しません。
                    ResumeAfterPlaybackCancellation();
                    return false;
                }
            }

            _voiceInputController.SetExternalFailure();
            StatusText.Text = "返答音声再生失敗";
            InputHintText.Text = "返答音声を再生できませんでした。サーバーの状態を確認してから、もう一度お試しください。";
            AddErrorMessage($"返答音声を再生できませんでした。会話を同期してから、もう一度お試しください: {lastException?.Message}");
            return false;
        }
        finally
        {
            _isAudioPlaybackActive = false;
            _playbackCancellationSource = null;
        }
    }

    private void RecordFirstAudioLatency(Guid turnId)
    {
        if (!_latencyRecordedTurnIds.Add(turnId))
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var transcriptionToAudioMs = _transcriptionDisplayedTimestamps.TryGetValue(turnId, out var transcriptionTimestamp)
            ? Stopwatch.GetElapsedTime(transcriptionTimestamp, now).TotalMilliseconds
            : (double?)null;
        var speechEndToAudioMs = _speechEndedTimestamps.TryGetValue(turnId, out var speechEndedTimestamp)
            ? Stopwatch.GetElapsedTime(speechEndedTimestamp, now).TotalMilliseconds
            : (double?)null;

        Trace.WriteLine(
            $"TurnLatency TurnId={turnId} SpeechEndToFirstAudioMs={speechEndToAudioMs:F0} "
            + $"TranscriptionToFirstAudioMs={transcriptionToAudioMs:F0}");
    }

    private void CompleteAudioTurnPlayback()
    {
        _chatMessages.ClearDesktopErrors();
        if (!_voiceInputController.SessionEnabled)
        {
            StatusText.Text = "返答音声再生完了";
            InputHintText.Text = "音声入力開始を押して、話しかけてください。";
        }

        _voiceInputController.ResumeAfterResponse();
    }

    private void ResumeAfterPlaybackCancellation()
    {
        // Window終了時は、入力待機へ戻す必要がありません。
        // 利用者が再生を止めた場合だけ、次の発話を受け付けられる状態へ戻します。
        if (!_windowClosingTokenSource.IsCancellationRequested)
        {
            _voiceInputController.ResumeAfterResponse();
        }
    }

    private bool IsCurrentConversation(Guid conversationId)
    {
        // Backendでも会話グループへ絞っていますが、画面側でもIDを確認します。
        // 再接続直後などに古い通知が届いても、別の会話へ誤表示しないための二重の防御です。
        return _conversationId is not null && _conversationId.Value == conversationId;
    }

    private static void RunOnUiThread(Action action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                // SignalRコールバックの画面反映失敗でアプリ全体を終了させません。
                Trace.WriteLine($"UI notification failed: {exception.GetType().Name}");
            }
        });
    }

    private static void TryCleanupOnClose(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            // 一つの解放失敗で後続リソースの解放を中断しません。
            Trace.WriteLine($"Window cleanup failed: {exception.GetType().Name}");
        }
    }

    private static string FormatStage(ProcessingStage stage)
    {
        return stage switch
        {
            ProcessingStage.Upload => "アップロード",
            ProcessingStage.Stt => "STT",
            ProcessingStage.Rag => "RAG",
            ProcessingStage.Gemini => "Gemini",
            ProcessingStage.Tts => "TTS",
            ProcessingStage.Database => "DB",
            _ => stage.ToString()
        };
    }

    private static string FormatEventType(TurnEventType eventType)
    {
        return eventType switch
        {
            TurnEventType.Started => "開始",
            TurnEventType.Completed => "完了",
            TurnEventType.Failed => "失敗",
            TurnEventType.Info => "情報",
            _ => eventType.ToString()
        };
    }

    private async Task<bool> RefreshConversationTurnsAsync(bool showSyncMessage = true)
    {
        try
        {
            if (_conversationId is null)
            {
                await CreateConversationAsync();
                return true;
            }

            SetBusyState("会話を同期中...");

            var turns = await _backendApiClient.ListConversationTurnsAsync(
                _conversationId.Value,
                _windowClosingTokenSource.Token);

            // 履歴更新では、画面のメッセージ一覧をDBの状態に合わせて作り直します。
            // これにより、SignalRを取り逃してもBackendに保存済みの状態へ戻せます。
            _chatMessages.ReplaceFromTurns(turns);
            RememberObservedTurns(turns);
            _chatMessages.ClearDesktopErrors();
            StatusText.Text = $"サーバー接続済み / {turns.Count}ターン";
            if (showSyncMessage)
            {
                InputHintText.Text = "会話を同期しました。";
            }

            return true;
        }
        catch (Exception ex)
        {
            _isBackendReady = false;
            ApplyVoiceInputState(_voiceInputController.State);
            StatusText.Text = "会話の同期に失敗しました";
            InputHintText.Text = "サーバーの状態を確認してから、もう一度お試しください。";
            AddErrorMessage($"会話を同期できませんでした。サーバーの状態を確認してください: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryRefreshTurnsAfterUploadFailureAsync(
        IReadOnlySet<Guid> turnIdsBeforeUpload)
    {
        if (_conversationId is null)
        {
            return false;
        }

        try
        {
            var turns = await _backendApiClient.ListConversationTurnsAsync(
                _conversationId.Value,
                _windowClosingTokenSource.Token);

            _chatMessages.ReplaceFromTurns(turns);
            RememberObservedTurns(turns);

            // ここでは失敗表示を成功表示へ上書きしません。
            // 今回新しく作られ、failedまで保存されたターンがある時だけBackend側の失敗と判断します。
            return turns.Any(turn =>
                !turnIdsBeforeUpload.Contains(turn.Id)
                && turn.Status == TurnStatus.Failed);
        }
        catch
        {
            // 履歴まで取得できない時は、送信元の通信エラーを表示する方が利用者に状況を伝えられます。
            return false;
        }
    }

    private void RememberObservedTurns(IEnumerable<STSApp.Contracts.Models.ConversationTurnDto> turns)
    {
        foreach (var turn in turns)
        {
            _observedTurnIds.Add(turn.Id);
        }
    }

    private void SetBusyState(string text)
    {
        StatusText.Text = text;
        InputHintText.Text = text;
    }

    private void ApplyVoiceInputState(VoiceInputState state)
    {
        // Backend処理中だけは停止・開始を受け付けません。
        // 処理中にマイクを開くと、どの発話への返答かが混ざってしまうためです。
        VoiceInputButton.IsEnabled = _isBackendReady
            && (state != VoiceInputState.Processing || _isAudioPlaybackActive);

        ConversationHistoryButton.IsEnabled = _isBackendReady
            && state != VoiceInputState.Processing;

        VoiceInputButton.Content = state switch
        {
            VoiceInputState.Listening or VoiceInputState.Recording => "音声入力停止",
            VoiceInputState.Processing when _isAudioPlaybackActive => "返答音声を停止",
            VoiceInputState.Processing => "処理中...",
            _ => "音声入力開始"
        };
    }

    private void AddUserMessage(string text, Guid? turnId = null)
    {
        _chatMessages.AddUserMessage(text, turnId);
    }

    private void AddErrorMessage(string text, Guid? turnId = null)
    {
        _chatMessages.AddErrorMessage(text, turnId);
    }

    private void AddAssistantMessage(
        string text,
        Guid? turnId = null,
        AnswerBasis? answerBasis = null)
    {
        _chatMessages.AddAssistantMessage(text, turnId, answerBasis);
    }

    private sealed class AudioTurnPlaybackState
    {
        public OrderedAudioChunkBuffer<Task<byte[]>> Buffer { get; } = new();
        public bool IsRunning { get; set; }
        public Task? RunningTask { get; set; }
    }

}
