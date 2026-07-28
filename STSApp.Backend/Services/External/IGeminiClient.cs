namespace STSApp.Backend.Services.External;

/// <summary>
/// Gemini API呼び出しの差し替え口です。
/// 具体的なHTTP仕様は、Geminiモデル名やAPIキー管理方法が確定してから実装します。
/// </summary>
public interface IGeminiClient
{
    // 実HTTPクライアントと開発用モックが同じ形で使えるようにしています。
    // Workflowは「Geminiが実APIかモックか」を意識しません。
    Task<string> GenerateReplyAsync(
        string userText,
        IReadOnlyList<(string UserText, string AssistantText)> recentTurns,
        CancellationToken cancellationToken);
}
