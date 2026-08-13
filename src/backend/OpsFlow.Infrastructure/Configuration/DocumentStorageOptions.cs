namespace OpsFlow.Infrastructure.Configuration;

/// <summary>
/// Configuration for local document storage, bound from the "DocumentStorage"
/// configuration section.
/// </summary>
public sealed class DocumentStorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "DocumentStorage";

    /// <summary>Root directory for stored document files. Resolved against the application content root when relative.</summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Resolves <paramref name="basePath"/> against <paramref name="contentRootPath"/> when
    /// relative, leaving absolute paths and blank values unchanged.
    /// </summary>
    public static string ResolveBasePath(string basePath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || Path.IsPathRooted(basePath))
        {
            return basePath;
        }

        return Path.GetFullPath(basePath, contentRootPath);
    }
}
