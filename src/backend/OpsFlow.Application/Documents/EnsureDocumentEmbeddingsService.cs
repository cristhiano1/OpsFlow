using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the ensure-document-embeddings use case: verifies the document
/// and chunk snapshot exist, checks for an existing compatible embedding set,
/// validates the generator profile, generates embeddings, validates outputs,
/// and persists the result atomically.
/// </summary>
public sealed class EnsureDocumentEmbeddingsService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentChunkSnapshotReader _snapshotReader;
    private readonly IDocumentEmbeddingSetRepository _embeddingSetRepository;
    private readonly IEmbeddingGenerator _generator;
    private readonly IClock _clock;

    /// <summary>Creates the service with its dependencies.</summary>
    public EnsureDocumentEmbeddingsService(
        IDocumentRepository documentRepository,
        IDocumentChunkSnapshotReader snapshotReader,
        IDocumentEmbeddingSetRepository embeddingSetRepository,
        IEmbeddingGenerator generator,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(documentRepository);
        ArgumentNullException.ThrowIfNull(snapshotReader);
        ArgumentNullException.ThrowIfNull(embeddingSetRepository);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(clock);

        _documentRepository = documentRepository;
        _snapshotReader = snapshotReader;
        _embeddingSetRepository = embeddingSetRepository;
        _generator = generator;
        _clock = clock;
    }

    /// <summary>Ensures an embedding set exists for the specified document.</summary>
    public async Task<EnsureDocumentEmbeddingsResult> EnsureAsync(
        EnsureDocumentEmbeddingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(command));
        }

        if (command.ProjectId == Guid.Empty)
        {
            return EnsureDocumentEmbeddingsResult.NotFound();
        }

        if (command.DocumentId == Guid.Empty)
        {
            return EnsureDocumentEmbeddingsResult.NotFound();
        }

        ValidateGeneratorProfile();

        var document = await _documentRepository.GetByProjectAsync(
            command.DocumentId, command.ProjectId, command.OrganizationId, cancellationToken);

        if (document is null)
        {
            return EnsureDocumentEmbeddingsResult.NotFound();
        }

        var snapshot = await _snapshotReader.GetByDocumentAsync(
            command.DocumentId, command.ProjectId, command.OrganizationId, cancellationToken);

        if (snapshot is null)
        {
            return EnsureDocumentEmbeddingsResult.ChunksNotFound();
        }

        ValidateSnapshotCompleteness(snapshot, command.DocumentId);

        var existing = await _embeddingSetRepository.GetByDocumentAndProfileAsync(
            command.DocumentId, _generator.Identity.ProfileId,
            command.ProjectId, command.OrganizationId, cancellationToken);

        if (existing is not null)
        {
            return ValidateCompatibility(existing, snapshot);
        }

        IReadOnlyList<ChunkEmbeddingInput> embeddingInputs;

        if (snapshot.ChunkCount == 0)
        {
            embeddingInputs = [];
        }
        else
        {
            var texts = snapshot.Chunks.Select(c => c.Text).ToList();
            var vectors = await _generator.GenerateAsync(texts, cancellationToken);

            ValidateGeneratorOutput(vectors, snapshot);

            embeddingInputs = [.. snapshot.Chunks
                .Select((chunk, i) => new ChunkEmbeddingInput(chunk.ChunkId, vectors[i]))];
        }

        var embeddingSet = new DocumentEmbeddingSet(
            Guid.NewGuid(),
            command.DocumentId,
            snapshot.ChunkingVersion,
            _generator.Identity.ProfileId,
            _generator.Identity.ModelId,
            _generator.Identity.Dimensions,
            snapshot.ChunkCount,
            _clock.UtcNow);

        var addResult = await _embeddingSetRepository.AddIfAbsentAsync(
            embeddingSet, embeddingInputs,
            command.ProjectId, command.OrganizationId, cancellationToken);

        return addResult.Status switch
        {
            DocumentEmbeddingSetAddStatus.Added =>
                EnsureDocumentEmbeddingsResult.SuccessCreated(addResult.EmbeddingSet!),
            DocumentEmbeddingSetAddStatus.NotFound =>
                EnsureDocumentEmbeddingsResult.NotFound(),
            DocumentEmbeddingSetAddStatus.AlreadyExists =>
                ValidateCompatibility(addResult.EmbeddingSet!, snapshot),
            _ => throw new InvalidOperationException(
                $"Unexpected add result status: {addResult.Status}"),
        };
    }

    private void ValidateGeneratorProfile()
    {
        var identity = _generator.Identity;

        if (identity.ProfileId != EmbeddingProfiles.SemanticV1Id)
        {
            throw new InvalidOperationException(
                $"Generator profile '{identity.ProfileId}' is not the supported profile " +
                $"'{EmbeddingProfiles.SemanticV1Id}'.");
        }

        if (identity.ModelId != EmbeddingProfiles.SemanticV1ModelId)
        {
            throw new InvalidOperationException(
                $"Generator model '{identity.ModelId}' is not the supported model " +
                $"'{EmbeddingProfiles.SemanticV1ModelId}'.");
        }

        if (identity.Dimensions != EmbeddingProfiles.SemanticV1Dimensions)
        {
            throw new InvalidOperationException(
                $"Generator dimensions {identity.Dimensions} do not match the supported " +
                $"profile dimensions {EmbeddingProfiles.SemanticV1Dimensions}.");
        }
    }

    private EnsureDocumentEmbeddingsResult ValidateCompatibility(
        DocumentEmbeddingSet existing, DocumentChunkSnapshot snapshot)
    {
        var identity = _generator.Identity;

        if (existing.ProfileId != identity.ProfileId
            || existing.ModelId != identity.ModelId
            || existing.Dimensions != identity.Dimensions
            || existing.ChunkingVersion != snapshot.ChunkingVersion
            || existing.EmbeddingCount != snapshot.ChunkCount)
        {
            return EnsureDocumentEmbeddingsResult.InvariantConflict(existing);
        }

        return EnsureDocumentEmbeddingsResult.SuccessExisting(existing);
    }

    private static void ValidateSnapshotCompleteness(DocumentChunkSnapshot snapshot, Guid documentId)
    {
        if (snapshot.DocumentId != documentId)
        {
            throw new InvalidOperationException(
                $"Snapshot DocumentId {snapshot.DocumentId} does not match command DocumentId {documentId}.");
        }

        if (snapshot.ChunkCount != snapshot.Chunks.Count)
        {
            throw new InvalidOperationException(
                $"Snapshot ChunkCount ({snapshot.ChunkCount}) does not match actual chunk count ({snapshot.Chunks.Count}).");
        }

        for (int i = 0; i < snapshot.Chunks.Count; i++)
        {
            var chunk = snapshot.Chunks[i];

            if (chunk.ChunkIndex != i)
            {
                throw new InvalidOperationException(
                    $"Expected ChunkIndex {i} but found {chunk.ChunkIndex} at position {i}.");
            }

            if (chunk.ChunkId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"ChunkId at index {i} is empty.");
            }
        }
    }

    private void ValidateGeneratorOutput(
        IReadOnlyList<ReadOnlyMemory<float>> vectors,
        DocumentChunkSnapshot snapshot)
    {
        if (vectors.Count != snapshot.ChunkCount)
        {
            throw new InvalidOperationException(
                $"Generator returned {vectors.Count} vectors but expected {snapshot.ChunkCount}.");
        }

        var expectedDimensions = _generator.Identity.Dimensions;

        for (int i = 0; i < vectors.Count; i++)
        {
            var vector = vectors[i];

            if (vector.Length != expectedDimensions)
            {
                throw new InvalidOperationException(
                    $"Vector at index {i} has {vector.Length} dimensions but expected {expectedDimensions}.");
            }

            var span = vector.Span;
            for (int j = 0; j < span.Length; j++)
            {
                if (!float.IsFinite(span[j]))
                {
                    throw new InvalidOperationException(
                        $"Vector at index {i} contains non-finite value {span[j]} at component {j}.");
                }
            }
        }
    }
}
