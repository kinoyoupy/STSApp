using Microsoft.EntityFrameworkCore;
using STSApp.Backend.Data;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Repositories;

/// <summary>
/// 音声ファイルの削除対象を会話ターンの状態と一緒に取得します。
/// Processing中の音声を誤って削除しないため、DB側の状態を基準にします。
/// </summary>
public sealed class AudioFileCleanupRepository : IAudioFileCleanupRepository
{
    private readonly StsDbContext _dbContext;

    public AudioFileCleanupRepository(StsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<AudioFileCleanupCandidate>> ListConversationAudioFilesAsync(
        Guid conversationId,
        bool includeProcessing,
        CancellationToken cancellationToken)
    {
        var query = CreateCandidateQuery()
            .Where(x => x.ConversationId == conversationId);

        if (!includeProcessing)
        {
            query = query.Where(x => x.TurnStatus != TurnStatus.Processing);
        }

        return await query
            .Select(x => new AudioFileCleanupCandidate(
                x.AudioFileId,
                x.ConversationTurnId,
                x.FilePath,
                x.TurnStatus))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AudioFileCleanupCandidate>> ListAllAudioFilesAsync(
        bool includeProcessing,
        CancellationToken cancellationToken)
    {
        var query = CreateCandidateQuery();

        if (!includeProcessing)
        {
            query = query.Where(x => x.TurnStatus != TurnStatus.Processing);
        }

        return await query
            .Select(x => new AudioFileCleanupCandidate(
                x.AudioFileId,
                x.ConversationTurnId,
                x.FilePath,
                x.TurnStatus))
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAudioFileRecordsAsync(
        IReadOnlyCollection<Guid> audioFileIds,
        CancellationToken cancellationToken)
    {
        if (audioFileIds.Count == 0)
        {
            return;
        }

        var records = await _dbContext.AudioFiles
            .Where(x => audioFileIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        _dbContext.AudioFiles.RemoveRange(records);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<AudioFileCandidateQuery> CreateCandidateQuery()
    {
        return from audioFile in _dbContext.AudioFiles.AsNoTracking()
               join turn in _dbContext.ConversationTurns.AsNoTracking()
                   on audioFile.ConversationTurnId equals turn.Id
               select new AudioFileCandidateQuery
               {
                   AudioFileId = audioFile.Id,
                   ConversationId = turn.ConversationId,
                   ConversationTurnId = audioFile.ConversationTurnId,
                   FilePath = audioFile.FilePath,
                   TurnStatus = turn.Status
               };
    }

    private sealed class AudioFileCandidateQuery
    {
        public Guid AudioFileId { get; init; }
        public Guid ConversationId { get; init; }
        public Guid ConversationTurnId { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public TurnStatus TurnStatus { get; init; }
    }
}
