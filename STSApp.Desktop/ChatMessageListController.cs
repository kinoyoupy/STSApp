using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using STSApp.Contracts.Enums;
using STSApp.Contracts.Models;

namespace STSApp.Desktop;

/// <summary>
/// チャット一覧のデータと見た目をまとめて管理します。
/// MainWindowからこの責任を分けることで、音声処理を読む時に
/// カード生成やスクロール計算の詳細を追わなくてよくなります。
/// </summary>
public sealed class ChatMessageListController : IDisposable
{
    private readonly StackPanel _messagesPanel;
    private readonly ScrollViewer _scrollViewer;
    private readonly ObservableCollection<ChatMessageItem> _messages = new();
    private readonly Dictionary<Guid, AssistantTextChunkBuffer> _assistantTextChunks = new();
    private readonly Border _messageEndSpacer = CreateMessageEndSpacer();

    public ChatMessageListController(
        StackPanel messagesPanel,
        ScrollViewer scrollViewer)
    {
        _messagesPanel = messagesPanel;
        _scrollViewer = scrollViewer;

        // 終端領域は常にカード一覧の最後に置きます。
        // Avaloniaが最後のカードをスクロール範囲へ含めきれない場合に、
        // 不足した分だけ余白を補ってカード全体を見えるようにするためです。
        _messagesPanel.Children.Add(_messageEndSpacer);
        _scrollViewer.LayoutUpdated += ScrollViewer_LayoutUpdated;

        // 起動直後にも、処理情報の代わりに「まだ会話がない」という空状態だけを表示します。
        // 最初のユーザー発話またはAI返答が追加されると、AddMessage内で取り除かれます。
        AddMessage(ChatMessageItem.EmptyState());
    }

    public void AddUserMessage(string text, Guid? turnId = null)
    {
        AddMessage(ChatMessageItem.User(text, turnId));
    }

    public void AddErrorMessage(string text, Guid? turnId = null)
    {
        // Backendのターンエラーは、SignalRと履歴取得の両方から届きます。
        // turnIdが同じエラーを重ねないことで、途中通知とDB履歴を同時に使っても1枚だけ表示します。
        if (turnId is not null
            && _messages.Any(message =>
                message.Kind == ChatMessageKind.Error
                && message.TurnId == turnId))
        {
            return;
        }

        AddMessage(ChatMessageItem.Error(text, turnId));
    }

    public void AddAssistantMessage(
        string text,
        Guid? turnId = null,
        AnswerBasis? answerBasis = null)
    {
        if (turnId is Guid id)
        {
            GetOrCreateAssistantTextBuffer(id).FinalizeText();
            UpsertAssistantMessage(text, id, answerBasis);
            return;
        }

        AddMessage(ChatMessageItem.Assistant(text, turnId, answerBasis));
    }

    public void AppendAssistantMessageChunk(Guid turnId, int sequence, string text)
    {
        var chunks = GetOrCreateAssistantTextBuffer(turnId);
        var assembledText = chunks.Add(sequence, text);
        if (assembledText is not null)
        {
            UpsertAssistantMessage(assembledText, turnId, null);
        }
    }

    public void ResetStreamingState()
    {
        _assistantTextChunks.Clear();
    }

    public void ClearDesktopErrors()
    {
        // Desktop内の接続・マイク・再生エラーはDBへ保存されないため、turnIdを持ちません。
        // その後に同じ処理が成功した時は、古い失敗を画面へ残すと現在も失敗中に見えるため消します。
        for (var index = _messages.Count - 1; index >= 0; index--)
        {
            var message = _messages[index];
            if (message.Kind != ChatMessageKind.Error || message.TurnId is not null)
            {
                continue;
            }

            _messages.RemoveAt(index);
            _messagesPanel.Children.RemoveAt(index);
        }

        if (_messages.Count == 0)
        {
            AddMessage(ChatMessageItem.EmptyState());
        }
    }

    public void ReplaceFromTurns(IReadOnlyList<ConversationTurnDto> turns)
    {
        // SignalRで届いた発話は、履歴取得の瞬間にはDBへ保存されていない場合があります。
        // 画面に出ている発話を一度退避し、DB側が空の時だけ補うことで、
        // 履歴更新によって直前の発話が一瞬消えることを防ぎます。
        var displayedConversationMessages = _messages
            .Where(message => message.IsConversationMessage && message.TurnId is not null)
            .ToList();
        var desktopErrors = _messages
            .Where(message =>
                message.Kind == ChatMessageKind.Error
                && message.TurnId is null)
            .ToList();

        _messages.Clear();
        _messagesPanel.Children.Clear();
        _messagesPanel.Children.Add(_messageEndSpacer);

        if (turns.Count == 0)
        {
            if (displayedConversationMessages.Count > 0)
            {
                foreach (var message in displayedConversationMessages)
                {
                    AddMessage(message);
                }

                RestoreDesktopErrors(desktopErrors);
                return;
            }

            AddMessage(ChatMessageItem.EmptyState());
            RestoreDesktopErrors(desktopErrors);
            return;
        }

        foreach (var turn in turns)
        {
            var displayedUserText = displayedConversationMessages
                .FirstOrDefault(message =>
                    message.TurnId == turn.Id
                    && message.Speaker == "ユーザー")
                ?.Text;
            var displayedAssistantText = displayedConversationMessages
                .FirstOrDefault(message =>
                    message.TurnId == turn.Id
                    && message.Speaker == "アシスタント")
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
                AddAssistantMessage(assistantText, turn.Id, turn.AnswerBasis);
            }

            if (turn.ErrorMessage is not null)
            {
                var stageText = turn.ErrorStage is null
                    ? "不明"
                    : FormatStage(turn.ErrorStage.Value);
                AddErrorMessage($"{stageText} / {turn.ErrorMessage}", turn.Id);
            }
        }

