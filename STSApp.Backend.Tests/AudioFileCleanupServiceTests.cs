using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services.Storage;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Tests;

public sealed class AudioFileCleanupServiceTests
{
    [Fact]
    public async Task CleanupConversationAsync_DeletesCompletedAudioOnly()
    {
        var completed = CreateCandidate(TurnStatus.Completed, "storage/audio/input/completed.wav");
        var processing = CreateCandidate(TurnStatus.Processing, "storage/audio/input/processing.wav");
        var repository = new FakeCleanupRepository
        {
            ConversationFiles = [completed, processing]
        };
        var storage = new FakeAudioFileStorage();
        var service = CreateService(repository, storage);

        await service.CleanupConversationAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([completed.FilePath], storage.DeletedPaths);
        Assert.Equal([completed.Id], repository.DeletedIds);
        Assert.DoesNotContain(processing.Id, repository.DeletedIds);
    }

    [Fact]
    public async Task CleanupConversationAsync_LeavesDatabaseRecordWhenFileDeleteFails()
    {
        var candidate = CreateCandidate(TurnStatus.Completed, "storage/audio/output/failure.wav");
        var repository = new FakeCleanupRepository
        {
            ConversationFiles = [candidate]
        };
        var storage = new FakeAudioFileStorage
        {
            DeleteException = new IOException("test failure")
        };
        var service = CreateService(repository, storage);

        await service.CleanupConversationAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([candidate.FilePath], storage.DeletedPaths);
        Assert.Empty(repository.DeletedIds);
    }

    [Fact]
    public async Task CleanupOrphanedAudioAsync_DeletesCompletedAndUnregisteredFiles()
    {
        var completed = CreateCandidate(TurnStatus.Completed, "storage/audio/input/completed.wav");
        var processing = CreateCandidate(TurnStatus.Processing, "storage/audio/input/processing.wav");
        var repository = new FakeCleanupRepository
        {
            AllFiles = [completed, processing]
        };
        var storage = new FakeAudioFileStorage
        {
            StoredPaths =
            [
                completed.FilePath,
                processing.FilePath,
                "storage/audio/output/orphan.wav"
            ]
        };
        var service = CreateService(repository, storage);

        await service.CleanupOrphanedAudioAsync(CancellationToken.None);

        Assert.Equal(
            [completed.FilePath, "storage/audio/output/orphan.wav"],
            storage.DeletedPaths);
        Assert.Equal([completed.Id], repository.DeletedIds);
        Assert.DoesNotContain(processing.FilePath, storage.DeletedPaths);
        Assert.DoesNotContain(processing.Id, repository.DeletedIds);
    }

    private static AudioFileCleanupService CreateService(
        FakeCleanupRepository repository,
        FakeAudioFileStorage storage)
    {
        return new AudioFileCleanupService(
            repository,
            storage,
            NullLogger<AudioFileCleanupService>.Instance);
    }

    private static AudioFileCleanupCandidate CreateCandidate(
        TurnStatus status,
        string filePath)
    {
        return new AudioFileCleanupCandidate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            filePath,
            status);
    }

    private sealed class FakeCleanupRepository : IAudioFileCleanupRepository
    {
        public IReadOnlyList<AudioFileCleanupCandidate> ConversationFiles { get; init; } = [];
        public IReadOnlyList<AudioFileCleanupCandidate> AllFiles { get; init; } = [];
        public List<Guid> DeletedIds { get; } = [];

        public Task<IReadOnlyList<AudioFileCleanupCandidate>> ListConversationAudioFilesAsync(
            Guid conversationId,
            bool includeProcessing,
            CancellationToken cancellationToken)
        {
            var result = includeProcessing
                ? ConversationFiles
                : ConversationFiles.Where(x => x.TurnStatus != TurnStatus.Processing).ToList();
            return Task.FromResult<IReadOnlyList<AudioFileCleanupCandidate>>(result);
        }

        public Task<IReadOnlyList<AudioFileCleanupCandidate>> ListAllAudioFilesAsync(
            bool includeProcessing,
            CancellationToken cancellationToken)
        {
            var result = includeProcessing
                ? AllFiles
                : AllFiles.Where(x => x.TurnStatus != TurnStatus.Processing).ToList();
            return Task.FromResult<IReadOnlyList<AudioFileCleanupCandidate>>(result);
        }

        public Task DeleteAudioFileRecordsAsync(
            IReadOnlyCollection<Guid> audioFileIds,
            CancellationToken cancellationToken)
        {
            DeletedIds.AddRange(audioFileIds);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudioFileStorage : IAudioFileStorage
    {
        public IReadOnlyList<string> StoredPaths { get; init; } = [];
        public List<string> DeletedPaths { get; } = [];
        public Exception? DeleteException { get; init; }

        public Task<StoredAudioFile> SaveInputAudioAsync(
            Guid turnId,
            IFormFile audioFile,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StoredAudioFile> SaveOutputAudioAsync(
            Guid turnId,
            Stream audioStream,
            string mimeType,
            string fileExtension,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteAsync(string filePath, CancellationToken cancellationToken)
        {
            DeletedPaths.Add(filePath);
            return DeleteException is null
                ? Task.CompletedTask
                : Task.FromException(DeleteException);
        }

        public Task<IReadOnlyList<string>> ListFilePathsAsync(CancellationToken cancellationToken)
            => Task.FromResult(StoredPaths);

        public Task<Stream?> OpenReadAsync(string filePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
