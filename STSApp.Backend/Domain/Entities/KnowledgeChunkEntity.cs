namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// 資料を検索しやすい小ささへ分割した1区画です。
/// 1資料を丸ごとEmbeddingすると質問と無関係な文も混ざるため、見出し単位で保存します。
/// </summary>
public sealed class KnowledgeChunkEntity
{
    public long Id { get; init; }
    public long KnowledgeDocumentId { get; set; }
    public string? ParentHeading { get; set; }
    public string Heading { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int ChunkOrder { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
}
