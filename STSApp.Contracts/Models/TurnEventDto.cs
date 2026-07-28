using STSApp.Contracts.Enums;

namespace STSApp.Contracts.Models;

/// <summary>
/// ターン内で起きた状態変化やエラー履歴です。
/// 保守・調査のため、SignalRで通知する情報と近い形で保存します。
/// </summary>
public sealed record TurnEventDto(
    long Id,
    Guid ConversationTurnId,
    ProcessingStage Stage,
    TurnEventType EventType,
    string? Message,
    string? MetadataJson,
    int? DurationMs,
    DateTime OccurredAt);
