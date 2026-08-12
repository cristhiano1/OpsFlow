using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Result of the list-documents use case. Distinguishes between a project that
/// exists within the caller's organization and one that does not (including
/// cross-tenant projects, which are intentionally indistinguishable from
/// nonexistent projects).
/// </summary>
public sealed class ListDocumentsResult
{
    /// <summary>
    /// <see langword="true"/> when the project was found within the caller's
    /// organization; <see langword="false"/> when the project does not exist or
    /// belongs to a different organization.
    /// </summary>
    public bool ProjectFound { get; private set; }

    /// <summary>
    /// The documents belonging to the project. Only meaningful when
    /// <see cref="ProjectFound"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<Document> Documents { get; private set; } = [];

    private ListDocumentsResult() { }

    /// <summary>Creates a successful result containing the returned documents.</summary>
    public static ListDocumentsResult Success(IReadOnlyList<Document> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return new ListDocumentsResult { ProjectFound = true, Documents = documents };
    }

    /// <summary>
    /// Creates a not-found result. Used when the project does not exist or
    /// belongs to a different organization.
    /// </summary>
    public static ListDocumentsResult ProjectNotFound() =>
        new() { ProjectFound = false };
}
