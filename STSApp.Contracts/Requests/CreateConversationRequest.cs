namespace STSApp.Contracts.Requests;

/// <summary>
/// 新しい会話セッションを作る時のリクエストです。
/// title を省略した場合、Backend側で既定のタイトルを付ける想定です。
/// </summary>
public sealed record CreateConversationRequest(string? Title);
