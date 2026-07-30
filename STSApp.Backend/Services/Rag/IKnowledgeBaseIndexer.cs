namespace STSApp.Backend.Services.Rag;

/// <summary>
/// Markdown資料を読み、変更分だけEmbeddingしてDBへ反映する処理です。
/// </summary>
public interface IKnowledgeBaseIndexer
{
    Task<RagReindexResult> ReindexAsync(CancellationToken cancellationToken);
}
