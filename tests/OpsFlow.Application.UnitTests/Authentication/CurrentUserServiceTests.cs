using OpsFlow.Application.Authentication;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Authentication;

public sealed class CurrentUserServiceTests
{
    private static readonly Guid SampleUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string SampleSecurityStamp = "sample-security-stamp-value";
    private static readonly string[] SampleRoles = ["Coordinator"];

    private static LoginResultUser BuildUser()
        => new(
            UserId: SampleUserId,
            Email: "user@test.local",
            DisplayName: "Test User",
            OrganizationId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            OrganizationName: "Test Organization",
            Roles: SampleRoles);

    [Fact]
    public void Constructor_rejects_null_reader()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new CurrentUserService(null!));
        Assert.Equal("currentUserReader", exception.ParamName);
    }

    [Fact]
    public async Task GetCurrentUserAsync_rejects_null_query()
    {
        var service = new CurrentUserService(new FakeCurrentUserReader());
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.GetCurrentUserAsync(null!, CancellationToken.None));
        Assert.Equal("query", exception.ParamName);
    }

    [Fact]
    public async Task GetCurrentUserAsync_forwards_query_and_cancellation_token()
    {
        var reader = new FakeCurrentUserReader
        {
            ResultToReturn = CurrentUserResult.Success(BuildUser()),
        };
        var service = new CurrentUserService(reader);
        using var cts = new CancellationTokenSource();
        var query = new CurrentUserQuery(SampleUserId, SampleSecurityStamp);

        _ = await service.GetCurrentUserAsync(query, cts.Token);

        Assert.Equal(1, reader.CallCount);
        Assert.Same(query, reader.ReceivedQuery);
        Assert.Equal(cts.Token, reader.ReceivedCancellationToken);
    }

    [Fact]
    public async Task GetCurrentUserAsync_returns_reader_success_result_unchanged()
    {
        var user = BuildUser();
        var reader = new FakeCurrentUserReader
        {
            ResultToReturn = CurrentUserResult.Success(user),
        };
        var service = new CurrentUserService(reader);

        var result = await service.GetCurrentUserAsync(
            new CurrentUserQuery(SampleUserId, SampleSecurityStamp), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Same(user, result.User);
    }

    [Fact]
    public async Task GetCurrentUserAsync_returns_reader_failure_result_unchanged()
    {
        var reader = new FakeCurrentUserReader
        {
            ResultToReturn = CurrentUserResult.Failure(),
        };
        var service = new CurrentUserService(reader);

        var result = await service.GetCurrentUserAsync(
            new CurrentUserQuery(SampleUserId, SampleSecurityStamp), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task GetCurrentUserAsync_propagates_reader_exceptions()
    {
        var failure = new FakeInfrastructureException("reader failed");
        var reader = new FakeCurrentUserReader { ExceptionToThrow = failure };
        var service = new CurrentUserService(reader);

        var caught = await Assert.ThrowsAsync<FakeInfrastructureException>(() =>
            service.GetCurrentUserAsync(
                new CurrentUserQuery(SampleUserId, SampleSecurityStamp), CancellationToken.None));

        Assert.Same(failure, caught);
    }
}
