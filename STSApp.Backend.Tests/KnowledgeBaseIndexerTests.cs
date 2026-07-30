using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services.Rag;

namespace STSApp.Backend.Tests;

public sealed class KnowledgeBaseIndexerTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"sts-rag-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task Reindex_skips_unchanged_document_by_hash()
    {
        var source = WriteKnowledgeFile("01_sample.md", "# 資料\n\n## 項目\n本文");
        var parser = new MarkdownKnowledgeChunkParser();
        var parsed = parser.Parse("01_sample.md", File.ReadAllText(source));
        var repository = new FakeKnowledgeRepository
        {
            IndexedDocuments = [new IndexedKnowledgeDocument(1, parsed.SourcePath, parsed.SourceHash)]
        };
        var embedding = new FakeEmbeddingClient();

        var result = await CreateIndexer(repository, embedding).ReindexAsync(CancellationToken.None);

        Assert.Equal(0, result.ChangedDocumentCount);
        Assert.Equal(1, result.SkippedDocumentCount);
        Assert.Equal(0, embedding.DocumentCallCount);
        Assert.True(repository.ApplyCalled);
        Assert.Empty(repository.AppliedChangedDocuments);
    }

    [Fact]
    public async Task Reindex_embeds_changed_documents_and_synchronizes_deleted_documents()
    {
        WriteKnowledgeFile("01_current.md", "# 現在資料\n\n## 現在\n現在の本文");
        var repository = new FakeKnowledgeRepository
        {
            IndexedDocuments = [
                new IndexedKnowledgeDocument(1, "01_current.md", "old-hash"),
                new IndexedKnowledgeDocument(2, "02_deleted.md", "old-hash")
            ]
        };
        var embedding = new FakeEmbeddingClient();

        var result = await CreateIndexer(repository, embedding).ReindexAsync(CancellationToken.None);

        Assert.Equal(1, result.ChangedDocumentCount);
        Assert.Equal(1, result.DeletedDocumentCount);
        Assert.Equal(1, result.EmbeddedChunkCount);
        Assert.Equal(1, embedding.DocumentCallCount);
        Assert.Equal(["02_deleted.md"], repository.AppliedDeletedPaths);
    }

    [Fact]
    public async Task Reindex_does_not_apply_any_database_change_when_embedding_fails()
    {
        WriteKnowledgeFile("01_first.md", "# 第一\n\n## 項目\n本文");
        WriteKnowledgeFile("02_second.md", "# 第二\n\n## 項目\n本文");
        var repository = new FakeKnowledgeRepository();
        var embedding = new FakeEmbeddingClient { ThrowOnDocumentCall = 2 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateIndexer(repository, embedding).ReindexAsync(CancellationToken.None));

        Assert.False(repository.ApplyCalled);
    }

    private KnowledgeBaseIndexer CreateIndexer(FakeKnowledgeRepository repository, FakeEmbeddingClient embedding)
    {
        return new KnowledgeBaseIndexer(
            repository,
            embedding,
            new MarkdownKnowledgeChunkParser(),
            Microsoft.Extensions.Options.Options.Create(new ExternalApiOptions
            {
                UseDevelopmentMocks = false,
                Gemini = new GeminiOptions { ApiKey = "test-key" }
            }),
            Microsoft.Extensions.Options.Options.Create(new RagOptions { KnowledgeBasePath = "knowledge" }),
            new TestHostEnvironment(_rootPath));
    }

    private string WriteKnowledgeFile(string fileName, string content)
    {
        var directory = Path.Combine(_rootPath, "knowledge");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public int DocumentCallCount { get; private set; }
        public int? ThrowOnDocumentCall { get; init; }

        public Task<float[]> EmbedDocumentAsync(string text, string title, CancellationToken cancellationToken)
        {
            DocumentCallCount++;
            if (ThrowOnDocumentCall == DocumentCallCount)
            {
                throw new InvalidOperationException("Embedding failed for test.");
            }

            return Task.FromResult(CreateVector());
        }

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateVector());
        }

        private static float[] CreateVector()
        {
            var vector = new float[768];
            vector[0] = 1f;
            return vector;
        }
    }

    private sealed class FakeKnowledgeRepository : IKnowledgeRepository
    {
        public IReadOnlyList<IndexedKnowledgeDocument> IndexedDocuments { get; init; } = [];
        public bool ApplyCalled { get; private set; }
        public IReadOnlyList<EmbeddedKnowledgeDocument> AppliedChangedDocuments { get; private set; } = [];
        public IReadOnlyCollection<string> AppliedDeletedPaths { get; private set; } = [];

        public Task<IReadOnlyList<IndexedKnowledgeDocument>> ListIndexedDocumentsAsync(CancellationToken cancellationToken)
            => Task.FromResult(IndexedDocuments);

        public Task ApplyReindexAsync(IReadOnlyList<EmbeddedKnowledgeDocument> changedDocuments, IReadOnlyCollection<string> deletedSourcePaths, string embeddingModelName, int embeddingDimensions, CancellationToken cancellationToken)
        {
            ApplyCalled = true;
            AppliedChangedDocuments = changedDocuments;
            AppliedDeletedPaths = deletedSourcePaths;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredKnowledgeVector>> ListSearchVectorsAsync(string embeddingModelName, int embeddingDimensions, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<StoredKnowledgeVector>>([]);

        public Task<int> CountAllEmbeddingsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task AddTurnReferencesAsync(Guid turnId, IReadOnlyList<RetrievedKnowledgeChunk> references, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new NullFileProvider();
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "STSApp.Backend.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
