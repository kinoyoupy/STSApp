namespace STSApp.Backend.Services.External;

/// <summary>
/// Gemini API呼び出しの差し替え口です。
/// 具体的なHTTP仕様は、Geminiモデル名やAPIキー管理方法が確定してから実装します。
/// </summary>
public interface IGeminiClient
{
    Task<string> GenerateReplyAsync(GeminiReplyRequest request, CancellationToken cancellationToken);
}
