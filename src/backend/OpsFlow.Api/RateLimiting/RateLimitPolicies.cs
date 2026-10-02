namespace OpsFlow.Api.RateLimiting;

public static class RateLimitPolicies
{
    public const string AuthStrict = "AuthStrict";
    public const string ApiStandard = "ApiStandard";
    public const string RagExpensive = "RagExpensive";
    public const string Upload = "Upload";
}
