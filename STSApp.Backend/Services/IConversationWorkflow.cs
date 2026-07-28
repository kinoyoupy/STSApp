using STSApp.Contracts.Models;

namespace STSApp.Backend.Services;

/// <summary>
/// 1ターン分の STT -> Gemini -> TTS の流れをまとめるサービスです。
/// ControllerやMinimal APIから直接外部APIを呼ばず、このサービスに集約します。
/// </summary>
public interface IConversationWorkflow
{
    Task<ConversationTurnDto> ProcessAudioTurnAsync(
        Guid conversationId,
        IFormFile audioFile,
        CancellationToken cancellationToken);
}
