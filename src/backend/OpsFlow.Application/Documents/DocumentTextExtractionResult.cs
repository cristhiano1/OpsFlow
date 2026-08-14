namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of a single extractor invocation. Factory methods enforce valid
/// combinations of outcome and extracted text.
/// </summary>
public sealed class DocumentTextExtractionResult
{
    /// <summary>The outcome of the extraction attempt.</summary>
    public DocumentTextExtractionOutcome Outcome { get; private set; }

    /// <summary>The extracted text. Only meaningful when <see cref="Outcome"/> is <see cref="DocumentTextExtractionOutcome.Success"/>.</summary>
    public string? Text { get; private set; }

    private DocumentTextExtractionResult() { }

    /// <summary>Creates a successful result with extracted text.</summary>
    public static DocumentTextExtractionResult Success(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new DocumentTextExtractionResult
        {
            Outcome = DocumentTextExtractionOutcome.Success,
            Text = text,
        };
    }

    /// <summary>The document bytes are malformed or unreadable.</summary>
    public static DocumentTextExtractionResult MalformedDocument() =>
        new() { Outcome = DocumentTextExtractionOutcome.MalformedDocument };

    /// <summary>The extracted text exceeds the configured character limit.</summary>
    public static DocumentTextExtractionResult LimitExceeded() =>
        new() { Outcome = DocumentTextExtractionOutcome.LimitExceeded };
}
