namespace OpsFlow.Evaluation.Dataset;

/// <summary>
/// Validates the structural and semantic integrity of an
/// <see cref="EvaluationDataset"/>. Fails closed with an
/// <see cref="EvaluationDatasetException"/> that names the offending key or
/// case; it never silently corrects or de-duplicates malformed data, because a
/// silently-repaired dataset could hide a benchmark defect.
/// </summary>
public static class EvaluationDatasetValidator
{
    /// <summary>Lowest permitted relevance grade in a case's relevance map.</summary>
    public const int MinGrade = 1;

    /// <summary>Highest permitted relevance grade.</summary>
    public const int MaxGrade = 3;

    /// <summary>
    /// Validates the dataset, throwing <see cref="EvaluationDatasetException"/>
    /// on the first violation encountered.
    /// </summary>
    public static void Validate(EvaluationDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (string.IsNullOrWhiteSpace(dataset.DatasetVersion))
        {
            throw new EvaluationDatasetException("datasetVersion is missing or blank.");
        }

        if (string.IsNullOrWhiteSpace(dataset.DatasetId))
        {
            throw new EvaluationDatasetException("datasetId is missing or blank.");
        }

        if (dataset.Documents is null || dataset.Documents.Count == 0)
        {
            throw new EvaluationDatasetException("dataset must contain at least one document.");
        }

        if (dataset.Cases is null || dataset.Cases.Count == 0)
        {
            throw new EvaluationDatasetException("dataset must contain at least one case.");
        }

        var corpusChunkKeys = ValidateCorpus(dataset.Documents);
        ValidateCases(dataset.Cases, corpusChunkKeys);
    }

    private static HashSet<string> ValidateCorpus(IReadOnlyList<EvaluationDocument> documents)
    {
        var chunkKeys = new HashSet<string>(StringComparer.Ordinal);
        var documentKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            if (document is null)
            {
                throw new EvaluationDatasetException("dataset contains a null document entry.");
            }

            if (string.IsNullOrWhiteSpace(document.DocumentKey))
            {
                throw new EvaluationDatasetException("a document has a missing or blank documentKey.");
            }

            if (!documentKeys.Add(document.DocumentKey))
            {
                throw new EvaluationDatasetException($"duplicate documentKey '{document.DocumentKey}'.");
            }

            if (document.Chunks is null || document.Chunks.Count == 0)
            {
                throw new EvaluationDatasetException($"document '{document.DocumentKey}' has no chunks.");
            }

            foreach (var chunk in document.Chunks)
            {
                if (chunk is null)
                {
                    throw new EvaluationDatasetException(
                        $"document '{document.DocumentKey}' contains a null chunk entry.");
                }

                if (string.IsNullOrWhiteSpace(chunk.ChunkKey))
                {
                    throw new EvaluationDatasetException(
                        $"document '{document.DocumentKey}' has a chunk with a missing or blank chunkKey.");
                }

                if (string.IsNullOrWhiteSpace(chunk.Text))
                {
                    throw new EvaluationDatasetException($"chunk '{chunk.ChunkKey}' has missing or blank text.");
                }

                if (!chunkKeys.Add(chunk.ChunkKey))
                {
                    throw new EvaluationDatasetException(
                        $"duplicate chunkKey '{chunk.ChunkKey}'; chunk keys must be unique across the whole dataset.");
                }
            }
        }

        return chunkKeys;
    }

    private static void ValidateCases(IReadOnlyList<EvaluationCase> cases, HashSet<string> corpusChunkKeys)
    {
        var caseIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var evaluationCase in cases)
        {
            if (evaluationCase is null)
            {
                throw new EvaluationDatasetException("dataset contains a null case entry.");
            }

            if (string.IsNullOrWhiteSpace(evaluationCase.CaseId))
            {
                throw new EvaluationDatasetException("a case has a missing or blank caseId.");
            }

            if (!caseIds.Add(evaluationCase.CaseId))
            {
                throw new EvaluationDatasetException($"duplicate caseId '{evaluationCase.CaseId}'.");
            }

            if (string.IsNullOrWhiteSpace(evaluationCase.Query))
            {
                throw new EvaluationDatasetException($"case '{evaluationCase.CaseId}' has a missing or blank query.");
            }

            if (evaluationCase.Relevance is null || evaluationCase.Relevance.Count == 0)
            {
                throw new EvaluationDatasetException(
                    $"case '{evaluationCase.CaseId}' must declare at least one relevant chunk.");
            }

            foreach (var (chunkKey, grade) in evaluationCase.Relevance)
            {
                if (string.IsNullOrWhiteSpace(chunkKey))
                {
                    throw new EvaluationDatasetException(
                        $"case '{evaluationCase.CaseId}' references a blank chunkKey in its relevance map.");
                }

                if (!corpusChunkKeys.Contains(chunkKey))
                {
                    throw new EvaluationDatasetException(
                        $"case '{evaluationCase.CaseId}' references unknown chunkKey '{chunkKey}'.");
                }

                if (grade < MinGrade)
                {
                    throw new EvaluationDatasetException(
                        $"case '{evaluationCase.CaseId}' assigns grade {grade} to '{chunkKey}'; " +
                        $"grades must be >= {MinGrade} (grade 0 is expressed by omission).");
                }

                if (grade > MaxGrade)
                {
                    throw new EvaluationDatasetException(
                        $"case '{evaluationCase.CaseId}' assigns grade {grade} to '{chunkKey}'; " +
                        $"grades must be <= {MaxGrade}.");
                }
            }
        }
    }
}
