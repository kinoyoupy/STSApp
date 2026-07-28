using STSApp.Contracts.Enums;

namespace STSApp.Contracts.Models;

/// <summary>
/// 1回のユーザー発話とAI返答を表す表示用データです。
/// 詳細な処理履歴は TurnEventDto 側に分けます。
/// </summary>
public sealed record ConversationTurnDto(
    Guid Id,
    Guid ConversationId,
    string? UserText,
    string? AssistantText,
    TurnStatus Status,
    ProcessingStage? ErrorStage,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime UpdatedAt);
