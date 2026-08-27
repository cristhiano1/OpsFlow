using OpsFlow.Domain.Documents;

namespace OpsFlow.Application.Documents;

/// <summary>
/// Immutable identity of an embedding generator. Carries the profile, model,
/// and dimension metadata used for compatibility checks and persistence.
/// </summary>
public sealed record EmbeddingGeneratorIdentity
{
    /// <summary>Product embedding-space compatibility identity.</summary>
    public string ProfileId { get; }

    /// <summary>Provider/model audit identity.</summary>
    public string ModelId { get; }

    /// <summary>Number of float dimensions each vector contains.</summary>
    public int Dimensions { get; }

    /// <summary>Creates a validated identity.</summary>
    public EmbeddingGeneratorIdentity(string profileId, string modelId, int dimensions)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile ID must not be null, empty, or whitespace.", nameof(profileId));
        }

        if (profileId.Length > DocumentEmbeddingSet.MaxProfileIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId),
                $"Profile ID length ({profileId.Length}) exceeds maximum ({DocumentEmbeddingSet.MaxProfileIdLength}).");
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model ID must not be null, empty, or whitespace.", nameof(modelId));
        }

        if (modelId.Length > DocumentEmbeddingSet.MaxModelIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(modelId),
                $"Model ID length ({modelId.Length}) exceeds maximum ({DocumentEmbeddingSet.MaxModelIdLength}).");
        }

        if (dimensions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensions must be >= 1.");
        }

        ProfileId = profileId;
        ModelId = modelId;
        Dimensions = dimensions;
    }
}
