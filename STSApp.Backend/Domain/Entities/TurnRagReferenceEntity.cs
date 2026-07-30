namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// あるAI返答を作る際に採用した資料チャンクの記録です。
/// 資料が後から更新・削除されても当時の根拠を追えるよう、本文などのスナップショットも保存します。
/// </summary>
public sealed class TurnRagReferenceEntity
{
    public long Id { get; init; }
    public Guid ConversationTurnId { get; set; }
    public long? KnowledgeChunkId { get; set; }
    public int RetrievalRank { get; set; }
    public decimal SimilarityScore { get; set; }
    public string DocumentTitleSnapshot { get; set; } = string.Empty;
    public string? ParentHeadingSnapshot { get; set; }
    public string HeadingSnapshot { get; set; } = string.Empty;
    public string ContentSnapshot { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
