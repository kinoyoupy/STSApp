namespace STSApp.Contracts.Events;

/// <summary>
/// TTS音声の生成が完了した時に、BackendからAvaloniaへ通知するイベントです。
/// audioId は audio_files.id に対応します。
/// </summary>
public sealed record SpeechSynthesisCompletedEvent(
    Guid ConversationId,
    Guid TurnId,
    Guid AudioId);
