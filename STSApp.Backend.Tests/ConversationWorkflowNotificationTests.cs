using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using STSApp.Backend.Hubs;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services;
using STSApp.Backend.Services.External;
using STSApp.Backend.Services.Rag;
using STSApp.Backend.Services.Storage;
using STSApp.Contracts.Enums;
using STSApp.Contracts.Models;

namespace STSApp.Backend.Tests;

/// <summary>
/// SignalRは補助的な通知であり、会話処理の成否を決めないことを確認します。
/// </summary>
public sealed class ConversationWorkflowNotificationTests
{
    private const string PrivateErrorDetail = "PRIVATE_SPEECH_OR_EXTERNAL_API_DETAIL";

    [Fact]
    public async Task Workflow_completes_when_every_signalr_notification_fails()
    {
        var conversationId = Guid.NewGuid();
        var repository = new ConversationRepositoryStub(conversationId);
        var workflow = new ConversationWorkflow(
            repository,
            new AudioFileStorageStub(),
            new SttClientStub(),
            new KnowledgeSearchServiceStub(),
            new KnowledgeRepositoryStub(),
            new GeminiClientStub(),
            new TtsClientStub(),
            new ThrowingHubContext(),
            NullLogger<ConversationWorkflow>.Instance);
        var audioBytes = new byte[] { 1, 2, 3 };
        var audioFile = new FormFile(
            new MemoryStream(audioBytes),
            0,
            audioBytes.Length,
            "audioFile",
            "recording.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

        var result = await workflow.ProcessAudioTurnAsync(
            conversationId,
            audioFile,
            CancellationToken.None);

        Assert.Equal(repository.TurnId, result.TurnId);
        Assert.Equal(repository.OutputAudioId, result.OutputAudioId);
        Assert.True(repository.Completed);
        Assert.False(repository.Failed);
    }

    [Fact]
    public async Task Workflow_does_not_store_raw_exception_message_in_failure_event()
    {
        var conversationId = Guid.NewGuid();
        var repository = new ConversationRepositoryStub(conversationId);
        var workflow = new ConversationWorkflow(
            repository,
            new AudioFileStorageStub(),
            new FailingSttClientStub(),
            new KnowledgeSearchServiceStub(),
            new KnowledgeRepositoryStub(),
            new GeminiClientStub(),
            new TtsClientStub(),
            new ThrowingHubContext(),
            NullLogger<ConversationWorkflow>.Instance);
        var audioBytes = new byte[] { 1, 2, 3 };
        var audioFile = new FormFile(
            new MemoryStream(audioBytes),
            0,
            audioBytes.Length,
            "audioFile",
            "recording.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.ProcessAudioTurnAsync(
                conversationId,
                audioFile,
                CancellationToken.None));

        Assert.Contains(PrivateErrorDetail, exception.Message);
        Assert.DoesNotContain(
            repository.Events,
            turnEvent => turnEvent.Message?.Contains(PrivateErrorDetail, StringComparison.Ordinal) == true);
        Assert.Contains(
            repository.Events,
            turnEvent =>
                turnEvent.EventType == TurnEventType.Failed &&
                turnEvent.Message == "音声を文字に変換できませんでした。");
    }

    [Fact]
    public async Task Workflow_uses_independent_token_to_record_failure_when_processing_is_cancelled()
    {
        var conversationId = Guid.NewGuid();
        var repository = new ConversationRepositoryStub(conversationId);
        var workflow = new ConversationWorkflow(
            repository,
            new AudioFileStorageStub(),
            new CancelledSttClientStub(),
            new KnowledgeSearchServiceStub(),
            new KnowledgeRepositoryStub(),
            new GeminiClientStub(),
            new TtsClientStub(),
            new ThrowingHubContext(),
            NullLogger<ConversationWorkflow>.Instance);
        var audioBytes = new byte[] { 1, 2, 3 };
        var audioFile = new FormFile(
            new MemoryStream(audioBytes),
            0,
            audioBytes.Length,
            "audioFile",
            "recording.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.ProcessAudioTurnAsync(
                conversationId,
                audioFile,
                cancellationTokenSource.Token));

