namespace STSApp.Contracts.Responses;

/// <summary>
/// 音声対話の処理が完了した時に返すレスポンスです。
/// OutputAudioIdは、SignalR通知を取り逃した場合でも返答音声を取得できるように返します。
/// </summary>
public sealed record TurnCreatedResponse(
    Guid ConversationId,
    Guid TurnId,
    Guid OutputAudioId);
