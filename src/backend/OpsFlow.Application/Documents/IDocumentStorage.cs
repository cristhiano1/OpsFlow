namespace OpsFlow.Application.Documents;

/// <summary>
/// Storage port for document bytes. Infrastructure provides the physical
/// implementation. The address is a trusted server-generated identifier —
/// no user-controlled path component is ever accepted.
/// </summary>
public interface IDocumentStorage
{
    /// <summary>Persists the content stream at the location identified by <paramref name="address"/>.</summary>
    Task SaveAsync(DocumentStorageAddress address, Stream content, CancellationToken cancellationToken);

    /// <summary>Deletes the object at the location identified by <paramref name="address"/>. Idempotent when the object does not exist.</summary>
    Task DeleteAsync(DocumentStorageAddress address, CancellationToken cancellationToken);
}
