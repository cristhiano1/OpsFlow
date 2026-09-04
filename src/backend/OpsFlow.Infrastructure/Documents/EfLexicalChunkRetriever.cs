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
        // Materialize the ordered rows first. Composing LINQ (e.g. a Select
        // projection) over a raw SQL query wraps it in an outer query with no
        // ORDER BY, which lets SQL Server return rows in an arbitrary order and
        // discards the inner rank ordering. Materializing here keeps the raw
        // SQL's ORDER BY authoritative; the projection below only maps, never sorts.
        var rows = await _db.Database
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
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new LexicalChunkHit(
            row.DocumentId,
            row.DocumentChunkId,
            row.ChunkIndex,
            row.StartOffset,
            row.EndOffset,
            row.Text,
            row.FtsRank))];
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
