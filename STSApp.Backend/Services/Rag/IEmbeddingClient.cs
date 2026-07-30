namespace STSApp.Backend.Services.Rag;

/// <summary>
/// テキストを意味ベクトルへ変換する外部APIの差し替え口です。
/// 生成用Geminiとは役割が異なるため、インターフェースも分けます。
/// </summary>
public interface IEmbeddingClient
{
    Task<float[]> EmbedDocumentAsync(string text, string title, CancellationToken cancellationToken);

    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken);
}
