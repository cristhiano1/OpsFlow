using Microsoft.Extensions.Options;

namespace OpsFlow.Infrastructure.Configuration;

/// <summary>
/// Validates <see cref="BedrockRerankerOptions"/> at startup so a misconfigured
/// reranker fails fast rather than at first use. Messages are static and carry
/// no secrets, credentials, query text, or candidate text — the options
/// intentionally hold no secret material to leak.
/// </summary>
internal sealed class BedrockRerankerOptionsValidator : IValidateOptions<BedrockRerankerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BedrockRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // Region is required and must be a single token. We deliberately do not
        // assert that a syntactically valid region actually offers this model;
        // model availability is an account/region provisioning concern surfaced
        // by Bedrock at call time, not something this validator can truthfully
        // guarantee.
        if (string.IsNullOrWhiteSpace(options.Region))
        {
            failures.Add("BedrockReranker:Region must not be empty.");
        }
        else if (options.Region.Any(char.IsWhiteSpace))
        {
            failures.Add("BedrockReranker:Region must not contain whitespace.");
        }

        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            failures.Add("BedrockReranker:ModelId must not be empty.");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add("BedrockReranker:TimeoutSeconds must be positive.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
