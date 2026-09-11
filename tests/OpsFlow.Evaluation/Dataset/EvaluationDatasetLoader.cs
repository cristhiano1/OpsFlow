using System.Reflection;
using System.Text.Json;

namespace OpsFlow.Evaluation.Dataset;

/// <summary>
/// Loads and validates <see cref="EvaluationDataset"/> instances from JSON.
/// Deserialization is deterministic and never reaches the network or the file
/// system by default; the versioned synthetic dataset ships as an embedded
/// assembly resource so tests do not depend on the process working directory.
/// Validation runs automatically after every load — there is no unchecked path.
/// </summary>
public static class EvaluationDatasetLoader
{
    /// <summary>Resource file name of the versioned synthetic dataset.</summary>
    public const string SyntheticV1ResourceFileName = "opsflow-retrieval-synthetic-v1.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Deserializes and validates a dataset from a JSON stream. Malformed JSON
    /// or a semantic contract violation throws <see cref="EvaluationDatasetException"/>.
    /// </summary>
    public static EvaluationDataset Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        EvaluationDataset? dataset;
        try
        {
            dataset = JsonSerializer.Deserialize<EvaluationDataset>(stream, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new EvaluationDatasetException("The evaluation dataset JSON is malformed.", ex);
        }

        if (dataset is null)
        {
            throw new EvaluationDatasetException("The evaluation dataset JSON deserialized to null.");
        }

        EvaluationDatasetValidator.Validate(dataset);
        return dataset;
    }

    /// <summary>
    /// Deserializes and validates a dataset from a JSON string. Intended for
    /// tests that supply inline JSON fixtures.
    /// </summary>
    public static EvaluationDataset LoadFromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        EvaluationDataset? dataset;
        try
        {
            dataset = JsonSerializer.Deserialize<EvaluationDataset>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new EvaluationDatasetException("The evaluation dataset JSON is malformed.", ex);
        }

        if (dataset is null)
        {
            throw new EvaluationDatasetException("The evaluation dataset JSON deserialized to null.");
        }

        EvaluationDatasetValidator.Validate(dataset);
        return dataset;
    }

    /// <summary>
    /// Loads the embedded, versioned synthetic dataset
    /// (<c>opsflow-retrieval-synthetic-v1</c>). Deterministic and independent of
    /// the working directory.
    /// </summary>
    public static EvaluationDataset LoadSyntheticV1()
    {
        var assembly = typeof(EvaluationDatasetLoader).Assembly;
        var resourceName = ResolveResourceName(assembly, SyntheticV1ResourceFileName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new EvaluationDatasetException(
                $"Embedded evaluation dataset resource '{resourceName}' could not be opened.");

        return Load(stream);
    }

    private static string ResolveResourceName(Assembly assembly, string fileName)
    {
        var matches = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(fileName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
        {
            throw new EvaluationDatasetException(
                $"No embedded resource ending with '{fileName}' was found in assembly '{assembly.GetName().Name}'.");
        }

        if (matches.Count > 1)
        {
            throw new EvaluationDatasetException(
                $"Multiple embedded resources end with '{fileName}'; the dataset resource name is ambiguous.");
        }

        return matches[0];
    }
}
