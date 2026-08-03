namespace STSApp.Backend.Services.External;

/// <summary>
/// Gemini API詳細が未設定の間に使うプレースホルダーです。
/// 実装前チェックリストの情報が揃ったら、HTTP実装へ差し替えます。
/// </summary>
public sealed class NotConfiguredGeminiClient : IGeminiClient
{
    public async IAsyncEnumerable<string> StreamReplyAsync(
        GeminiReplyRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        throw new NotImplementedException("Gemini API settings are not configured yet.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
