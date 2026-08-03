namespace STSApp.Contracts.Events;

/// <summary>
/// Geminiから1文分の返答が確定した時に通知するイベントです。
/// Sequenceはターン内で0から始まる連番です。
/// </summary>
public sealed record AssistantTextChunkGeneratedEvent(
    Guid ConversationId,
    Guid TurnId,
    int Sequence,
    string Text);
