using Avalonia.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using STSApp.Contracts.Enums;
using STSApp.Contracts.Events;
using System.Collections.Generic;
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
    // SignalR通知とREST完了応答の両方から同じ音声IDが届くため、再生済みIDを覚えて二重再生を防ぎます。
    private readonly HashSet<Guid> _completedAudioPlaybackIds = [];
    private readonly Dictionary<Guid, Task<bool>> _activeAudioPlaybackTasks = [];
    private readonly HashSet<Guid> _observedTurnIds = [];
    // 起動処理と履歴更新が同時に会話を作ろうとしても、作成APIは1本ずつ実行します。
    // 1画面で2つの会話IDが競合し、通知先と保存先が分かれることを防ぐためです。
    private readonly SemaphoreSlim _conversationCreationGate = new(1, 1);

    // Windowを閉じた時に、実行中のHTTP通信やSignalR接続へキャンセルを伝えるためのものです。
    // 非同期処理が画面破棄後も残ると、例外や不要な通信の原因になります。
    private readonly CancellationTokenSource _windowClosingTokenSource = new();

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
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        // SignalRを先に接続してから会話を作ります。
        // これにより、後続の音声処理でBackendから送られる状態通知を受け取りやすくします。
        await StartSignalRAsync();
        await CreateConversationAsync();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        // 画面が閉じられた後にHTTP通信が続かないよう、キャンセルします。
        _windowClosingTokenSource.Cancel();
        _windowClosingTokenSource.Dispose();
        _chatMessages.Dispose();
        _voiceInputController.StateChanged -= ApplyVoiceInputState;
        _voiceInputController.ActivityChanged -= VoiceInputController_ActivityChanged;
        _voiceInputController.AudioReady -= VoiceInputController_AudioReady;
        _voiceInputController.ErrorOccurred -= VoiceInputController_ErrorOccurred;
        _voiceInputController.Dispose();
        _backendApiClient.Dispose();
        await _conversationHubClient.DisposeAsync();
    }

    private async void RefreshButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 履歴更新は「DBに保存された現在の状態」を取り直す操作です。
        // SignalR通知を取り逃した場合でも、このボタンでBackend側の最終状態を確認できます。
        await RefreshConversationTurnsAsync();
    }

    private void VoiceInputButton_Click(object? sender, RoutedEventArgs e)
    {
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

    private void VoiceInputController_ActivityChanged(VoiceInputSessionActivity activity)
    {
        switch (activity)
        {
            case VoiceInputSessionActivity.ListeningStarted:
                StatusText.Text = "音声入力待機中";
                InputHintText.Text = "話しかけてください。話し終えると自動で送信します。";
                break;
            case VoiceInputSessionActivity.SpeechStarted:
                StatusText.Text = "発話を検知しました";
                InputHintText.Text = "話し終えると自動で送信します。";
                break;
            case VoiceInputSessionActivity.SpeechEnded:
                StatusText.Text = "終話を検知しました";
                InputHintText.Text = "音声を送信しています...";
                break;
            case VoiceInputSessionActivity.ListeningStopped:
                StatusText.Text = "音声入力停止";
                InputHintText.Text = "音声入力開始を押すと、話しかける準備ができます。";
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
            AddErrorMessage($"音声入力を停止する際に問題が起きました: {error.Message}");
            return;
        }

        switch (error.Kind)
        {
            case VoiceInputSessionErrorKind.Start:
                StatusText.Text = "音声入力開始失敗";
                InputHintText.Text = "macOSのマイク権限、または入力デバイスの状態を確認してください。";
                AddErrorMessage($"音声入力の待機を開始できませんでした: {error.Message}");
                break;
            case VoiceInputSessionErrorKind.CaptureStart:
                StatusText.Text = "録音保存開始失敗";
                InputHintText.Text = "音声入力を開始し直してください。";
                AddErrorMessage($"発話音声の保存を開始できませんでした: {error.Message}");
                break;
            case VoiceInputSessionErrorKind.Finalize:
                StatusText.Text = "録音保存失敗";
                InputHintText.Text = "音声入力を開始し直してください。";
                AddErrorMessage($"録音データを作成できませんでした: {error.Message}");
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
            InputHintText.Text = "Backendへ音声を送信しました。";

            // SignalRを取り逃した場合、チャット本文はDB履歴から、返答音声はREST応答のIDから復元します。
            // 先に履歴を表示してから再生することで、「テキストだけ先に表示」の順序も維持します。
            await RefreshConversationTurnsAsync();
            await PlayAudioOnceAsync(result.OutputAudioId);
        }
        catch (Exception ex)
        {
            _voiceInputController.SetExternalFailure();
            StatusText.Text = "Backend処理失敗";
            InputHintText.Text = uploadWasAttempted
                ? "履歴更新でBackend側の状態を確認できます。"
                : "Backendへ音声を送信できませんでした。";

            var backendFailureWasStored = uploadWasAttempted
                && await TryRefreshTurnsAfterUploadFailureAsync(turnIdsBeforeUpload);

            // Backendが新しい失敗ターンを保存できた場合は、そのDBエラーを表示します。
            // 通信切断などでターン自体が作られなかった場合だけ、Desktop側の通信エラーを残します。
            if (!backendFailureWasStored)
            {
                AddErrorMessage($"Backendへ音声を送信できませんでした: {ex.Message}");
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

            SetBusyState("Backend接続中...");

            // このアプリでは認証がないため、起動時に新しい会話セッションを作成します。
            // 返ってきたconversationIdを以降のREST/SignalR表示フィルタに使います。
            _conversationId = await _backendApiClient.CreateConversationAsync(
                "Avalonia音声対話",
                _windowClosingTokenSource.Token);

            await TryJoinConversationNotificationsAsync(_conversationId.Value);

            _isBackendReady = true;
            ApplyVoiceInputState(_voiceInputController.State);
            StatusText.Text = $"Backend接続済み / Conversation: {_conversationId}";
            InputHintText.Text = "音声入力開始を押すと、話しかける準備ができます。";
        }
        catch (Exception ex)
        {
            _isBackendReady = false;
            ApplyVoiceInputState(_voiceInputController.State);
            StatusText.Text = "Backend接続失敗";
            InputHintText.Text = "Backendを起動してから履歴更新を押してください。";
            AddErrorMessage($"Backendへ接続できませんでした: {ex.Message}");
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
            AddErrorMessage($"リアルタイム通知へ接続できませんでした: {ex.Message}");
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
        }
        catch (Exception ex)
        {
            AddErrorMessage($"SignalRへ接続できませんでした: {ex.Message}");
        }
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
                StatusText.Text = "文字起こし完了";
                InputHintText.Text = "AI返答を生成しています。";
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
            RunOnUiThread(async () =>
            {
                if (!IsCurrentConversation(value.ConversationId))
                {
                    return;
                }

                _observedTurnIds.Add(value.TurnId);
                StatusText.Text = "返答音声生成完了";
                InputHintText.Text = "返答音声を再生しています。";

                await PlayAudioOnceAsync(value.AudioId);
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
                InputHintText.Text = "履歴更新でBackend側の状態を確認できます。";
            });
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

    private async Task<bool> DownloadAndPlayAudioWithRetryAsync(Guid audioId)
    {
        Exception? lastException = null;

        // SignalRとRESTのどちらが先に届いても、同じ共有Taskを待ちます。
        // 一時的な取得失敗で音声を永久に失わないよう、画面に再生ボタンを戻さず1回だけ自動再試行します。
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var audioBytes = await _backendApiClient.DownloadAudioAsync(
                    audioId,
                    _windowClosingTokenSource.Token);

                await _audioPlaybackService.PlayWavAsync(
                    audioBytes,
                    _windowClosingTokenSource.Token);

                if (!_voiceInputController.SessionEnabled)
                {
                    StatusText.Text = "返答音声再生完了";
                    InputHintText.Text = "音声入力開始を押すと、話しかける準備ができます。";
                }

                // 1回の開始操作で次の発話も受け付けるため、セッションが有効なら再生後に待機へ戻します。
                _voiceInputController.ResumeAfterResponse();
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                if (attempt == 1)
                {
                    try
                    {
                        await Task.Delay(250, _windowClosingTokenSource.Token);
                    }
                    catch (OperationCanceledException) when (_windowClosingTokenSource.IsCancellationRequested)
                    {
                        return false;
                    }
                }
            }
            catch (OperationCanceledException) when (_windowClosingTokenSource.IsCancellationRequested)
            {
                return false;
            }
        }

        _voiceInputController.SetExternalFailure();
        StatusText.Text = "返答音声再生失敗";
        InputHintText.Text = "音声ファイルを取得または再生できませんでした。";
        AddErrorMessage($"返答音声を再生できませんでした: {lastException?.Message}");
        return false;
    }

    private async Task PlayAudioOnceAsync(Guid audioId)
    {
        if (_completedAudioPlaybackIds.Contains(audioId))
        {
            return;
        }

        if (!_activeAudioPlaybackTasks.TryGetValue(audioId, out var playbackTask))
        {
            playbackTask = DownloadAndPlayAudioWithRetryAsync(audioId);
            _activeAudioPlaybackTasks.Add(audioId, playbackTask);
        }

        try
        {
            if (await playbackTask)
            {
                _completedAudioPlaybackIds.Add(audioId);
            }
        }
        finally
        {
            _activeAudioPlaybackTasks.Remove(audioId);
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
        Dispatcher.UIThread.Post(action);
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

    private async Task RefreshConversationTurnsAsync()
    {
        try
        {
            if (_conversationId is null)
            {
                await CreateConversationAsync();
                return;
            }

            SetBusyState("履歴取得中...");

            var turns = await _backendApiClient.ListConversationTurnsAsync(
                _conversationId.Value,
                _windowClosingTokenSource.Token);

            // 履歴更新では、画面のメッセージ一覧をDBの状態に合わせて作り直します。
            // これにより、SignalRを取り逃してもBackendに保存済みの状態へ戻せます。
            _chatMessages.ReplaceFromTurns(turns);
            RememberObservedTurns(turns);
            StatusText.Text = $"Backend接続済み / {turns.Count}ターン";
            InputHintText.Text = "履歴を更新しました。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "履歴取得失敗";
            InputHintText.Text = "Backendの状態を確認してください。";
            AddErrorMessage($"履歴を取得できませんでした: {ex.Message}");
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
        VoiceInputButton.IsEnabled = _isBackendReady && state != VoiceInputState.Processing;

        VoiceInputButton.Content = state switch
        {
            VoiceInputState.Listening or VoiceInputState.Recording => "音声入力停止",
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

}
