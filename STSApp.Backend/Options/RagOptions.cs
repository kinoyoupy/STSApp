namespace STSApp.Backend.Options;

/// <summary>
/// VoiceLinkの資料検索に関する設定です。
/// APIキーは既存のExternalApis:Geminiだけで管理し、ここには検索の振る舞いだけを置きます。
/// </summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    // Backendプロジェクトから見た、取り込み対象Markdownの場所です。
    public string KnowledgeBasePath { get; init; } = "../Document/RagKnowledgeBase";
    public string EmbeddingModelName { get; init; } = "gemini-embedding-001";
    public int EmbeddingDimensions { get; init; } = 768;
    public double SimilarityThreshold { get; init; } = 0.70;
    public int MaxResults { get; init; } = 3;
    public int TimeoutSeconds { get; init; } = 60;
}
