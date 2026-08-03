namespace STSApp.Backend.Services.External;

/// <summary>
/// Gemini API呼び出しの差し替え口です。
/// Interactions APIから受信したテキスト差分を順番に返します。
/// </summary>
public interface IGeminiClient
{
    IAsyncEnumerable<string> StreamReplyAsync(
        GeminiReplyRequest request,
        CancellationToken cancellationToken);
}
