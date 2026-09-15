namespace OpsFlow.Infrastructure.Configuration;

/// <summary>
/// Configuration for the Amazon Bedrock chunk reranker, bound from the
/// "BedrockReranker" configuration section. Deliberately carries no credentials:
/// AWS credentials are resolved exclusively through the AWS SDK default
/// credential chain (environment, shared profile, SSO, ECS task role, or IAM
/// workload identity), never from application configuration. Only non-secret,
/// operational settings live here.
/// </summary>
public sealed class BedrockRerankerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "BedrockReranker";

    /// <summary>
    /// AWS region system name (for example <c>eu-central-1</c>). Used both to
    /// construct the Bedrock client's <c>RegionEndpoint</c> and to build the
    /// foundation-model ARN, so a single value keeps the two consistent.
    /// </summary>
    public string Region { get; set; } = "eu-central-1";

    /// <summary>
    /// Bedrock foundation-model id used for reranking. Defaults to Cohere Rerank
    /// 3.5. Configurable so a manual benchmark can later evaluate an alternative
    /// model (for example <c>amazon.rerank-v1:0</c>) without any code change; the
    /// configured value is surfaced truthfully as the reranker's model identity.
    /// </summary>
    public string ModelId { get; set; } = "cohere.rerank-v3-5:0";

    /// <summary>
    /// OpsFlow per-request timeout, in seconds. It is enforced with a linked
    /// cancellation token around the asynchronous Bedrock rerank operation (the
    /// AWS SDK client timeout does not bound async calls). A local timeout is
    /// treated as a reranker failure, never as caller cancellation.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
