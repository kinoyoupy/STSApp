namespace STSApp.Backend.Services.External;

/// <summary>
/// Gemini API詳細が未設定の間に使うプレースホルダーです。
/// 実装前チェックリストの情報が揃ったら、HTTP実装へ差し替えます。
/// </summary>
public sealed class NotConfiguredGeminiClient : IGeminiClient
{
    public Task<string> GenerateReplyAsync(
        string userText,
        IReadOnlyList<(string UserText, string AssistantText)> recentTurns,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException("Gemini API settings are not configured yet.");
    }
}
