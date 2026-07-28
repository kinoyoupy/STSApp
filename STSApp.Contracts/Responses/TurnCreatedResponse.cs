namespace STSApp.Contracts.Responses;

/// <summary>
/// 音声アップロード後、Backend側で処理対象ターンを受け付けたことを返すレスポンスです。
/// 実際のSTT結果やAI返答はSignalRイベントでも通知します。
/// </summary>
public sealed record TurnCreatedResponse(Guid ConversationId, Guid TurnId);
