namespace STSApp.Backend.Services.Rag;

/// <summary>
/// ユーザーの発話をEmbeddingし、近い資料チャンクを探すサービスです。
/// </summary>
public interface IKnowledgeSearchService
{
    Task<RagSearchResult> SearchAsync(string userText, CancellationToken cancellationToken);
}
