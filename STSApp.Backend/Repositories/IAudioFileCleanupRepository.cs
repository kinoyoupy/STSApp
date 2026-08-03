using STSApp.Backend.Domain.Entities;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Repositories;

/// <summary>
/// 音声削除に必要なDB情報だけを扱うRepositoryです。
/// 会話本文やRAG参照履歴を削除処理から分離するため、専用の窓口にします。
/// </summary>
public interface IAudioFileCleanupRepository
{
    Task<IReadOnlyList<AudioFileCleanupCandidate>> ListConversationAudioFilesAsync(
        Guid conversationId,
        bool includeProcessing,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AudioFileCleanupCandidate>> ListAllAudioFilesAsync(
        bool includeProcessing,
        CancellationToken cancellationToken);

    Task DeleteAudioFileRecordsAsync(
        IReadOnlyCollection<Guid> audioFileIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// 音声ファイル本体とDB記録を対応付けるための内部データです。
/// </summary>
public sealed record AudioFileCleanupCandidate(
    Guid Id,
    Guid ConversationTurnId,
    string FilePath,
    TurnStatus TurnStatus);
