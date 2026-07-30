using System.Text.Json;
using Microsoft.Extensions.Options;
using STSApp.Backend.Options;
using STSApp.Backend.Repositories;
using STSApp.Backend.Services.Rag;

namespace STSApp.Backend.Tests;

public sealed class KnowledgeSearchServiceTests
{
    [Fact]
    public async Task Search_selects_only_scores_at_or_above_threshold_and_limits_to_three()
    {
        var repository = new SearchRepository([
            CreateStoredVector(1, [1f, 0f, 0f]),
            CreateStoredVector(2, [0.9f, 0.1f, 0f]),
            CreateStoredVector(3, [0.8f, 0.2f, 0f]),
            CreateStoredVector(4, [0.7f, 0.3f, 0f]),
            CreateStoredVector(5, [0f, 1f, 0f])
        ]);
        var service = CreateService(repository, [1f, 0f, 0f]);

        var result = await service.SearchAsync("料金を教えて", CancellationToken.None);

        Assert.Equal(3, result.References.Count);
        Assert.Equal([1L, 2L, 3L], result.References.Select(reference => reference.KnowledgeChunkId));
        Assert.All(result.References, reference => Assert.True(reference.SimilarityScore >= 0.70d));
    }

    [Fact]
    public async Task Search_returns_general_knowledge_when_no_vector_reaches_threshold()
    {
        var repository = new SearchRepository([CreateStoredVector(1, [0f, 1f, 0f])]);
        var service = CreateService(repository, [1f, 0f, 0f]);

        var result = await service.SearchAsync("一般質問", CancellationToken.None);

        Assert.Empty(result.References);
        Assert.Equal(STSApp.Contracts.Enums.AnswerBasis.GeneralKnowledge, result.AnswerBasis);
    }

    private static KnowledgeSearchService CreateService(SearchRepository repository, float[] queryVector)
    {
        return new KnowledgeSearchService(
            repository,
            new QueryEmbeddingClient(queryVector),
            Microsoft.Extensions.Options.Options.Create(new ExternalApiOptions
            {
                UseDevelopmentMocks = false,
                Gemini = new GeminiOptions { ApiKey = "test-key" }
            }),
            Microsoft.Extensions.Options.Options.Create(new RagOptions { EmbeddingDimensions = 3, SimilarityThreshold = 0.70, MaxResults = 3 }));
    }

    private static StoredKnowledgeVector CreateStoredVector(long id, float[] vector)
    {
        return new StoredKnowledgeVector(id, "資料", null, $"見出し{id}", "本文", JsonSerializer.Serialize(VectorMath.Normalize(vector)));
    }

    private sealed class QueryEmbeddingClient : IEmbeddingClient
    {
        private readonly float[] _queryVector;

        public QueryEmbeddingClient(float[] queryVector)
        {
            _queryVector = queryVector;
        }

        public Task<float[]> EmbedDocumentAsync(string text, string title, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => Task.FromResult(_queryVector);
    }

    private sealed class SearchRepository : IKnowledgeRepository
    {
        private readonly IReadOnlyList<StoredKnowledgeVector> _vectors;

        public SearchRepository(IReadOnlyList<StoredKnowledgeVector> vectors)
        {
            _vectors = vectors;
        }

        public Task<IReadOnlyList<IndexedKnowledgeDocument>> ListIndexedDocumentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexedKnowledgeDocument>>([]);
        public Task ApplyReindexAsync(IReadOnlyList<EmbeddedKnowledgeDocument> changedDocuments, IReadOnlyCollection<string> deletedSourcePaths, string embeddingModelName, int embeddingDimensions, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<StoredKnowledgeVector>> ListSearchVectorsAsync(string embeddingModelName, int embeddingDimensions, CancellationToken cancellationToken) => Task.FromResult(_vectors);
        public Task<int> CountAllEmbeddingsAsync(CancellationToken cancellationToken) => Task.FromResult(_vectors.Count);
        public Task AddTurnReferencesAsync(Guid turnId, IReadOnlyList<RetrievedKnowledgeChunk> references, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
