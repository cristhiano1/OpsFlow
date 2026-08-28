namespace OpsFlow.Infrastructure.Configuration;

/// <summary>
/// Configuration for the OpenAI embedding provider, bound from the "OpenAI"
/// configuration section. The API key is supplied through user secrets or
/// environment variables — never committed.
/// </summary>
public sealed class OpenAIEmbeddingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OpenAI";

    /// <summary>OpenAI API key. When absent, embedding generation is unavailable.</summary>
    public string? ApiKey { get; set; }
}
