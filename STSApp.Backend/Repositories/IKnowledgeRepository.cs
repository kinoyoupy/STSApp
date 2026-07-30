using STSApp.Backend.Services.Rag;

namespace STSApp.Backend.Repositories;

/// <summary>
/// 検索用ナレッジベースと、ターンごとの参照履歴を扱うDB窓口です。
/// 会話Repositoryとは更新の理由が違うため、責務を分けています。
/// </summary>
public interface IKnowledgeRepository
{
    Task<IReadOnlyList<IndexedKnowledgeDocument>> ListIndexedDocumentsAsync(CancellationToken cancellationToken);

    Task ApplyReindexAsync(
        IReadOnlyList<EmbeddedKnowledgeDocument> changedDocuments,
        IReadOnlyCollection<string> deletedSourcePaths,
        string embeddingModelName,
        int embeddingDimensions,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredKnowledgeVector>> ListSearchVectorsAsync(
        string embeddingModelName,
        int embeddingDimensions,
        CancellationToken cancellationToken);

    Task<int> CountAllEmbeddingsAsync(CancellationToken cancellationToken);

    Task AddTurnReferencesAsync(
        Guid turnId,
        IReadOnlyList<RetrievedKnowledgeChunk> references,
        CancellationToken cancellationToken);
}

/// <summary>
/// 検索に必要な資料本文とベクトルを、1行として読み出すための内部データです。
/// </summary>
public sealed record StoredKnowledgeVector(
    long KnowledgeChunkId,
    string DocumentTitle,
    string? ParentHeading,
    string Heading,
    string Content,
    string VectorJson);
