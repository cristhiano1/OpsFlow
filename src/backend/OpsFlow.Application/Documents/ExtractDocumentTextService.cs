using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Coordinates the extract-document-text use case (POST): performs a tenant-
/// scoped metadata lookup, checks for an existing cached extraction, selects
/// the appropriate extractor, opens storage, extracts text with bounded
/// resource limits, normalizes, and persists the result.
/// </summary>
public sealed class ExtractDocumentTextService
{
    /// <summary>Maximum number of characters allowed in a single extraction result.</summary>
    public const int MaxExtractedCharacters = 5_000_000;

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentExtractionRepository _extractionRepository;
    private readonly IDocumentStorage _documentStorage;
    private readonly IEnumerable<IDocumentTextExtractor> _extractors;
    private readonly IClock _clock;

    /// <summary>Creates the service with its dependencies.</summary>
    public ExtractDocumentTextService(
        IDocumentRepository documentRepository,
        IDocumentExtractionRepository extractionRepository,
        IDocumentStorage documentStorage,
        IEnumerable<IDocumentTextExtractor> extractors,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(documentRepository);
        ArgumentNullException.ThrowIfNull(extractionRepository);
        ArgumentNullException.ThrowIfNull(documentStorage);
        ArgumentNullException.ThrowIfNull(extractors);
        ArgumentNullException.ThrowIfNull(clock);

        _documentRepository = documentRepository;
        _extractionRepository = extractionRepository;
        _documentStorage = documentStorage;
        _extractors = extractors;
        _clock = clock;
    }

    /// <summary>Ensures a text extraction exists for the specified document.</summary>
    public async Task<ExtractDocumentTextResult> ExtractAsync(
        ExtractDocumentTextCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(command));
        }

        if (command.ProjectId == Guid.Empty)
        {
            return ExtractDocumentTextResult.NotFound();
        }

        if (command.DocumentId == Guid.Empty)
        {
            return ExtractDocumentTextResult.NotFound();
        }

        var document = await _documentRepository.GetByProjectAsync(
            command.DocumentId, command.ProjectId, command.OrganizationId, cancellationToken);

        if (document is null)
        {
            return ExtractDocumentTextResult.NotFound();
        }

        var existing = await _extractionRepository.GetByDocumentAsync(
            command.DocumentId, command.ProjectId, command.OrganizationId, cancellationToken);

        if (existing is not null)
        {
            return ExtractDocumentTextResult.SuccessExisting(existing);
        }

        var extractor = _extractors.FirstOrDefault(e => e.CanExtract(document.ContentType));
        if (extractor is null)
        {
            return ExtractDocumentTextResult.UnsupportedFormat();
        }

        var address = new DocumentStorageAddress(
            document.OrganizationId, document.ProjectId, document.Id);

        var stream = await _documentStorage.OpenReadAsync(address, cancellationToken);
        if (stream is null)
        {
            return ExtractDocumentTextResult.StorageMissing();
        }

        DocumentTextExtractionResult extractionResult;
        try
        {
            await using (stream)
            {
                extractionResult = await extractor.ExtractAsync(
                    stream, MaxExtractedCharacters, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (extractionResult.Outcome == DocumentTextExtractionOutcome.MalformedDocument)
        {
            return ExtractDocumentTextResult.MalformedDocument();
        }

        if (extractionResult.Outcome == DocumentTextExtractionOutcome.LimitExceeded)
        {
            return ExtractDocumentTextResult.ExtractionLimitExceeded();
        }

        var normalizedText = TextNormalization.Normalize(extractionResult.Text!);

        var extraction = new DocumentExtraction(
            command.DocumentId,
            normalizedText,
            _clock.UtcNow);

        var addResult = await _extractionRepository.AddIfAbsentAsync(
            extraction, command.ProjectId, command.OrganizationId, cancellationToken);

        return addResult.WasAdded
            ? ExtractDocumentTextResult.SuccessCreated(addResult.Extraction)
            : ExtractDocumentTextResult.SuccessExisting(addResult.Extraction);
    }
}
