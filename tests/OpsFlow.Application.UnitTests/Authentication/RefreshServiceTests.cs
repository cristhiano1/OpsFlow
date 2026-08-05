using OpsFlow.Application.Authentication;
using OpsFlow.Application.Authorization;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Authentication;

public sealed class RefreshServiceTests
{
    private static readonly DateTimeOffset AccessTokenExpiresAt =
        new(2026, 1, 1, 12, 15, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset RefreshTokenExpiresAt =
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private const string ValidRawRefreshToken = "raw-refresh-token-value";
    private const string ValidNewAccessToken = "new-access-token-value";
    private const string ValidNewRefreshToken = "new-refresh-token-value";

    private static LoginResultUser SampleUser() => new(
        UserId: Guid.NewGuid(),
        Email: "user@opsflow.local",
        DisplayName: "Test User",
        OrganizationId: Guid.NewGuid(),
        OrganizationName: "Test Organization",
        Roles: [OpsFlowRoles.Viewer]);

    [Fact]
    public void Constructor_rejects_null_rotator()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new RefreshService(null!));
        Assert.Equal("refreshSessionRotator", exception.ParamName);
    }

    [Fact]
    public async Task RefreshAsync_rejects_null_command()
    {
        var service = new RefreshService(new FakeRefreshSessionRotator());
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.RefreshAsync(null!, CancellationToken.None));
        Assert.Equal("command", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RefreshAsync_returns_failure_when_raw_token_is_blank(string blankToken)
    {
        var rotator = new FakeRefreshSessionRotator();
        var service = new RefreshService(rotator);

        var result = await service.RefreshAsync(new RefreshCommand(blankToken), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.User);
        Assert.Equal(0, rotator.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_returns_failure_when_rotator_rejects()
    {
        var rotator = new FakeRefreshSessionRotator
        {
            ResultToReturn = RefreshRotationResult.Rejected(),
        };
        var service = new RefreshService(rotator);

        var result = await service.RefreshAsync(
            new RefreshCommand(ValidRawRefreshToken), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.User);
        Assert.Equal(1, rotator.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_returns_success_with_rotator_snapshot()
    {
        var user = SampleUser();
        var rotator = new FakeRefreshSessionRotator
        {
            ResultToReturn = RefreshRotationResult.Rotated(
                ValidNewAccessToken,
                AccessTokenExpiresAt,
                ValidNewRefreshToken,
                RefreshTokenExpiresAt,
                user),
        };
        var service = new RefreshService(rotator);

        var result = await service.RefreshAsync(
            new RefreshCommand(ValidRawRefreshToken), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(ValidNewAccessToken, result.AccessToken);
        Assert.Equal(AccessTokenExpiresAt, result.AccessTokenExpiresAt);
        Assert.Equal(ValidNewRefreshToken, result.RefreshToken);
        Assert.Equal(RefreshTokenExpiresAt, result.RefreshTokenExpiresAt);
        Assert.Equal(user, result.User);
    }

    [Fact]
    public async Task RefreshAsync_forwards_raw_token_and_cancellation_to_rotator()
    {
        var rotator = new FakeRefreshSessionRotator
        {
            ResultToReturn = RefreshRotationResult.Rejected(),
        };
        var service = new RefreshService(rotator);
        using var cts = new CancellationTokenSource();

        _ = await service.RefreshAsync(new RefreshCommand(ValidRawRefreshToken), cts.Token);

        Assert.Equal(ValidRawRefreshToken, rotator.ReceivedRequest?.RawRefreshToken);
        Assert.Equal(cts.Token, rotator.ReceivedCancellationToken);
    }

    [Fact]
    public async Task RefreshAsync_propagates_rotator_exceptions()
    {
        var failure = new FakeInfrastructureException("rotator failed");
        var rotator = new FakeRefreshSessionRotator { ExceptionToThrow = failure };
        var service = new RefreshService(rotator);

        var caught = await Assert.ThrowsAsync<FakeInfrastructureException>(() =>
            service.RefreshAsync(new RefreshCommand(ValidRawRefreshToken), CancellationToken.None));

        Assert.Same(failure, caught);
    }
}
