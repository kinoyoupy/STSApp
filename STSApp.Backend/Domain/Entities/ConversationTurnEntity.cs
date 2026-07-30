using STSApp.Contracts.Enums;

namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// conversation_turns テーブルに対応するエンティティです。
/// 1回のユーザー発話とAI返答、およびターン全体の現在状態を持ちます。
/// </summary>
public sealed class ConversationTurnEntity
{
    public Guid Id { get; init; }
    public Guid ConversationId { get; init; }
    public string? UserText { get; set; }
    public string? AssistantText { get; set; }
    public AnswerBasis? AnswerBasis { get; set; }
    public TurnStatus Status { get; set; }
    public ProcessingStage? ErrorStage { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
}
