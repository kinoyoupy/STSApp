using Avalonia.Layout;
using Avalonia.Media;
using System;
using STSApp.Contracts.Enums;

namespace STSApp.Desktop;

/// <summary>
/// チャットUIへ1件分のメッセージを表示するためのデータです。
/// Backendの会話データそのものではなく、画面表示用に色や配置も持たせています。
/// </summary>
public sealed class ChatMessageItem
{
    public ChatMessageItem(
        string speaker,
        string text,
        IBrush background,
        IBrush borderBrush,
        IBrush speakerColor,
        HorizontalAlignment alignment,
        ChatMessageKind kind,
        Guid? turnId,
        AnswerBasis? answerBasis = null)
    {
        Speaker = speaker;
        Text = text;
        Background = background;
        BorderBrush = borderBrush;
        SpeakerColor = speakerColor;
        Alignment = alignment;
        Kind = kind;
        TurnId = turnId;
        AnswerBasis = answerBasis;
    }

    public string Speaker { get; }
    public string Text { get; }
    public IBrush Background { get; }
    public IBrush BorderBrush { get; }
    public IBrush SpeakerColor { get; }
    public HorizontalAlignment Alignment { get; }
    public ChatMessageKind Kind { get; }
    public bool IsConversationMessage => Kind == ChatMessageKind.Conversation;
    public Guid? TurnId { get; }
    public AnswerBasis? AnswerBasis { get; }

    public static ChatMessageItem User(string text, Guid? turnId = null)
    {
        return new ChatMessageItem(
            "ユーザー",
            text,
            Brush.Parse("#E8F1FF"),
            Brush.Parse("#B9D4FF"),
            Brush.Parse("#245A9F"),
            HorizontalAlignment.Right,
            kind: ChatMessageKind.Conversation,
            turnId: turnId);
    }

    public static ChatMessageItem Error(string text, Guid? turnId = null)
    {
        return new ChatMessageItem(
            "エラー",
            text,
            Brush.Parse("#FFF4F4"),
            Brush.Parse("#E8B4B4"),
            Brush.Parse("#A33A3A"),
            HorizontalAlignment.Left,
            kind: ChatMessageKind.Error,
            turnId: turnId);
    }

    public static ChatMessageItem EmptyState()
    {
        return new ChatMessageItem(
            "システム",
            "この会話にはまだ発話がありません。",
            Brush.Parse("#FFFFFF"),
            Brush.Parse("#D9DEE7"),
            Brush.Parse("#657085"),
            HorizontalAlignment.Left,
            kind: ChatMessageKind.EmptyState,
            turnId: null);
    }

    public static ChatMessageItem Assistant(
        string text,
        Guid? turnId = null,
        AnswerBasis? answerBasis = null)
    {
        return new ChatMessageItem(
            "アシスタント",
            text,
            Brush.Parse("#F1F8F4"),
            Brush.Parse("#C7E7D2"),
            Brush.Parse("#2D7A4D"),
            HorizontalAlignment.Left,
            kind: ChatMessageKind.Conversation,
            turnId: turnId,
            answerBasis: answerBasis);
    }
}

/// <summary>
/// 履歴更新時に「DBから復元するもの」と「Desktop内だけで保持するもの」を区別するための種類です。
/// </summary>
public enum ChatMessageKind
{
    Conversation,
    Error,
    EmptyState
}
