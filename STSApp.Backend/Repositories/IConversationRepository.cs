using STSApp.Contracts.Models;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Repositories;

/// <summary>
/// 会話セッションとターンをDBから読み書きするための窓口です。
/// API層がDbContextを直接触りすぎないように、DB操作をここへ集めます。
/// </summary>
public interface IConversationRepository
{
    Task<ConversationDto> CreateConversationAsync(string? title, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConversationDto>> ListConversationsAsync(CancellationToken cancellationToken);

    Task<bool> ConversationExistsAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConversationTurnDto>> ListConversationTurnsAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    Task<ConversationTurnDto> CreateProcessingTurnAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    Task AddTurnEventAsync(
        Guid turnId,
        ProcessingStage stage,
        TurnEventType eventType,
        string? message,
        int? durationMs,
        CancellationToken cancellationToken);

    Task<AudioFileDto> AddAudioFileAsync(
        Guid turnId,
        AudioFileKind kind,
        string filePath,
        string mimeType,
        long? fileSizeBytes,
        CancellationToken cancellationToken);

    Task<AudioFileDto?> GetAudioFileAsync(
        Guid audioId,
        CancellationToken cancellationToken);

    Task UpdateUserTextAsync(
        Guid turnId,
        string userText,
        CancellationToken cancellationToken);

    Task UpdateAssistantTextAsync(
        Guid turnId,
        string assistantText,
        AnswerBasis answerBasis,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<(string UserText, string AssistantText)>> ListRecentCompletedTurnsAsync(
        Guid conversationId,
        Guid excludeTurnId,
        int maxTurns,
        CancellationToken cancellationToken);

    Task MarkTurnCompletedAsync(
        Guid turnId,
        CancellationToken cancellationToken);

    Task MarkTurnFailedAsync(
        Guid turnId,
        ProcessingStage errorStage,
        string errorMessage,
        CancellationToken cancellationToken);
}
