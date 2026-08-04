using OpsFlow.Application.Authentication;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeAccessTokenService(IList<string>? invocationLog = null) : IAccessTokenService
{
    public AccessTokenResult? ResultToReturn { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public AccessTokenDescriptor? ReceivedDescriptor { get; private set; }

    public AccessTokenResult CreateAccessToken(AccessTokenDescriptor descriptor)
    {
        CallCount++;
        ReceivedDescriptor = descriptor;
        invocationLog?.Add("access");

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        // The null-forgiving operator lets a test simulate a contract-violating
        // implementation that returns null, without weakening production nullability.
        return ResultToReturn!;
    }
}
