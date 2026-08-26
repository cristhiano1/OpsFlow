using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the ensure-document-chunks use case: verifies the document
/// and extraction exist, checks for an existing chunk set, runs the
/// deterministic chunker, and persists the result atomically.
/// </summary>
public sealed class EnsureDocumentChunksService
{
    /// <summary>Current chunking algorithm version.</summary>
    public const int ChunkingVersion = 1;

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentExtractionRepository _extractionRepository;
    private readonly IDocumentChunkSetRepository _chunkSetRepository;
    private readonly IDocumentChunker _chunker;
    private readonly IClock _clock;

    /// <summary>Creates the service with its dependencies.</summary>
    public EnsureDocumentChunksService(
        IDocumentRepository documentRepository,
        IDocumentExtractionRepository extractionRepository,
        IDocumentChunkSetRepository chunkSetRepository,
        IDocumentChunker chunker,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(documentRepository);
        ArgumentNullException.ThrowIfNull(extractionRepository);
        ArgumentNullException.ThrowIfNull(chunkSetRepository);
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(clock);

        _documentRepository = documentRepository;
        _extractionRepository = extractionRepository;
        _chunkSetRepository = chunkSetRepository;
        _chunker = chunker;
        _clock = clock;
    }

    /// <summary>Ensures a chunk set exists for the specified document.</summary>
    public async Task<EnsureDocumentChunksResult> EnsureAsync(
        EnsureDocumentChunksCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(command));
        }

        if (command.ProjectId == Guid.Empty)
        {
            return EnsureDocumentChunksResult.NotFound();
        }

        if (command.DocumentId == Guid.Empty)
        {
            return EnsureDocumentChunksResult.NotFound();
        }

        var document = await _documentRepository.GetByProjectAsync(
            command.DocumentId, command.ProjectId, command.OrganizationId, cancellationToken);

        if (document is null)
        {
            return EnsureDocumentChunksResult.NotFound();
        }

        var existing = await _chunkSetRepository.GetByDocumentAsync(
            command.DocumentId, command.ProjectId, command.OrganizationId, cancellationToken);

        if (existing is not null)
        {
            return EnsureDocumentChunksResult.SuccessExisting(existing);
        }

        var extraction = await _extractionRepository.GetByDocumentAsync(
            command.DocumentId, command.ProjectId, command.OrganizationId, cancellationToken);

        if (extraction is null)
        {
            return EnsureDocumentChunksResult.ExtractionNotFound();
        }

        var slices = _chunker.Chunk(extraction.ExtractedText);

        var chunkSet = new DocumentChunkSet(
            command.DocumentId,
            ChunkingVersion,
            slices.Count,
            _clock.UtcNow);

        var chunks = new List<DocumentChunk>(slices.Count);
        for (int i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            chunks.Add(new DocumentChunk(
                Guid.NewGuid(),
                command.DocumentId,
                i,
                slice.StartOffset,
                slice.EndOffset,
                extraction.ExtractedText[slice.StartOffset..slice.EndOffset]));
        }

        var addResult = await _chunkSetRepository.AddIfAbsentAsync(
            chunkSet, chunks, command.ProjectId, command.OrganizationId, cancellationToken);

        return addResult.WasAdded
            ? EnsureDocumentChunksResult.SuccessCreated(addResult.ChunkSet)
            : EnsureDocumentChunksResult.SuccessExisting(addResult.ChunkSet);
    }
}
