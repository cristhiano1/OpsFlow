namespace OpsFlow.Infrastructure.Configuration;

/// <summary>
/// Configuration for the OpenAI grounded answer generator, bound from the
/// "OpenAI" configuration section (shared with the embedding provider's
/// <see cref="OpenAIEmbeddingOptions"/>). The API key is supplied through user
/// secrets or environment variables — never committed. Unlike the fixed
/// embedding model, the answer model is configurable via
/// <c>OpenAI:AnswerModel</c>.
/// </summary>
public sealed class OpenAIAnswerGenerationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OpenAI";

    /// <summary>OpenAI API key. When absent, answer generation is unavailable.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Chat model used for grounded answer generation. Configurable via
    /// <c>OpenAI:AnswerModel</c>; defaults to a small, low-cost, structured-output
    /// capable model.
    /// </summary>
    public string AnswerModel { get; set; } = "gpt-4o-mini";
}
