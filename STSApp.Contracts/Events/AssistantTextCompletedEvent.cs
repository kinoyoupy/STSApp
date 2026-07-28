namespace STSApp.Contracts.Events;

/// <summary>
/// Geminiの返答テキスト生成が完了した時に、BackendからAvaloniaへ通知するイベントです。
/// </summary>
public sealed record AssistantTextCompletedEvent(
    Guid ConversationId,
    Guid TurnId,
    string AssistantText);
