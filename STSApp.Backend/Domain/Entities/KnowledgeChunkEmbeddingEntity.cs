namespace STSApp.Backend.Domain.Entities;

/// <summary>
/// 1チャンクを意味ベクトルへ変換した結果です。
/// MySQL 8.4には今回使うVector型がないため、JSON文字列として保存してBackendで比較します。
/// </summary>
public sealed class KnowledgeChunkEmbeddingEntity
{
    public long Id { get; init; }
    public long KnowledgeChunkId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public string VectorJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
