using STSApp.Contracts.Enums;

namespace STSApp.Contracts.Events;

/// <summary>
/// STT中、Gemini応答生成中、TTS生成中など、内部状態の変化を通知するイベントです。
/// UIにそのまま細かく出す必要はありませんが、Avalonia側で状態をキャッチするために使います。
/// </summary>
public sealed record TurnStatusChangedEvent(
    // 通知を受け取る会話セッションのUUIDです。
    Guid ConversationId,
    // 通知を受け取るターンのUUIDです。
    Guid TurnId,
    ProcessingStage Stage,
    TurnEventType EventType,
    string? Message);
