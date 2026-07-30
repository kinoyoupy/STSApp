namespace STSApp.Backend.Services.Rag;

/// <summary>
/// 開発モック中、またはEmbedding設定が不足している時に使う明示的な失敗実装です。
/// 検索障害を一般回答で隠さないため、RAG段階で必ず失敗させます。
/// </summary>
public sealed class NotConfiguredEmbeddingClient : IEmbeddingClient
{
    public Task<float[]> EmbedDocumentAsync(string text, string title, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("RAG Embedding APIは設定されていません。UseDevelopmentMocks=false、Gemini APIキー、Embeddingモデル設定を確認してください。");
    }

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("RAG Embedding APIは設定されていません。資料検索を実行できません。");
    }
}
