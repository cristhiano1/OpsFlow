namespace OpsFlow.Evaluation.Dataset;

/// <summary>
/// Raised when an evaluation dataset is malformed — either structurally invalid
/// JSON or a semantic contract violation detected by
/// <see cref="EvaluationDatasetValidator"/>. Messages identify the offending
/// key or case but never contain secrets or environment-specific paths.
/// </summary>
public sealed class EvaluationDatasetException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public EvaluationDatasetException()
    {
    }

    /// <summary>Creates the exception with a descriptive message.</summary>
    public EvaluationDatasetException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a descriptive message and inner cause.</summary>
    public EvaluationDatasetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
