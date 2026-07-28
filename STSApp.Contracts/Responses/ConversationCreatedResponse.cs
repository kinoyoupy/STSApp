namespace STSApp.Contracts.Responses;

/// <summary>
/// 会話セッション作成後に返す最小限のレスポンスです。
/// </summary>
public sealed record ConversationCreatedResponse(Guid ConversationId);
