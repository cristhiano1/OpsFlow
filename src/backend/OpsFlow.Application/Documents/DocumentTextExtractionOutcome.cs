namespace OpsFlow.Application.Documents;

/// <summary>Distinguishes the outcomes of a single extractor invocation.</summary>
public enum DocumentTextExtractionOutcome
{
    /// <summary>Text was successfully extracted.</summary>
    Success,

    /// <summary>The document bytes are malformed or unreadable by the parser.</summary>
    MalformedDocument,

    /// <summary>The extracted text exceeds the configured character limit.</summary>
    LimitExceeded,
}
