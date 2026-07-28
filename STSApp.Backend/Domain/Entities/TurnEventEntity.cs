using STSApp.Contracts.Enums;

namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// turn_events テーブルに対応するエンティティです。
/// 状態変化やエラー履歴を時系列で残します。
/// </summary>
public sealed class TurnEventEntity
{
    // 内部ログが増えることを想定したBIGINTの連番です。
    public long Id { get; init; }
    // どの会話ターンのイベントかを示します。
    public Guid ConversationTurnId { get; init; }
    // upload/stt/gemini/tts/databaseのどの段階かを示します。
    public ProcessingStage Stage { get; init; }
    // started/completed/failed/infoのどの種類かを示します。
    public TurnEventType EventType { get; init; }
    public string? Message { get; init; }
    public string? MetadataJson { get; init; }
    public int? DurationMs { get; init; }
    public DateTime OccurredAt { get; init; }
}
