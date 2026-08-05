using OpsFlow.Application.Authentication;

namespace OpsFlow.Api.IntegrationTests.TestSupport;

/// <summary>
/// Test double for <see cref="IAccessTokenService"/> that throws a controlled
/// exception from <see cref="CreateAccessToken"/>. Used to prove that
/// RefreshSessionRotator mints the JWT INSIDE the transaction and rolls back
/// every refresh-rotation write when access-token generation fails.
/// </summary>
internal sealed class ThrowingAccessTokenService(Exception exceptionToThrow) : IAccessTokenService
{
    public int CallCount { get; private set; }

    public AccessTokenResult CreateAccessToken(AccessTokenDescriptor descriptor)
    {
        CallCount++;
        throw exceptionToThrow;
    }
}
