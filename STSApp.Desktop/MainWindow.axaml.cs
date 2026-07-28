using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using STSApp.Contracts.Enums;
using STSApp.Contracts.Events;
using STSApp.Contracts.Models;

namespace STSApp.Desktop;

public partial class MainWindow : Window
{
    // ObservableCollection は、会話履歴をアプリ内部で保持するためのコレクションです。
    // 現在の画面はMessagesPanelへカードを明示的に追加する構成のため、
    // 画面への反映はAddMessageToPanelなどの処理が担当します。
    private readonly ObservableCollection<ChatMessageItem> _messages = new();

    // REST API、SignalR、録音、音声再生は役割を分けています。
    // MainWindowにすべて直接書くと見通しが悪くなるため、外部とのやり取りは小さなクラスへ逃がしています。
    private readonly BackendApiClient _backendApiClient;
    private readonly ConversationHubClient _conversationHubClient;
    private readonly PushToTalkAudioRecorder _audioRecorder = new();
    private readonly AudioPlaybackService _audioPlaybackService = new();
    private readonly Border _messageEndSpacer = CreateMessageEndSpacer();

    // Windowを閉じた時に、実行中のHTTP通信やSignalR接続へキャンセルを伝えるためのものです。
    // 非同期処理が画面破棄後も残ると、例外や不要な通信の原因になります。
    private readonly CancellationTokenSource _windowClosingTokenSource = new();

    // Backendで作られた会話セッションIDです。
    // 音声アップロード、履歴取得、SignalR通知のフィルタリングで同じIDを使います。
    private Guid? _conversationId;

    // PointerPressedが連続して呼ばれても二重録音にならないようにするフラグです。
    private bool _isRecording;

    public MainWindow()
        : this(DesktopAppSettings.Load())
    {
    }

    public MainWindow(DesktopAppSettings settings)
    {
        InitializeComponent();

        _backendApiClient = new BackendApiClient(settings.BackendBaseUrl);
        _conversationHubClient = new ConversationHubClient(settings.BackendBaseUrl);
        MessagesPanel.Children.Add(_messageEndSpacer);
        MessagesScrollViewer.LayoutUpdated += MessagesScrollViewer_LayoutUpdated;

        AddSystemMessage("アプリを起動しました。Backendへ接続します。");

        // Windowが表示された後にBackend接続を始めます。
        // コンストラクタ内で待ち処理をすると、画面表示が遅くなるためです。
        RegisterPushToTalkHandlers();
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
        MessagesScrollViewer.LayoutUpdated -= MessagesScrollViewer_LayoutUpdated;
        _audioRecorder.Dispose();
        _backendApiClient.Dispose();
        await _conversationHubClient.DisposeAsync();
    }

