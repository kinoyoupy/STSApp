namespace STSApp.Backend.Services.External;

/// <summary>
/// 実Gemini APIを設定する前に、AI返答生成後のUI表示やDB保存を確認するための開発用Geminiです。
/// </summary>
public sealed class DevelopmentMockGeminiClient : IGeminiClient
{
    public Task<string> GenerateReplyAsync(
        string userText,
        IReadOnlyList<(string UserText, string AssistantText)> recentTurns,
        CancellationToken cancellationToken)
    {
        // recentTurns は直近履歴です。
        // ここでは件数だけ返答に含め、履歴を受け取れていることを画面上でも確認しやすくします。
        var reply = $"開発用Geminiの返答です。ユーザー発話「{userText}」を受け取りました。直近履歴は{recentTurns.Count}件です。";
        return Task.FromResult(reply);
    }
}
