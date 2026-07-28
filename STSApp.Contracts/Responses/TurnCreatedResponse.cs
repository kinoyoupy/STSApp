namespace STSApp.Contracts.Responses;

/// <summary>
/// 音声アップロード後、Backend側で処理対象ターンを受け付けたことを返すレスポンスです。
/// 実際のSTT結果やAI返答はSignalRイベントでも通知します。
/// </summary>
public sealed record TurnCreatedResponse(
    // 送信先の会話セッションを識別します。
    Guid ConversationId,
    // Backendが作成した処理単位を識別します。
    // STT/Gemini/TTSのSignalR通知も、このIDで同じ発話に紐づきます。
    Guid TurnId);
