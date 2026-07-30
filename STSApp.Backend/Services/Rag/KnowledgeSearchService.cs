using System.Text.Json;
using Microsoft.Extensions.Options;
using STSApp.Backend.Repositories;
using STSApp.Backend.Options;

namespace STSApp.Backend.Services.Rag;

/// <summary>
/// MySQLから取り出した正規化済みEmbeddingをBackendで比較する検索処理です。
/// 専用Vector DBを増やさず、今回の小さな資料数に見合う構成にしています。
/// </summary>
public sealed class KnowledgeSearchService : IKnowledgeSearchService
{
    private readonly IKnowledgeRepository _repository;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ExternalApiOptions _externalApiOptions;
    private readonly RagOptions _options;

    public KnowledgeSearchService(
        IKnowledgeRepository repository,
        IEmbeddingClient embeddingClient,
        IOptions<ExternalApiOptions> externalApiOptions,
        IOptions<RagOptions> options)
    {
        _repository = repository;
        _embeddingClient = embeddingClient;
        _externalApiOptions = externalApiOptions.Value;
        _options = options.Value;
    }

    public async Task<RagSearchResult> SearchAsync(string userText, CancellationToken cancellationToken)
    {
        if (_externalApiOptions.UseDevelopmentMocks)
        {
            throw new InvalidOperationException("UseDevelopmentMocks=true の間はRAG検索を実行できません。");
        }

        if (string.IsNullOrWhiteSpace(userText))
        {
            throw new InvalidOperationException("RAG検索するユーザー発話が空です。");
        }

        var allEmbeddingCount = await _repository.CountAllEmbeddingsAsync(cancellationToken);
        if (allEmbeddingCount == 0)
        {
            throw new InvalidOperationException("RAG資料がまだ取り込まれていません。開発用再インデックスを実行してください。");
        }

        var storedVectors = await _repository.ListSearchVectorsAsync(
            _options.EmbeddingModelName,
            _options.EmbeddingDimensions,
            cancellationToken);
        if (storedVectors.Count == 0)
        {
            throw new InvalidOperationException("RAGのEmbeddingモデルまたは次元数が保存済みデータと一致しません。");
        }

        var queryVector = await _embeddingClient.EmbedQueryAsync(userText, cancellationToken);
        if (queryVector.Length != _options.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"検索用Embedding次元数が設定と一致しません。Expected={_options.EmbeddingDimensions}, Actual={queryVector.Length}");
        }

        var normalizedQuery = VectorMath.Normalize(queryVector);
        var candidates = storedVectors
            .Select(vector => new
            {
                Vector = vector,
                Similarity = VectorMath.CosineSimilarityOfNormalizedVectors(
                    normalizedQuery,
                    DeserializeAndValidateVector(vector.VectorJson))
            })
            .Where(candidate => candidate.Similarity >= _options.SimilarityThreshold)
            .OrderByDescending(candidate => candidate.Similarity)
            .Take(_options.MaxResults)
            .Select((candidate, index) => new RetrievedKnowledgeChunk(
                candidate.Vector.KnowledgeChunkId,
                candidate.Vector.DocumentTitle,
                candidate.Vector.ParentHeading,
                candidate.Vector.Heading,
                candidate.Vector.Content,
                candidate.Similarity,
                index + 1))
            .ToList();

        return new RagSearchResult(candidates);
    }

    private float[] DeserializeAndValidateVector(string vectorJson)
    {
        float[]? vector;
        try
        {
            vector = JsonSerializer.Deserialize<float[]>(vectorJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("保存済みRAG EmbeddingのJSON形式が不正です。", ex);
        }

        if (vector is null || vector.Length != _options.EmbeddingDimensions)
        {
            throw new InvalidOperationException("保存済みRAG Embeddingの次元数が不正です。");
        }

        // 保存時に正規化したベクトルでも、DBを手で変更した場合などは壊れる可能性があります。
        // 再正規化せず検査だけ行うことで、不整合を一般回答へ隠さず明確なRAG失敗にします。
        var length = Math.Sqrt(vector.Sum(value => value * value));
        if (Math.Abs(length - 1d) > 0.01d)
        {
            throw new InvalidOperationException("保存済みRAG Embeddingが正規化されていません。");
        }

        return vector;
    }
}
