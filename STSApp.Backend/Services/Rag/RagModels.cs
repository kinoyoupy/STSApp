using STSApp.Contracts.Enums;

namespace STSApp.Backend.Services.Rag;

/// <summary>
/// ファイルから読み込んだ、まだDBへ保存していない資料です。
/// </summary>
public sealed record KnowledgeSourceDocument(
    string SourcePath,
    string Title,
    string SourceHash,
    IReadOnlyList<KnowledgeChunkDraft> Chunks);

/// <summary>
/// Markdownの見出し単位で切り出した、Embedding前の本文です。
/// </summary>
public sealed record KnowledgeChunkDraft(
    string? ParentHeading,
    string Heading,
    string Content,
    int ChunkOrder,
    string ContentHash);

/// <summary>
/// DBにある資料ファイルの同期判定に必要な最小情報です。
/// </summary>
public sealed record IndexedKnowledgeDocument(long Id, string SourcePath, string SourceHash);

/// <summary>
/// Embeddingまで完了した、DB反映用の資料データです。
/// </summary>
public sealed record EmbeddedKnowledgeDocument(
    KnowledgeSourceDocument Source,
    IReadOnlyList<EmbeddedKnowledgeChunk> Chunks);

/// <summary>
/// 正規化済みベクトルを持つチャンクです。
/// </summary>
public sealed record EmbeddedKnowledgeChunk(
    KnowledgeChunkDraft Draft,
    float[] NormalizedVector);

/// <summary>
/// 検索結果としてGeminiへ渡す資料です。画面には出さず、内部処理と監査用に使います。
/// </summary>
public sealed record RetrievedKnowledgeChunk(
    long KnowledgeChunkId,
    string DocumentTitle,
    string? ParentHeading,
    string Heading,
    string Content,
    double SimilarityScore,
    int RetrievalRank);

/// <summary>
/// 1回のRAG検索結果です。0件は資料に近い内容がないという正常な結果です。
/// </summary>
public sealed record RagSearchResult(IReadOnlyList<RetrievedKnowledgeChunk> References)
{
    public AnswerBasis AnswerBasis => References.Count > 0
        ? AnswerBasis.KnowledgeGrounded
        : AnswerBasis.GeneralKnowledge;
}

/// <summary>
/// 開発用の再インデックスAPIが返す作業結果です。
/// </summary>
public sealed record RagReindexResult(
    int ScannedDocumentCount,
    int ChangedDocumentCount,
    int SkippedDocumentCount,
    int DeletedDocumentCount,
    int EmbeddedChunkCount);
