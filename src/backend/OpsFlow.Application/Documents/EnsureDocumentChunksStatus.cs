namespace OpsFlow.Application.Documents;

/// <summary>Distinguishes the outcomes of the ensure-document-chunks use case.</summary>
public enum EnsureDocumentChunksStatus
{
    /// <summary>Chunks were produced and persisted for the first time.</summary>
    SuccessCreated,

    /// <summary>A cached chunk set already existed and was returned.</summary>
    SuccessExisting,

    /// <summary>The document, project, or tenant scope did not match any record.</summary>
    NotFound,

    /// <summary>The document exists but has no text extraction yet.</summary>
    ExtractionNotFound,
}
