using STSApp.Contracts.Enums;

namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// turn_events テーブルに対応するエンティティです。
/// 状態変化やエラー履歴を時系列で残します。
/// </summary>
public sealed class TurnEventEntity
{
    public long Id { get; init; }
    public Guid ConversationTurnId { get; init; }
    public ProcessingStage Stage { get; init; }
    public TurnEventType EventType { get; init; }
    public string? Message { get; init; }
    public string? MetadataJson { get; init; }
    public int? DurationMs { get; init; }
    public DateTime OccurredAt { get; init; }
}
