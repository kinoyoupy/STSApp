using Microsoft.Extensions.Options;
using STSApp.Backend.Options;
using STSApp.Backend.Repositories;

namespace STSApp.Backend.Services.Rag;

/// <summary>
/// 開発用ナレッジベースをDBへ取り込むサービスです。
/// API失敗時に半分だけ更新しないよう、外部API呼び出しとDB反映を二段階に分けます。
/// </summary>
public sealed class KnowledgeBaseIndexer : IKnowledgeBaseIndexer
{
    private readonly IKnowledgeRepository _repository;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly MarkdownKnowledgeChunkParser _parser;
    private readonly ExternalApiOptions _externalApiOptions;
    private readonly RagOptions _options;
    private readonly IHostEnvironment _environment;

    public KnowledgeBaseIndexer(
        IKnowledgeRepository repository,
        IEmbeddingClient embeddingClient,
        MarkdownKnowledgeChunkParser parser,
        IOptions<ExternalApiOptions> externalApiOptions,
        IOptions<RagOptions> options,
        IHostEnvironment environment)
    {
        _repository = repository;
        _embeddingClient = embeddingClient;
        _parser = parser;
        _externalApiOptions = externalApiOptions.Value;
        _options = options.Value;
        _environment = environment;
    }

    public async Task<RagReindexResult> ReindexAsync(CancellationToken cancellationToken)
    {
        ValidateSettings();

        var sourceRoot = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, _options.KnowledgeBasePath));
        if (!Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException($"RAG資料フォルダが見つかりません: {sourceRoot}");
        }

        var sources = Directory
            .EnumerateFiles(sourceRoot, "*.md", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileName(path), "README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => ReadSourceDocument(sourceRoot, path))
            .ToList();

        var existingDocuments = await _repository.ListIndexedDocumentsAsync(cancellationToken);
        var existingByPath = existingDocuments.ToDictionary(document => document.SourcePath, StringComparer.Ordinal);
        var changedSources = sources
            .Where(source => !existingByPath.TryGetValue(source.SourcePath, out var existing)
                || !string.Equals(existing.SourceHash, source.SourceHash, StringComparison.Ordinal))
            .ToList();
        var currentPaths = sources.Select(source => source.SourcePath).ToHashSet(StringComparer.Ordinal);
        var deletedPaths = existingDocuments
            .Where(document => !currentPaths.Contains(document.SourcePath))
            .Select(document => document.SourcePath)
            .ToArray();

        // ここで先に全チャンクのEmbeddingを終えます。
        // 途中で1件でも失敗した場合、ApplyReindexAsyncは呼ばれずDBは変更されません。
        var embeddedDocuments = new List<EmbeddedKnowledgeDocument>();
        foreach (var source in changedSources)
        {
            var embeddedChunks = new List<EmbeddedKnowledgeChunk>();
            foreach (var chunk in source.Chunks)
            {
                var textForEmbedding = BuildDocumentEmbeddingText(source.Title, chunk);
                var vector = await _embeddingClient.EmbedDocumentAsync(
                    textForEmbedding,
                    source.Title,
                    cancellationToken);
                ValidateDimension(vector);
                embeddedChunks.Add(new EmbeddedKnowledgeChunk(chunk, VectorMath.Normalize(vector)));
            }

            embeddedDocuments.Add(new EmbeddedKnowledgeDocument(source, embeddedChunks));
        }

        await _repository.ApplyReindexAsync(
            embeddedDocuments,
            deletedPaths,
            _options.EmbeddingModelName,
            _options.EmbeddingDimensions,
            cancellationToken);

        return new RagReindexResult(
            sources.Count,
            changedSources.Count,
            sources.Count - changedSources.Count,
            deletedPaths.Length,
            embeddedDocuments.Sum(document => document.Chunks.Count));
    }

    private KnowledgeSourceDocument ReadSourceDocument(string sourceRoot, string fullPath)
    {
        var sourcePath = Path.GetRelativePath(sourceRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        var markdown = File.ReadAllText(fullPath);
        return _parser.Parse(sourcePath, markdown);
    }

    private void ValidateSettings()
    {
        if (_externalApiOptions.UseDevelopmentMocks)
        {
            throw new InvalidOperationException("UseDevelopmentMocks=true の間はRAG再インデックスを実行できません。");
        }

        if (string.IsNullOrWhiteSpace(_externalApiOptions.Gemini.ApiKey))
        {
            throw new InvalidOperationException("RAG EmbeddingにはGemini APIキーが必要です。");
        }

        if (_options.EmbeddingDimensions != 768)
        {
            throw new InvalidOperationException("今回のRAGはEmbeddingDimensions=768でのみ動作します。");
        }
    }

    private void ValidateDimension(IReadOnlyList<float> vector)
    {
        if (vector.Count != _options.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding次元数が設定と一致しません。Expected={_options.EmbeddingDimensions}, Actual={vector.Count}");
        }
    }

    private static string BuildDocumentEmbeddingText(string title, KnowledgeChunkDraft chunk)
    {
        // 資料名と見出しもEmbeddingへ含めると、「料金」「保存期間」のような短い質問との意味の対応を取りやすくなります。
        return $"資料名: {title}\n親見出し: {chunk.ParentHeading ?? "なし"}\n見出し: {chunk.Heading}\n本文:\n{chunk.Content}";
    }
}
