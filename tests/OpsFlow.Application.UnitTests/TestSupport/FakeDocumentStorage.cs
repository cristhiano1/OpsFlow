using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentStorage : IDocumentStorage
{
    public List<DocumentStorageAddress> SavedAddresses { get; } = [];
    public DocumentStorageAddress? LastSavedAddress => SavedAddresses.Count > 0 ? SavedAddresses[^1] : null;
    public bool SaveCalled => SavedAddresses.Count > 0;
    public Exception? SaveException { get; set; }

    public List<DocumentStorageAddress> DeletedAddresses { get; } = [];
    public bool DeleteCalled => DeletedAddresses.Count > 0;
    public Exception? DeleteException { get; set; }

    public int[] SharedCallOrder { get; set; } = [0];
    public int SaveCallOrder { get; private set; }

    public Task SaveAsync(DocumentStorageAddress address, Stream content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (SaveException is not null)
        {
            throw SaveException;
        }

        SaveCallOrder = ++SharedCallOrder[0];
        SavedAddresses.Add(address);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(DocumentStorageAddress address, CancellationToken cancellationToken)
    {
        if (DeleteException is not null)
        {
            throw DeleteException;
        }

        DeletedAddresses.Add(address);
        return Task.CompletedTask;
    }
}
