namespace STSApp.Contracts.Events;

/// <summary>
/// STTによる文字起こしが完了した時に、BackendからAvaloniaへ通知するイベントです。
/// </summary>
public sealed record TranscriptionCompletedEvent(
    Guid ConversationId,
    Guid TurnId,
    string UserText);
