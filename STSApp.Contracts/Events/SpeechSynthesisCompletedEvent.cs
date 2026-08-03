namespace STSApp.Contracts.Events;

/// <summary>
/// 1ターン分のTTS音声がすべて生成された時に通知するイベントです。
/// </summary>
public sealed record SpeechSynthesisCompletedEvent(
    Guid ConversationId,
    Guid TurnId,
    IReadOnlyList<Guid> AudioIds);
