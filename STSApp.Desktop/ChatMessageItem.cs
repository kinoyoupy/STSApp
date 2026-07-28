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

    // 画面上部に表示する「ユーザー」「アシスタント」「システム」のラベルです。
    public string Speaker { get; }
    // 実際に吹き出しへ表示する本文です。
    public string Text { get; }
    // 発話者ごとの背景色と枠線です。UIの見た目だけに関係します。
    public IBrush Background { get; }
    public IBrush BorderBrush { get; }
    public IBrush SpeakerColor { get; }
    // ユーザー発話を右、AI返答やシステム通知を左に寄せるための値です。
    public HorizontalAlignment Alignment { get; }
    // 履歴更新時に会話ターンとして扱うかを区別します。
    public bool IsConversationMessage { get; }
    // Backendのターンと画面カードを結びつけるIDです。
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
