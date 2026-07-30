using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using STSApp.Backend.Data;
using STSApp.Backend.Domain.Entities;
using STSApp.Backend.Services.Rag;

namespace STSApp.Backend.Repositories;

/// <summary>
/// Vector RAGで必要なDB操作をまとめます。
/// 再インデックスは1トランザクションで反映し、途中の資料だけがDBへ残らないようにします。
/// </summary>
public sealed class KnowledgeRepository : IKnowledgeRepository
{
    private readonly StsDbContext _dbContext;

    public KnowledgeRepository(StsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<IndexedKnowledgeDocument>> ListIndexedDocumentsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.KnowledgeDocuments
            .AsNoTracking()
            .Select(document => new IndexedKnowledgeDocument(document.Id, document.SourcePath, document.SourceHash))
            .ToListAsync(cancellationToken);
    }

    public async Task ApplyReindexAsync(
        IReadOnlyList<EmbeddedKnowledgeDocument> changedDocuments,
        IReadOnlyCollection<string> deletedSourcePaths,
        string embeddingModelName,
        int embeddingDimensions,
        CancellationToken cancellationToken)
    {
        // Embeddingはすべてメモリ上で成功してからこのメソッドを呼びます。
        // さらにDB側もトランザクションにすることで、資料・チャンク・ベクトルの世代を必ずそろえます。
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;

        if (deletedSourcePaths.Count > 0)
        {
            var deletedDocuments = await _dbContext.KnowledgeDocuments
                .Where(document => deletedSourcePaths.Contains(document.SourcePath))
                .ToListAsync(cancellationToken);

            _dbContext.KnowledgeDocuments.RemoveRange(deletedDocuments);
        }

        foreach (var changedDocument in changedDocuments)
        {
            var source = changedDocument.Source;
            var existingDocument = await _dbContext.KnowledgeDocuments
                .FirstOrDefaultAsync(document => document.SourcePath == source.SourcePath, cancellationToken);

            if (existingDocument is not null)
            {
                // 既存資料は子テーブルを消して作り直します。
                // 本文変更でチャンク構成が変わっても、古いEmbeddingが残らないことを優先します。
                _dbContext.KnowledgeDocuments.Remove(existingDocument);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            var document = new KnowledgeDocumentEntity
            {
                SourcePath = source.SourcePath,
                Title = source.Title,
                SourceHash = source.SourceHash,
                CreatedAt = now,
                UpdatedAt = now
            };

            _dbContext.KnowledgeDocuments.Add(document);
            await _dbContext.SaveChangesAsync(cancellationToken);

            foreach (var embeddedChunk in changedDocument.Chunks)
            {
                var draft = embeddedChunk.Draft;
                var chunk = new KnowledgeChunkEntity
                {
                    KnowledgeDocumentId = document.Id,
                    ParentHeading = draft.ParentHeading,
                    Heading = draft.Heading,
                    Content = draft.Content,
                    ChunkOrder = draft.ChunkOrder,
                    ContentHash = draft.ContentHash,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _dbContext.KnowledgeChunks.Add(chunk);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _dbContext.KnowledgeChunkEmbeddings.Add(new KnowledgeChunkEmbeddingEntity
                {
                    KnowledgeChunkId = chunk.Id,
                    ModelName = embeddingModelName,
                    Dimensions = embeddingDimensions,
                    VectorJson = JsonSerializer.Serialize(embeddedChunk.NormalizedVector),
                    CreatedAt = now
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoredKnowledgeVector>> ListSearchVectorsAsync(
        string embeddingModelName,
        int embeddingDimensions,
        CancellationToken cancellationToken)
    {
        return await (
            from embedding in _dbContext.KnowledgeChunkEmbeddings.AsNoTracking()
            join chunk in _dbContext.KnowledgeChunks.AsNoTracking() on embedding.KnowledgeChunkId equals chunk.Id
            join document in _dbContext.KnowledgeDocuments.AsNoTracking() on chunk.KnowledgeDocumentId equals document.Id
            where embedding.ModelName == embeddingModelName && embedding.Dimensions == embeddingDimensions
            select new StoredKnowledgeVector(
                chunk.Id,
                document.Title,
                chunk.ParentHeading,
                chunk.Heading,
                chunk.Content,
                embedding.VectorJson))
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountAllEmbeddingsAsync(CancellationToken cancellationToken)
    {
        return _dbContext.KnowledgeChunkEmbeddings.CountAsync(cancellationToken);
    }

    public async Task AddTurnReferencesAsync(
        Guid turnId,
        IReadOnlyList<RetrievedKnowledgeChunk> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var entries = references.Select(reference => new TurnRagReferenceEntity
        {
            ConversationTurnId = turnId,
            KnowledgeChunkId = reference.KnowledgeChunkId,
            RetrievalRank = reference.RetrievalRank,
            SimilarityScore = (decimal)reference.SimilarityScore,
            DocumentTitleSnapshot = reference.DocumentTitle,
            ParentHeadingSnapshot = reference.ParentHeading,
            HeadingSnapshot = reference.Heading,
            ContentSnapshot = reference.Content,
            CreatedAt = now
        });

        _dbContext.TurnRagReferences.AddRange(entries);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
