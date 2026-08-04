using OpsFlow.Infrastructure.Authentication;

namespace OpsFlow.Api.IntegrationTests.TestSupport;

internal sealed class FixedRefreshTokenGenerator(string token) : IRefreshTokenGenerator
{
    public string Generate() => token;
}
