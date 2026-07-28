using Avalonia.Layout;
using Avalonia.Media;
using System;

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
        bool isConversationMessage,
        Guid? turnId)
    {
        Speaker = speaker;
        Text = text;
        Background = background;
        BorderBrush = borderBrush;
        SpeakerColor = speakerColor;
        Alignment = alignment;
        IsConversationMessage = isConversationMessage;
        TurnId = turnId;
    }

    public string Speaker { get; }
    public string Text { get; }
    public IBrush Background { get; }
    public IBrush BorderBrush { get; }
    public IBrush SpeakerColor { get; }
    public HorizontalAlignment Alignment { get; }
    public bool IsConversationMessage { get; }
    public Guid? TurnId { get; }

    public static ChatMessageItem User(string text, Guid? turnId = null)
    {
        return new ChatMessageItem(
            "ユーザー",
            text,
            Brush.Parse("#E8F1FF"),
            Brush.Parse("#B9D4FF"),
            Brush.Parse("#245A9F"),
            HorizontalAlignment.Right,
            isConversationMessage: true,
            turnId: turnId);
    }

    public static ChatMessageItem System(string text)
    {
        return new ChatMessageItem(
            "システム",
            text,
            Brush.Parse("#FFFFFF"),
            Brush.Parse("#D9DEE7"),
            Brush.Parse("#657085"),
            HorizontalAlignment.Left,
            isConversationMessage: false,
            turnId: null);
    }

    public static ChatMessageItem Assistant(string text, Guid? turnId = null)
    {
        return new ChatMessageItem(
            "アシスタント",
            text,
            Brush.Parse("#F1F8F4"),
            Brush.Parse("#C7E7D2"),
            Brush.Parse("#2D7A4D"),
            HorizontalAlignment.Left,
            isConversationMessage: true,
            turnId: turnId);
    }
}
