namespace STSApp.Backend.Services;

/// <summary>
/// 1ターン分の STT -> Gemini -> TTS の流れをまとめるサービスです。
/// ControllerやMinimal APIから直接外部APIを呼ばず、このサービスに集約します。
/// </summary>
public interface IConversationWorkflow
{
    Task<ConversationTurnProcessingResult> ProcessAudioTurnAsync(
        Guid conversationId,
        IFormFile audioFile,
        CancellationToken cancellationToken);
}

/// <summary>
/// 1ターンの処理結果です。
/// 出力音声IDも返すことで、SignalR通知を取り逃したDesktopがREST応答から音声を取得できます。
/// </summary>
public sealed record ConversationTurnProcessingResult(
    Guid TurnId,
    Guid OutputAudioId);