        Assert.True(repository.Failed);
        Assert.False(repository.FailureTokenWasCancellationRequested);
    }

    [Theory]
    [InlineData(AudioFileKind.Input, "storage/audio/input.wav")]
    [InlineData(AudioFileKind.Output, "storage/audio/output.wav")]
    public async Task Workflow_deletes_audio_when_its_database_registration_fails(
        AudioFileKind failingKind,
        string expectedDeletedPath)
    {
        var conversationId = Guid.NewGuid();
        var repository = new ConversationRepositoryStub(conversationId)
        {
            FailingAudioKind = failingKind
        };
        var storage = new AudioFileStorageStub();
        var workflow = new ConversationWorkflow(
            repository,
            storage,
            new SttClientStub(),
            new KnowledgeSearchServiceStub(),
            new KnowledgeRepositoryStub(),
            new GeminiClientStub(),
            new TtsClientStub(),
            new ThrowingHubContext(),
            NullLogger<ConversationWorkflow>.Instance);
        var audioBytes = new byte[] { 1, 2, 3 };
        var audioFile = new FormFile(
            new MemoryStream(audioBytes),
            0,
            audioBytes.Length,
            "audioFile",
            "recording.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            workflow.ProcessAudioTurnAsync(
                conversationId,
                audioFile,
                CancellationToken.None));

        Assert.Equal([expectedDeletedPath], storage.DeletedPaths);
        Assert.True(repository.Failed);
    }

    [Fact]
    public async Task Workflow_keeps_original_database_error_when_compensation_delete_fails()
    {
        var conversationId = Guid.NewGuid();
        var repository = new ConversationRepositoryStub(conversationId)
        {
            FailingAudioKind = AudioFileKind.Input
        };
        var storage = new AudioFileStorageStub
        {
            DeleteException = new IOException("Cleanup failed.")
        };
        var workflow = new ConversationWorkflow(
            repository,
            storage,
            new SttClientStub(),
            new KnowledgeSearchServiceStub(),
            new KnowledgeRepositoryStub(),
            new GeminiClientStub(),
            new TtsClientStub(),
            new ThrowingHubContext(),
            NullLogger<ConversationWorkflow>.Instance);
        var audioFile = new FormFile(
            new MemoryStream([1, 2, 3]),
            0,
            3,
            "audioFile",
            "recording.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            workflow.ProcessAudioTurnAsync(
                conversationId,
                audioFile,
                CancellationToken.None));

        Assert.Equal("Audio file registration failed.", exception.Message);
    }

    private sealed class ConversationRepositoryStub : IConversationRepository
    {
        private readonly Guid _conversationId;

        public ConversationRepositoryStub(Guid conversationId)
        {
            _conversationId = conversationId;
        }

        public Guid TurnId { get; } = Guid.NewGuid();
        public Guid OutputAudioId { get; } = Guid.NewGuid();
        public bool Completed { get; private set; }
        public bool Failed { get; private set; }
        public bool FailureTokenWasCancellationRequested { get; private set; }
        public AudioFileKind? FailingAudioKind { get; init; }
        public List<(TurnEventType EventType, string? Message)> Events { get; } = [];

        public Task<ConversationTurnDto> CreateProcessingTurnAsync(
            Guid conversationId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ConversationTurnDto(
                TurnId,
                conversationId,
                null,
                null,
                null,
                TurnStatus.Processing,
                null,
                null,
                DateTime.UtcNow,
                DateTime.UtcNow));
        }

        public Task<AudioFileDto> AddAudioFileAsync(
            Guid turnId,
            AudioFileKind kind,
            string filePath,
            string mimeType,
            long? fileSizeBytes,
            CancellationToken cancellationToken)
        {
            if (kind == FailingAudioKind)
            {
                return Task.FromException<AudioFileDto>(
                    new DbUpdateException("Audio file registration failed."));
            }

            return Task.FromResult(new AudioFileDto(
                kind == AudioFileKind.Output ? OutputAudioId : Guid.NewGuid(),
                turnId,
                kind,
                filePath,
                mimeType,
                fileSizeBytes,
                DateTime.UtcNow));
        }

        public Task MarkTurnCompletedAsync(Guid turnId, CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public Task MarkTurnFailedAsync(
            Guid turnId,
            ProcessingStage errorStage,
            string errorMessage,
            CancellationToken cancellationToken)
        {
            Failed = true;
            FailureTokenWasCancellationRequested = cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string UserText, string AssistantText)>> ListRecentCompletedTurnsAsync(
            Guid conversationId,
            Guid excludeTurnId,
            int maxTurns,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<(string, string)>>([]);
        }

        public Task AddTurnEventAsync(Guid turnId, ProcessingStage stage, TurnEventType eventType, string? message, int? durationMs, CancellationToken cancellationToken)
        {
            Events.Add((eventType, message));
            return Task.CompletedTask;
        }
        public Task UpdateUserTextAsync(Guid turnId, string userText, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateAssistantTextAsync(Guid turnId, string assistantText, AnswerBasis answerBasis, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConversationExistsAsync(Guid conversationId, CancellationToken cancellationToken) => Task.FromResult(conversationId == _conversationId);
        public Task<ConversationDto> CreateConversationAsync(string? title, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationDto>> ListConversationsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationTurnDto>> ListConversationTurnsAsync(Guid conversationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AudioFileDto?> GetAudioFileAsync(Guid audioId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AudioFileStorageStub : IAudioFileStorage
    {
        public List<string> DeletedPaths { get; } = [];
        public Exception? DeleteException { get; init; }

        public Task<StoredAudioFile> SaveInputAudioAsync(Guid turnId, IFormFile audioFile, CancellationToken cancellationToken)
            => Task.FromResult(new StoredAudioFile("storage/audio/input.wav", "audio/wav", audioFile.Length));

        public Task<StoredAudioFile> SaveOutputAudioAsync(Guid turnId, Stream audioStream, string mimeType, string fileExtension, CancellationToken cancellationToken)
            => Task.FromResult(new StoredAudioFile("storage/audio/output.wav", mimeType, 3));

        public Task DeleteAsync(string filePath, CancellationToken cancellationToken)
        {
            DeletedPaths.Add(filePath);
            return DeleteException is null
                ? Task.CompletedTask
                : Task.FromException(DeleteException);
        }

        public Task<IReadOnlyList<string>> ListFilePathsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<Stream?> OpenReadAsync(string filePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class SttClientStub : ISttClient
    {
        public Task<string> TranscribeAsync(Stream audioStream, string fileName, string contentType, CancellationToken cancellationToken)
            => Task.FromResult("こんにちは");
    }

    private sealed class FailingSttClientStub : ISttClient
    {
        public Task<string> TranscribeAsync(Stream audioStream, string fileName, string contentType, CancellationToken cancellationToken)
            => Task.FromException<string>(new InvalidOperationException(PrivateErrorDetail));
    }

    private sealed class CancelledSttClientStub : ISttClient
    {
        public Task<string> TranscribeAsync(Stream audioStream, string fileName, string contentType, CancellationToken cancellationToken)
            => Task.FromCanceled<string>(cancellationToken);
    }

    private sealed class GeminiClientStub : IGeminiClient
    {
        public Task<string> GenerateReplyAsync(GeminiReplyRequest request, CancellationToken cancellationToken)
            => Task.FromResult("こんにちは。");
    }

    private sealed class TtsClientStub : ITtsClient
    {
        public Task<GeneratedSpeech> SynthesizeAsync(string text, CancellationToken cancellationToken)
            => Task.FromResult(new GeneratedSpeech(new MemoryStream([1, 2, 3]), "audio/wav", ".wav"));
    }

    private sealed class KnowledgeSearchServiceStub : IKnowledgeSearchService
    {
        public Task<RagSearchResult> SearchAsync(string userText, CancellationToken cancellationToken)
            => Task.FromResult(new RagSearchResult([]));
    }

    private sealed class KnowledgeRepositoryStub : IKnowledgeRepository
    {
        public Task AddTurnReferencesAsync(Guid turnId, IReadOnlyList<RetrievedKnowledgeChunk> references, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<IndexedKnowledgeDocument>> ListIndexedDocumentsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ApplyReindexAsync(IReadOnlyList<EmbeddedKnowledgeDocument> changedDocuments, IReadOnlyCollection<string> deletedSourcePaths, string embeddingModelName, int embeddingDimensions, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredKnowledgeVector>> ListSearchVectorsAsync(string embeddingModelName, int embeddingDimensions, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountAllEmbeddingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingHubContext : IHubContext<ConversationHub>
    {
        public IHubClients Clients { get; } = new ThrowingHubClients();
        public IGroupManager Groups { get; } = new GroupManagerStub();
    }

    private sealed class ThrowingHubClients : IHubClients
    {
        private static IClientProxy Proxy { get; } = new ThrowingClientProxy();

        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class ThrowingClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("SignalR is unavailable."));
    }

    private sealed class GroupManagerStub : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