    private async void RefreshButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 履歴更新は「DBに保存された現在の状態」を取り直す操作です。
        // SignalR通知を取り逃した場合でも、このボタンでBackend側の最終状態を確認できます。
        await RefreshConversationTurnsAsync();
    }

    private void RegisterPushToTalkHandlers()
    {
        // AvaloniaのButtonは内部でPointerイベントを処理済みにすることがあります。
        // AddHandler(... handledEventsToo: true) を使うと、処理済みになったイベントも受け取れます。
        PushToTalkButton.AddHandler(
            InputElement.PointerPressedEvent,
            PushToTalkButton_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        PushToTalkButton.AddHandler(
            InputElement.PointerReleasedEvent,
            PushToTalkButton_PointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void PushToTalkButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isRecording)
        {
            return;
        }

        // ボタンの外で指やマウスを離しても終了イベントを拾えるように、
        // 押下中はポインターをこのボタンに捕まえておきます。
        e.Pointer.Capture(PushToTalkButton);

        // PushToTalk は「押している間だけ録音」する操作です。
        // 録音開始時点でマイク権限や入力デバイスの問題があれば、ここで分かるようにします。
        try
        {
            _audioRecorder.Start();
            _isRecording = true;
        }
        catch (Exception ex)
        {
            e.Pointer.Capture(null);
            StatusText.Text = "録音開始失敗";
            InputHintText.Text = "macOSのマイク権限、または入力デバイスの状態を確認してください。";
            AddSystemMessage($"録音を開始できませんでした: {ex.Message}");
            return;
        }

        InputHintText.Text = "録音中...";
        StatusText.Text = "音声入力中";
        AddSystemMessage("録音を開始しました。");
    }

    private async void PushToTalkButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isRecording)
        {
            return;
        }

        e.Pointer.Capture(null);

        _isRecording = false;
        InputHintText.Text = "音声を送信しています...";
        StatusText.Text = "音声送信中";
        var shouldRefreshTurns = false;
        RecordedAudio audio;

        try
        {
            // 録音停止は、Backend通信より先に必ず行います。
            // 先に会話作成や通信を行うと、そこで失敗した時に録音デバイスが開いたまま残ってしまいます。
            audio = _audioRecorder.Stop();
        }
        catch (Exception)
        {
            StatusText.Text = "録音保存失敗";
            InputHintText.Text = "録音データを作成できませんでした。少し長めに押すか、マイク権限を確認してください。";
            // 詳細な原因は下部の状態欄で表示します。
            // チャット欄には短い結果だけを置き、長い例外文でカードが画面を占有しないようにします。
            AddSystemMessage("録音データを作成できませんでした。");
            return;
        }

        try
        {
            if (_conversationId is null)
            {
                await CreateConversationAsync();
            }

            if (_conversationId is null)
            {
                AddSystemMessage("会話セッションがないため、音声を送信できませんでした。");
                return;
            }

            // 音声ファイルはRESTでBackendへ送ります。
            // 文字起こし結果やAI返答など、処理途中の変化はSignalRで別途受け取ります。
            // ここから先はBackend側にターンが作られる可能性があるため、失敗しても履歴更新します。
            shouldRefreshTurns = true;
            await _backendApiClient.UploadAudioTurnAsync(
                _conversationId.Value,
                audio,
                _windowClosingTokenSource.Token);

            StatusText.Text = "音声送信完了";
            InputHintText.Text = "Backendへ音声を送信しました。";
            AddSystemMessage("録音音声をBackendへ送信しました。");
        }
        catch (Exception ex)
        {
            // STT APIが未設定の場合、Backendは502を返します。
            // その場合もBackend側には失敗ターンが保存されるため、履歴更新で確認できます。
            StatusText.Text = "Backend処理失敗";
            InputHintText.Text = shouldRefreshTurns
                ? "履歴更新でBackend側の状態を確認できます。"
                : "Backendへ音声を送信できませんでした。";
            AddSystemMessage($"Backendで音声処理に失敗しました: {ex.Message}");
        }

        if (shouldRefreshTurns)
        {
            await RefreshConversationTurnsAsync();
        }
    }

    private async Task CreateConversationAsync()
    {
        try
        {
            SetBusyState("Backend接続中...");

            // このアプリでは認証がないため、起動時に新しい会話セッションを作成します。
            // 返ってきたconversationIdを以降のREST/SignalR表示フィルタに使います。
            _conversationId = await _backendApiClient.CreateConversationAsync(
                "Avalonia音声対話",
                _windowClosingTokenSource.Token);

            StatusText.Text = $"Backend接続済み / Conversation: {_conversationId}";
            InputHintText.Text = "PushToTalkで話す準備ができています。";
            AddSystemMessage("Backendに会話セッションを作成しました。");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Backend接続失敗";
            InputHintText.Text = "Backendを起動してから履歴更新を押してください。";
            AddSystemMessage($"Backendへ接続できませんでした: {ex.Message}");
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
            AddSystemMessage("SignalRに接続しました。処理状態をリアルタイムに受け取れます。");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"SignalRへ接続できませんでした: {ex.Message}");
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

                AddAssistantMessage(value.AssistantText, value.TurnId);
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

                AddSystemMessage($"返答音声の生成が完了しました。AudioId: {value.AudioId}");
                StatusText.Text = "返答音声生成完了";
                InputHintText.Text = "返答音声を再生しています。";

                await DownloadAndPlayAudioAsync(value.AudioId);
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

                AddSystemMessage($"処理に失敗しました: {FormatStage(value.Stage)} / {value.Message}");
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

        // Backendは upload / stt / gemini / tts のような細かい状態を通知します。
        // 初期UIではその詳細をシステムメッセージとして見せ、動作確認しやすくしています。
        var stageText = FormatStage(value.Stage);
        var eventText = FormatEventType(value.EventType);
        var message = string.IsNullOrWhiteSpace(value.Message)
            ? $"{stageText}: {eventText}"
            : $"{stageText}: {eventText} - {value.Message}";

        StatusText.Text = message;
        InputHintText.Text = stageText switch
        {
            "STT" => "音声を文字起こししています。",
            "Gemini" => "AI返答を生成しています。",
            "TTS" => "返答音声を生成しています。",
            _ => message
        };

        AddSystemMessage(message);
    }

    private async Task DownloadAndPlayAudioAsync(Guid audioId)
    {
        try
        {
            var audioBytes = await _backendApiClient.DownloadAudioAsync(
                audioId,
                _windowClosingTokenSource.Token);

            await _audioPlaybackService.PlayWavAsync(
                audioBytes,
                _windowClosingTokenSource.Token);

            StatusText.Text = "返答音声再生完了";
            InputHintText.Text = "PushToTalkで次の発話ができます。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "返答音声再生失敗";
            InputHintText.Text = "音声ファイルを取得または再生できませんでした。";
            AddSystemMessage($"返答音声を再生できませんでした: {ex.Message}");
        }
    }

    private bool IsCurrentConversation(Guid conversationId)
    {
        // SignalRは現在Allクライアントへ通知しています。
        // そのため、自分が開いている会話IDの通知だけを画面に反映します。
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

    private static string FormatTurnStatus(TurnStatus status)
    {
        return status switch
        {
            TurnStatus.Processing => "処理中",
            TurnStatus.Completed => "完了",
            TurnStatus.Failed => "失敗",
            _ => status.ToString()
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
            ReplaceMessagesFromTurns(turns);
            StatusText.Text = $"Backend接続済み / {turns.Count}ターン";
            InputHintText.Text = "履歴を更新しました。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "履歴取得失敗";
            InputHintText.Text = "Backendの状態を確認してください。";
            AddSystemMessage($"履歴を取得できませんでした: {ex.Message}");
        }
    }

    private void ReplaceMessagesFromTurns(IReadOnlyList<ConversationTurnDto> turns)
    {
        // SignalRで先に届いた発話は、履歴取得の時点ではまだDBへ反映されていないことがあります。
        // 再描画前にターンID付きの表示を退避し、DBの項目が空ならその表示を戻します。
        var displayedConversationMessages = _messages
            .Where(x => x.IsConversationMessage && x.TurnId is not null)
            .ToList();

        _messages.Clear();
        MessagesPanel.Children.Clear();
        MessagesPanel.Children.Add(_messageEndSpacer);

        if (turns.Count == 0)
        {
            if (displayedConversationMessages.Count > 0)
            {
                foreach (var message in displayedConversationMessages)
                {
                    AddMessageToPanel(message);
                }

                return;
            }

            AddSystemMessage("この会話にはまだ発話がありません。");
            return;
        }

        foreach (var turn in turns)
        {
            var displayedUserText = displayedConversationMessages
                .FirstOrDefault(x => x.TurnId == turn.Id && x.Speaker == "ユーザー")
                ?.Text;
            var displayedAssistantText = displayedConversationMessages
                .FirstOrDefault(x => x.TurnId == turn.Id && x.Speaker == "アシスタント")
                ?.Text;

            var userText = string.IsNullOrWhiteSpace(turn.UserText)
                ? displayedUserText
                : turn.UserText;
            var assistantText = string.IsNullOrWhiteSpace(turn.AssistantText)
                ? displayedAssistantText
                : turn.AssistantText;

            if (!string.IsNullOrWhiteSpace(userText))
            {
                AddUserMessage(userText, turn.Id);
            }

            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                AddAssistantMessage(assistantText, turn.Id);
            }

            // STTで失敗した場合などは、ユーザー発話テキストもAI返答テキストもまだ入っていません。
            // その場合でも「ターンは作られている」と分かるように、履歴更新時に状態を表示します。
            if (string.IsNullOrWhiteSpace(userText)
                && string.IsNullOrWhiteSpace(assistantText)
                && turn.ErrorMessage is null)
            {
                AddSystemMessage($"ターン状態: {FormatTurnStatus(turn.Status)}");
            }

            if (turn.ErrorMessage is not null)
            {
                var stageText = turn.ErrorStage is null ? "不明" : FormatStage(turn.ErrorStage.Value);
                AddSystemMessage($"エラー: {stageText} / {turn.ErrorMessage}");
            }
        }
    }

    private void SetBusyState(string text)
    {
        StatusText.Text = text;
        InputHintText.Text = text;
    }

    private void AddUserMessage(string text, Guid? turnId = null)
    {
        AddMessageToPanel(ChatMessageItem.User(text, turnId));
    }

    private void AddSystemMessage(string text)
    {
        AddMessageToPanel(ChatMessageItem.System(text));
    }

    private void AddAssistantMessage(string text, Guid? turnId = null)
    {
        AddMessageToPanel(ChatMessageItem.Assistant(text, turnId));
    }

    private void AddMessageToPanel(ChatMessageItem message)
    {
        _messages.Add(message);
        // 終端領域を常に最後に置くため、新しいカードはその直前へ挿入します。
        var spacerIndex = Math.Max(0, MessagesPanel.Children.Count - 1);
        MessagesPanel.Children.Insert(spacerIndex, CreateMessageCard(message));
        ScrollToLatestMessage();
    }

    private static Border CreateMessageEndSpacer()
    {
        return new Border
        {
            // 初期値は0にし、レイアウト後に最後尾カードの不足量から決めます。
            Height = 0,
            IsHitTestVisible = false
        };
    }

    private void MessagesScrollViewer_LayoutUpdated(object? sender, EventArgs e)
    {
        var lastCard = MessagesPanel.Children
            .OfType<Border>()
            .LastOrDefault(card => !ReferenceEquals(card, _messageEndSpacer));

        if (lastCard is null)
        {
            return;
        }

        var lastCardBottom = lastCard.TranslatePoint(
            new Point(0, lastCard.Bounds.Height),
            MessagesScrollViewer)?.Y;

        if (lastCardBottom is null)
        {
            return;
        }

        // カードの位置はScrollViewerの表示座標、Extentはスクロール内容の座標です。
        // Offsetを足して同じ内容座標へそろえます。
        var lastCardBottomInContent = lastCardBottom.Value + MessagesScrollViewer.Offset.Y;

        // 現在の終端領域を除いた、一覧が認識している終端を求めます。
        // ここへ最後尾カードが収まっていなければ、その不足分を終端領域へ加えます。
        var currentSpacerHeight = _messageEndSpacer.Height;
        var contentEndWithoutSpacer = MessagesScrollViewer.Extent.Height - currentSpacerHeight;
        var missingEndSpace = Math.Max(
            0,
            lastCardBottomInContent - contentEndWithoutSpacer);

        // missingEndSpaceは「現在の終端領域を除いた一覧」に対して必要な量です。
        // 現在値を足し戻すと、画面拡大時や履歴短縮時に余白が縮まらなくなります。
        var requiredSpacerHeight = missingEndSpace;
        if (Math.Abs(requiredSpacerHeight - currentSpacerHeight) < 0.5)
        {
            return;
        }

        _messageEndSpacer.Height = requiredSpacerHeight;
    }

    private static Border CreateMessageCard(ChatMessageItem message)
    {
        var messageStack = new StackPanel
        {
            Spacing = 6
        };

        messageStack.Children.Add(new TextBlock
        {
            Text = message.Speaker,
            FontSize = 12,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = message.SpeakerColor
        });

        messageStack.Children.Add(new TextBlock
        {
            Text = message.Text,
            FontSize = 15,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brush.Parse("#20242C")
        });

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(14, 12),
            Background = message.Background,
            BorderBrush = message.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MaxWidth = 720,
            HorizontalAlignment = message.Alignment,
            Child = messageStack
        };
    }

    private void ScrollToLatestMessage()
    {
        // メッセージが1件追加されるたびに、独立したスクロール要求を登録します。
        // 追加要求を一つにまとめると、先に追加されたメッセージのためのスクロールが
        // まだ古いレイアウトを見ている間に、後続メッセージの要求が捨てられる可能性があります。
        // メッセージ数が通常の会話規模であれば、正しく最新位置へ移動することを優先します。
        Dispatcher.UIThread.Post(() =>
        {
            MessagesScrollViewer.ScrollToEnd();
        }, DispatcherPriority.Render);
    }

}
