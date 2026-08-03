using Microsoft.Extensions.Logging;
using STSApp.Backend.Repositories;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Services.Storage;

/// <summary>
/// 音声ファイル本体とDB上の参照情報を、処理状態を確認しながら整理します。
/// ファイルシステムとDBは同じトランザクションで変更できないため、
/// 何度実行しても安全な形にして、失敗した対象を次回へ繰り越します。
/// </summary>
public sealed class AudioFileCleanupService : IAudioFileCleanupService
{
    private readonly IAudioFileCleanupRepository _repository;
    private readonly IAudioFileStorage _storage;
    private readonly ILogger<AudioFileCleanupService> _logger;

    public AudioFileCleanupService(
        IAudioFileCleanupRepository repository,
        IAudioFileStorage storage,
        ILogger<AudioFileCleanupService> logger)
    {
        _repository = repository;
        _storage = storage;
        _logger = logger;
    }

    public Task CleanupConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        return CleanupDatabaseAudioFilesAsync(
            _repository.ListConversationAudioFilesAsync(
                conversationId,
                includeProcessing: false,
                cancellationToken),
            cancellationToken);
    }

    public async Task CleanupOrphanedAudioAsync(CancellationToken cancellationToken)
    {
        // Processing中の音声は、Backend起動直後にまだ使われる処理がない場合でも、
        // 先にRecoverInterruptedTurnsAsyncが失敗状態へ戻してから削除対象にします。
        var databaseFiles = await _repository.ListAllAudioFilesAsync(
            includeProcessing: true,
            cancellationToken);
        var cleanableFiles = databaseFiles
            .Where(x => x.TurnStatus != TurnStatus.Processing)
            .ToList();

        await CleanupDatabaseAudioFilesAsync(cleanableFiles, cancellationToken);

        // DBに登録されていないファイルは、途中停止などで残った孤立ファイルです。
        // Processing中の音声に対応するファイルは保護し、それ以外だけを削除します。
        var protectedPaths = databaseFiles
            .Where(x => x.TurnStatus == TurnStatus.Processing)
            .Select(x => x.FilePath)
            .ToHashSet(StringComparer.Ordinal);
        var registeredPaths = databaseFiles
            .Select(x => x.FilePath)
            .ToHashSet(StringComparer.Ordinal);

        var storedPaths = await _storage.ListFilePathsAsync(cancellationToken);
        foreach (var filePath in storedPaths)
        {
            if (registeredPaths.Contains(filePath) || protectedPaths.Contains(filePath))
            {
                continue;
            }

            await TryDeleteFileAsync(filePath, "孤立ファイル");
        }
    }

    private async Task CleanupDatabaseAudioFilesAsync(
        Task<IReadOnlyList<AudioFileCleanupCandidate>> candidatesTask,
        CancellationToken cancellationToken)
    {
        var candidates = await candidatesTask;
        await CleanupDatabaseAudioFilesAsync(candidates, cancellationToken);
    }

    private async Task CleanupDatabaseAudioFilesAsync(
        IReadOnlyList<AudioFileCleanupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var deletedFileIds = new List<Guid>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await TryDeleteFileAsync(candidate.FilePath, "会話音声"))
            {
                continue;
            }

            deletedFileIds.Add(candidate.Id);
        }

        // ファイルが削除できたものだけDBから消します。
        // ファイル削除に失敗した参照は残し、次回の整理で再試行できるようにします。
        await _repository.DeleteAudioFileRecordsAsync(deletedFileIds, cancellationToken);
    }

    private async Task<bool> TryDeleteFileAsync(string filePath, string category)
    {
        try
        {
            // DeleteAsyncはファイルが既にない場合も成功扱いにします。
            await _storage.DeleteAsync(filePath, CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "{Category}の削除に失敗しました。次回の整理で再試行します。FilePath={FilePath}",
                category,
                filePath);
            return false;
        }
    }
}