        // Backendに保存されないマイク・接続・再生エラーは、履歴更新後も現在の起動中だけ残します。
        RestoreDesktopErrors(desktopErrors);

        if (_messages.All(message => !message.IsConversationMessage))
        {
            AddMessage(ChatMessageItem.EmptyState());
        }
    }

    public void Dispose()
    {
        // Windowを閉じた後にレイアウト通知を受け続けると、
        // 破棄済みの画面へ触れる原因になるため、登録したイベントを解除します。
        _scrollViewer.LayoutUpdated -= ScrollViewer_LayoutUpdated;
    }

    private void AddMessage(ChatMessageItem message)
    {
        if (message.IsConversationMessage)
        {
            RemoveEmptyState();
        }

        _messages.Add(message);

        // 終端領域を常に最後に残すため、新しいカードはその直前へ挿入します。
        var spacerIndex = Math.Max(0, _messagesPanel.Children.Count - 1);
        _messagesPanel.Children.Insert(spacerIndex, CreateMessageCard(message));
        ScrollToLatestMessage();
    }

    private int FindAssistantMessageIndex(Guid turnId)
    {
        for (var index = 0; index < _messages.Count; index++)
        {
            var message = _messages[index];
            if (message.TurnId == turnId && message.Speaker == "アシスタント")
            {
                return index;
            }
        }

        return -1;
    }

    private AssistantTextChunkBuffer GetOrCreateAssistantTextBuffer(Guid turnId)
    {
        if (!_assistantTextChunks.TryGetValue(turnId, out var buffer))
        {
            buffer = new AssistantTextChunkBuffer();
            _assistantTextChunks.Add(turnId, buffer);
        }

        return buffer;
    }

    private void UpsertAssistantMessage(string text, Guid turnId, AnswerBasis? answerBasis)
    {
        var existingIndex = FindAssistantMessageIndex(turnId);
        if (existingIndex >= 0)
        {
            ReplaceMessageAt(existingIndex, ChatMessageItem.Assistant(text, turnId, answerBasis));
            return;
        }

        AddMessage(ChatMessageItem.Assistant(text, turnId, answerBasis));
    }

    private void ReplaceMessageAt(int index, ChatMessageItem message)
    {
        _messages[index] = message;
        _messagesPanel.Children[index] = CreateMessageCard(message);
        ScrollToLatestMessage();
    }

    private void RestoreDesktopErrors(IReadOnlyList<ChatMessageItem> desktopErrors)
    {
        foreach (var error in desktopErrors)
        {
            AddMessage(error);
        }
    }

    private void RemoveEmptyState()
    {
        var emptyState = _messages
            .FirstOrDefault(message => message.Kind == ChatMessageKind.EmptyState);
        if (emptyState is null)
        {
            return;
        }

        var emptyStateIndex = _messages.IndexOf(emptyState);
        _messages.RemoveAt(emptyStateIndex);
        _messagesPanel.Children.RemoveAt(emptyStateIndex);
    }

    private static Border CreateMessageEndSpacer()
    {
        return new Border
        {
            // 必要な高さはレイアウト後に実測するため、固定値は持たせません。
            Height = 0,
            IsHitTestVisible = false
        };
    }

    private void ScrollViewer_LayoutUpdated(object? sender, EventArgs e)
    {
        var lastCard = _messagesPanel.Children
            .OfType<Border>()
            .LastOrDefault(card => !ReferenceEquals(card, _messageEndSpacer));

        if (lastCard is null)
        {
            return;
        }

        var lastCardBottom = lastCard.TranslatePoint(
            new Point(0, lastCard.Bounds.Height),
            _scrollViewer)?.Y;

        if (lastCardBottom is null)
        {
            return;
        }

        // カード位置とスクロール範囲は基準が違うため、Offsetを足して
        // どちらも「一覧内容の先頭から何pxか」という同じ基準へそろえます。
        var lastCardBottomInContent = lastCardBottom.Value + _scrollViewer.Offset.Y;
        var currentSpacerHeight = _messageEndSpacer.Height;
        var contentEndWithoutSpacer = _scrollViewer.Extent.Height - currentSpacerHeight;
        var requiredSpacerHeight = Math.Max(
            0,
            lastCardBottomInContent - contentEndWithoutSpacer);

        // 小数点以下のごく小さな差で更新を繰り返すと、
        // レイアウト計算が終わらなくなるため0.5px未満は同じ値とみなします。
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

        if (message.AnswerBasis == AnswerBasis.GeneralKnowledge)
        {
            // 資料名や類似度は内部情報として保持し、画面には
            // VoiceLink資料を根拠にしていないことだけを短く伝えます。
            messageStack.Children.Add(new TextBlock
            {
                Text = "VoiceLink固有の資料を参照していない一般的な回答です。",
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = Avalonia.Media.Brush.Parse("#657085")
            });
        }

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
        // カードを追加した直後は、Avaloniaが一覧の新しい高さをまだ計算中です。
        // 描画直前まで待ってから移動することで、古い末尾位置へ止まることを防ぎます。
        Dispatcher.UIThread.Post(
            () => _scrollViewer.ScrollToEnd(),
            DispatcherPriority.Render);
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

}
