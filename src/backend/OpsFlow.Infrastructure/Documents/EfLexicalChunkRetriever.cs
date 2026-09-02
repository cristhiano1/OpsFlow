using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// EF Core implementation of <see cref="ILexicalChunkRetriever"/>.
/// Executes a parameterized FREETEXTTABLE query with server-side tenant/project
/// filtering and TopK enforcement.
/// </summary>
public sealed class EfLexicalChunkRetriever : ILexicalChunkRetriever
{
    private readonly OpsFlowDbContext _db;

    /// <summary>Creates the retriever with the supplied database context.</summary>
    public EfLexicalChunkRetriever(OpsFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LexicalChunkHit>> RetrieveAsync(
        Guid organizationId,
        Guid projectId,
        string queryText,
        int topK,
        CancellationToken cancellationToken)
    {
        return await _db.Database
            .SqlQuery<LexicalChunkHitRow>($"""
                SELECT
                    c.[DocumentId],
                    c.[Id] AS [DocumentChunkId],
                    c.[ChunkIndex],
                    c.[StartOffset],
                    c.[EndOffset],
                    c.[Text],
                    ft.[RANK] AS [FtsRank]
                FROM FREETEXTTABLE([DocumentChunks], [Text], {queryText}) AS ft
                INNER JOIN [DocumentChunks] AS c ON c.[Id] = ft.[KEY]
                INNER JOIN [Documents] AS d ON d.[Id] = c.[DocumentId]
                WHERE d.[OrganizationId] = {organizationId}
                  AND d.[ProjectId] = {projectId}
                ORDER BY ft.[RANK] DESC, c.[DocumentId] ASC, c.[ChunkIndex] ASC
                OFFSET 0 ROWS FETCH NEXT {topK} ROWS ONLY
                """)
            .Select(r => new LexicalChunkHit(
                r.DocumentId,
                r.DocumentChunkId,
                r.ChunkIndex,
                r.StartOffset,
                r.EndOffset,
                r.Text,
                r.FtsRank))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    internal sealed class LexicalChunkHitRow
    {
        public Guid DocumentId { get; set; }
        public Guid DocumentChunkId { get; set; }
        public int ChunkIndex { get; set; }
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public string Text { get; set; } = string.Empty;
        public int FtsRank { get; set; }
    }
}
