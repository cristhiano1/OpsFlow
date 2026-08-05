using OpsFlow.Application.Authentication;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Authentication;

public sealed class LogoutServiceTests
{
    private const string ValidRawRefreshToken = "raw-refresh-token-value";

    [Fact]
    public void Constructor_rejects_null_revoker()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new LogoutService(null!));
        Assert.Equal("logoutSessionRevoker", exception.ParamName);
    }

    [Fact]
    public async Task LogoutAsync_rejects_null_command()
    {
        var service = new LogoutService(new FakeLogoutSessionRevoker());
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.LogoutAsync(null!, CancellationToken.None));
        Assert.Equal("command", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LogoutAsync_is_a_noop_when_raw_token_is_missing_or_blank(string? rawToken)
    {
        var revoker = new FakeLogoutSessionRevoker();
        var service = new LogoutService(revoker);

        await service.LogoutAsync(new LogoutCommand(rawToken), CancellationToken.None);

        Assert.Equal(0, revoker.CallCount);
    }

    [Fact]
    public async Task LogoutAsync_forwards_raw_token_to_revoker()
    {
        var revoker = new FakeLogoutSessionRevoker();
        var service = new LogoutService(revoker);

        await service.LogoutAsync(new LogoutCommand(ValidRawRefreshToken), CancellationToken.None);

        Assert.Equal(1, revoker.CallCount);
        Assert.NotNull(revoker.ReceivedRequest);
        Assert.Equal(ValidRawRefreshToken, revoker.ReceivedRequest.RawRefreshToken);
    }

    [Fact]
    public async Task LogoutAsync_forwards_cancellation_token()
    {
        var revoker = new FakeLogoutSessionRevoker();
        var service = new LogoutService(revoker);
        using var cts = new CancellationTokenSource();

        await service.LogoutAsync(new LogoutCommand(ValidRawRefreshToken), cts.Token);

        Assert.Equal(cts.Token, revoker.ReceivedCancellationToken);
    }

    [Fact]
    public async Task LogoutAsync_propagates_revoker_exceptions()
    {
        var failure = new FakeInfrastructureException("revoker failed");
        var revoker = new FakeLogoutSessionRevoker { ExceptionToThrow = failure };
        var service = new LogoutService(revoker);

        var caught = await Assert.ThrowsAsync<FakeInfrastructureException>(() =>
            service.LogoutAsync(new LogoutCommand(ValidRawRefreshToken), CancellationToken.None));

        Assert.Same(failure, caught);
    }
}
