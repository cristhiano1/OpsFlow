namespace OpsFlow.Application.Documents;

/// <summary>
/// Port for extracting text from a document content stream. Infrastructure
/// provides format-specific implementations. The caller owns the stream;
/// implementations must not dispose it.
/// </summary>
public interface IDocumentTextExtractor
{
    /// <summary>Returns <c>true</c> when this extractor handles the given canonical content type.</summary>
    bool CanExtract(string canonicalContentType);

    /// <summary>
    /// Extracts text from the content stream up to <paramref name="maxCharacters"/>.
    /// Returns a typed result distinguishing success, malformed input, and limit
    /// exceeded. The extracted text is normalized (CRLF/CR to LF, NUL removed,
    /// outer whitespace trimmed). The caller owns the stream and is responsible
    /// for disposal.
    /// </summary>
    Task<DocumentTextExtractionResult> ExtractAsync(
        Stream content,
        int maxCharacters,
        CancellationToken cancellationToken);
}
