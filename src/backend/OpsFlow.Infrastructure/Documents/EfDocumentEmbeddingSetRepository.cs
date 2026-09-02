using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentEmbeddingSetRepository"/>.
/// All reads enforce tenant isolation via a join to the Documents table.
/// Writes verify tenant ownership before inserting. Only SQL Server
/// duplicate-key violations (2601/2627) are treated as concurrent duplicates.
/// </summary>
public sealed class EfDocumentEmbeddingSetRepository : IDocumentEmbeddingSetRepository
{
    private readonly OpsFlowDbContext _db;

    /// <summary>Creates the repository with the supplied database context.</summary>
    public EfDocumentEmbeddingSetRepository(OpsFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DocumentEmbeddingSet?> GetByDocumentAndProfileAsync(
        Guid documentId,
        string profileId,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return await _db.DocumentEmbeddingSets
            .Join(
                _db.Documents,
                es => es.DocumentId,
                d => d.Id,
                (es, d) => new { EmbeddingSet = es, Document = d })
            .Where(x => x.EmbeddingSet.DocumentId == documentId
                     && x.EmbeddingSet.ProfileId == profileId
                     && x.Document.ProjectId == projectId
                     && x.Document.OrganizationId == organizationId)
            .Select(x => x.EmbeddingSet)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentEmbeddingSetAddResult> AddIfAbsentAsync(
        DocumentEmbeddingSet embeddingSet,
        IReadOnlyList<ChunkEmbeddingInput> embeddings,
        Guid projectId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddingSet);
        ArgumentNullException.ThrowIfNull(embeddings);

        if (embeddings.Count != embeddingSet.EmbeddingCount)
        {
            throw new ArgumentException(
                $"Embeddings count ({embeddings.Count}) does not match EmbeddingCount ({embeddingSet.EmbeddingCount}).",
                nameof(embeddings));
        }

        if (embeddingSet.Dimensions != EmbeddingProfiles.SemanticV1Dimensions)
        {
            throw new ArgumentException(
                $"EmbeddingSet dimensions ({embeddingSet.Dimensions}) does not match required dimensions ({EmbeddingProfiles.SemanticV1Dimensions}).",
                nameof(embeddingSet));
        }

        if (embeddings.Count > 0)
        {
            var seenChunkIds = new HashSet<Guid>(embeddings.Count);

            foreach (var input in embeddings)
            {
                if (input.DocumentChunkId == Guid.Empty)
                {
                    throw new ArgumentException(
                        "DocumentChunkId must not be empty.", nameof(embeddings));
                }

                if (!seenChunkIds.Add(input.DocumentChunkId))
                {
                    throw new ArgumentException(
                        $"Duplicate DocumentChunkId: {input.DocumentChunkId}.", nameof(embeddings));
                }

                if (input.Vector.Length != EmbeddingProfiles.SemanticV1Dimensions)
                {
                    throw new ArgumentException(
                        $"Vector length ({input.Vector.Length}) does not match expected dimensions ({EmbeddingProfiles.SemanticV1Dimensions}).",
                        nameof(embeddings));
                }

                var span = input.Vector.Span;
                bool anyNonZero = false;
                for (int i = 0; i < span.Length; i++)
                {
                    if (!float.IsFinite(span[i]))
                    {
                        throw new ArgumentException(
                            $"Vector contains non-finite value at index {i}.", nameof(embeddings));
                    }

                    if (span[i] != 0f)
                    {
                        anyNonZero = true;
                    }
                }

                if (!anyNonZero)
                {
                    throw new ArgumentException(
                        $"Vector for chunk {input.DocumentChunkId} has zero norm and cannot be used for cosine distance.",
                        nameof(embeddings));
                }
            }
        }

        var sourceMetadata = await _db.DocumentChunkSets
            .Join(
                _db.Documents,
                cs => cs.DocumentId,
                d => d.Id,
                (cs, d) => new { ChunkSet = cs, Document = d })
            .Where(x => x.ChunkSet.DocumentId == embeddingSet.DocumentId
                     && x.Document.ProjectId == projectId
                     && x.Document.OrganizationId == organizationId)
            .Select(x => new { x.ChunkSet.ChunkingVersion, x.ChunkSet.ChunkCount })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (sourceMetadata is null)
        {
            return DocumentEmbeddingSetAddResult.NotFound();
        }

        if (embeddingSet.ChunkingVersion != sourceMetadata.ChunkingVersion)
        {
            throw new ArgumentException(
                $"EmbeddingSet chunking version ({embeddingSet.ChunkingVersion}) does not match source chunk set version ({sourceMetadata.ChunkingVersion}).",
                nameof(embeddingSet));
        }

        if (embeddingSet.EmbeddingCount != sourceMetadata.ChunkCount)
        {
            throw new ArgumentException(
                $"EmbeddingSet embedding count ({embeddingSet.EmbeddingCount}) does not match source chunk count ({sourceMetadata.ChunkCount}).",
                nameof(embeddingSet));
        }

        var actualChunkRowCount = await _db.DocumentChunks
            .AsNoTracking()
            .Where(c => c.DocumentId == embeddingSet.DocumentId)
            .CountAsync(cancellationToken);

        if (actualChunkRowCount != sourceMetadata.ChunkCount)
        {
            throw new InvalidOperationException(
                $"Persisted chunk row count ({actualChunkRowCount}) does not match DocumentChunkSet.ChunkCount ({sourceMetadata.ChunkCount}). Source chunk artifact is structurally inconsistent.");
        }

        if (embeddings.Count > 0)
        {
            var chunkIds = embeddings.Select(e => e.DocumentChunkId).ToArray();

            var matchingChunkCount = await _db.DocumentChunks
                .AsNoTracking()
                .Where(c => c.DocumentId == embeddingSet.DocumentId
                          && chunkIds.Contains(c.Id))
                .CountAsync(cancellationToken);

            if (matchingChunkCount != chunkIds.Length)
            {
                throw new ArgumentException(
                    $"Not all chunk IDs belong to document {embeddingSet.DocumentId}.",
                    nameof(embeddings));
            }
        }

        var rows = embeddings.Select(input => new DocumentChunkEmbeddingRow
        {
            EmbeddingSetId = embeddingSet.Id,
            DocumentChunkId = input.DocumentChunkId,
            Embedding = new SqlVector<float>(input.Vector.ToArray()),
        }).ToList();

        try
        {
            _ = _db.DocumentEmbeddingSets.Add(embeddingSet);
            _db.Set<DocumentChunkEmbeddingRow>().AddRange(rows);
            _ = await _db.SaveChangesAsync(cancellationToken);
            return DocumentEmbeddingSetAddResult.Added(embeddingSet);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyViolation(ex))
        {
            _db.Entry(embeddingSet).State = EntityState.Detached;
            foreach (var row in rows)
            {
                _db.Entry(row).State = EntityState.Detached;
            }

            var existing = await GetByDocumentAndProfileAsync(
                embeddingSet.DocumentId, embeddingSet.ProfileId,
                projectId, organizationId, cancellationToken);

            if (existing is null)
            {
                throw;
            }

            return DocumentEmbeddingSetAddResult.AlreadyExists(existing);
        }
    }

    private static bool IsDuplicateKeyViolation(DbUpdateException ex)
    {
        // SQL Server error numbers for duplicate key violations:
        // 2601 = unique index violation
        // 2627 = unique constraint (PK or unique key) violation
        return ex.InnerException is SqlException sqlEx
            && sqlEx.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627);
    }
}
