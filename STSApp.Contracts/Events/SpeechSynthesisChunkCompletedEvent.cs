namespace STSApp.Contracts.Events;

/// <summary>
/// 1文分のTTS音声が生成された時に通知するイベントです。
/// AudioIdはaudio_files.id、Sequenceは再生順に対応します。
/// </summary>
public sealed record SpeechSynthesisChunkCompletedEvent(
    Guid ConversationId,
    Guid TurnId,
    int Sequence,
    Guid AudioId);
