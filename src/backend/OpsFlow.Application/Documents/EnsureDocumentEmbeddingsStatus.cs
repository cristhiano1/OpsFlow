namespace OpsFlow.Application.Documents;

/// <summary>Outcome of the ensure-document-embeddings use case.</summary>
public enum EnsureDocumentEmbeddingsStatus
{
    /// <summary>Embeddings were generated and persisted for the first time.</summary>
    SuccessCreated,

    /// <summary>A compatible embedding set already existed.</summary>
    SuccessExisting,

    /// <summary>The document was not found within the tenant scope.</summary>
    NotFound,

    /// <summary>The document exists but has no chunk set yet.</summary>
    ChunksNotFound,

    /// <summary>An existing embedding set has incompatible metadata.</summary>
    InvariantConflict,
}
