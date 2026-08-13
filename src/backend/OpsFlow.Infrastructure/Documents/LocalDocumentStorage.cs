using Microsoft.Extensions.Options;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.Documents;

/// <summary>
/// Stores document bytes on the local filesystem. The physical path is derived
/// entirely from trusted server-generated GUIDs — no user-controlled input
/// influences the storage location.
/// </summary>
public sealed class LocalDocumentStorage : IDocumentStorage
{
    private readonly string _basePath;

    /// <summary>Creates the storage implementation with the configured base path.</summary>
    public LocalDocumentStorage(IOptions<DocumentStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var basePath = options.Value.BasePath;

        if (string.IsNullOrWhiteSpace(basePath))
        {
            throw new InvalidOperationException(
                "DocumentStorage:BasePath is not configured. Set it in appsettings or environment variables.");
        }

        _basePath = Path.GetFullPath(basePath);
    }

    /// <inheritdoc />
    public async Task SaveAsync(DocumentStorageAddress address, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(content);

        var finalPath = ResolvePath(address);
        var directory = Path.GetDirectoryName(finalPath)!;
        _ = Directory.CreateDirectory(directory);

        if (File.Exists(finalPath))
        {
            throw new InvalidOperationException($"Storage object already exists at the target location.");
        }

        var tempPath = Path.Combine(directory, $".tmp-{Guid.NewGuid():N}");

        try
        {
            await using var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            await content.CopyToAsync(fs, cancellationToken);
            await fs.FlushAsync(cancellationToken);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        try
        {
            File.Move(tempPath, finalPath);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(DocumentStorageAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolvePath(address);

        try
        {
            Stream stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 81920,
                });

            return Task.FromResult<Stream?>(stream);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(DocumentStorageAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        var path = ResolvePath(address);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(DocumentStorageAddress address)
    {
        var relative = Path.Combine(
            address.OrganizationId.ToString("N"),
            address.ProjectId.ToString("N"),
            address.DocumentId.ToString("N"));

        var fullPath = Path.GetFullPath(Path.Combine(_basePath, relative));

        if (!fullPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Resolved path escapes the configured storage root.");
        }

        return fullPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup — do not mask the original exception.
        }
    }
}
