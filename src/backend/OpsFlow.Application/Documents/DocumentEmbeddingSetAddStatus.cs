namespace OpsFlow.Application.Documents;

/// <summary>Outcome of an idempotent embedding set insert attempt.</summary>
public enum DocumentEmbeddingSetAddStatus
{
    /// <summary>The embedding set was newly inserted.</summary>
    Added,

    /// <summary>An embedding set for this document and profile already existed.</summary>
    AlreadyExists,

    /// <summary>The document could not be found within the tenant scope.</summary>
    NotFound,
}
