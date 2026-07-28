using STSApp.Contracts.Enums;

namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// conversation_turns テーブルに対応するエンティティです。
/// 1回のユーザー発話とAI返答、およびターン全体の現在状態を持ちます。
/// </summary>
public sealed class ConversationTurnEntity
{
    // PushToTalk 1回分を識別するUUIDです。
    public Guid Id { get; init; }
    // 所属する会話セッションのUUIDです。
    public Guid ConversationId { get; init; }
    // STTが成功した後に保存されるユーザーの文字です。
    public string? UserText { get; set; }
    // Geminiが成功した後に保存されるAIの文字です。
    public string? AssistantText { get; set; }
    // ターン全体の現在状態です。詳細履歴はTurnEventEntityへ分けます。
    public TurnStatus Status { get; set; }
    // 失敗した場合だけ、失敗した処理段階を持ちます。
    public ProcessingStage? ErrorStage { get; set; }
    // 失敗の理由を調査・表示するための文章です。
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
}
