namespace OpsFlow.Application.Documents;

/// <summary>
/// Orchestrates the full document ingestion pipeline: upload → extract text →
/// chunk → generate embeddings. Delegates each step to the existing single-
/// responsibility services. All downstream services are idempotent via
/// <c>AddIfAbsentAsync</c>, so partial-failure retry is safe.
/// </summary>
public sealed class IngestDocumentService
{
    private readonly UploadDocumentService _uploadService;
    private readonly ExtractDocumentTextService _extractService;
    private readonly EnsureDocumentChunksService _chunksService;
    private readonly EnsureDocumentEmbeddingsService _embeddingsService;

    /// <summary>Creates the service with its collaborators.</summary>
    public IngestDocumentService(
        UploadDocumentService uploadService,
        ExtractDocumentTextService extractService,
        EnsureDocumentChunksService chunksService,
        EnsureDocumentEmbeddingsService embeddingsService)
    {
        ArgumentNullException.ThrowIfNull(uploadService);
        ArgumentNullException.ThrowIfNull(extractService);
        ArgumentNullException.ThrowIfNull(chunksService);
        ArgumentNullException.ThrowIfNull(embeddingsService);

        _uploadService = uploadService;
        _extractService = extractService;
        _chunksService = chunksService;
        _embeddingsService = embeddingsService;
    }

    /// <summary>
    /// Uploads the document and runs the full indexing pipeline. Returns a
    /// result that distinguishes upload failures from indexing failures.
    /// Provider exceptions (e.g. <see cref="EmbeddingGenerationException"/>)
    /// propagate to the caller.
    /// </summary>
    public async Task<IngestDocumentResult> IngestAsync(
        UploadDocumentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var uploadResult = await _uploadService.UploadAsync(command, cancellationToken);

        if (!uploadResult.ProjectFound)
        {
            return IngestDocumentResult.ProjectNotFound();
        }

        if (!uploadResult.Succeeded)
        {
            return IngestDocumentResult.UploadValidationFailed(uploadResult.Error!);
        }

        var doc = uploadResult.Document!;
        var orgId = command.OrganizationId;
        var projectId = command.ProjectId;

        var extractResult = await _extractService.ExtractAsync(
            new ExtractDocumentTextCommand(orgId, projectId, doc.Id),
            cancellationToken);

        var extractionFailure = MapExtractionFailure(extractResult.Status);
        if (extractionFailure is not null)
        {
            return IngestDocumentResult.IndexingFailed(doc, extractionFailure.Value);
        }

        var chunkResult = await _chunksService.EnsureAsync(
            new EnsureDocumentChunksCommand(orgId, projectId, doc.Id),
            cancellationToken);

        if (chunkResult.Status is not (EnsureDocumentChunksStatus.SuccessCreated
            or EnsureDocumentChunksStatus.SuccessExisting))
        {
            throw new InvalidOperationException(
                $"Unexpected chunking status '{chunkResult.Status}' for document {doc.Id} " +
                "that was just uploaded and extracted.");
        }

        var embedResult = await _embeddingsService.EnsureAsync(
            new EnsureDocumentEmbeddingsCommand(orgId, projectId, doc.Id),
            cancellationToken);

        if (embedResult.Status is not (EnsureDocumentEmbeddingsStatus.SuccessCreated
            or EnsureDocumentEmbeddingsStatus.SuccessExisting))
        {
            throw new InvalidOperationException(
                $"Unexpected embedding status '{embedResult.Status}' for document {doc.Id} " +
                "that was just uploaded, extracted, and chunked.");
        }

        return IngestDocumentResult.FullyIngested(doc);
    }

    private static DocumentIngestionFailure? MapExtractionFailure(ExtractDocumentTextStatus status) =>
        status switch
        {
            ExtractDocumentTextStatus.SuccessCreated => null,
            ExtractDocumentTextStatus.SuccessExisting => null,
            ExtractDocumentTextStatus.UnsupportedFormat => DocumentIngestionFailure.ExtractionUnsupportedFormat,
            ExtractDocumentTextStatus.MalformedDocument => DocumentIngestionFailure.ExtractionMalformedDocument,
            ExtractDocumentTextStatus.ExtractionLimitExceeded => DocumentIngestionFailure.ExtractionLimitExceeded,
            ExtractDocumentTextStatus.StorageMissing => DocumentIngestionFailure.ExtractionStorageMissing,
            ExtractDocumentTextStatus.NotFound => throw new InvalidOperationException(
                "Document not found during extraction immediately after a successful upload."),
            _ => throw new InvalidOperationException($"Unknown extraction status: {status}"),
        };
}
