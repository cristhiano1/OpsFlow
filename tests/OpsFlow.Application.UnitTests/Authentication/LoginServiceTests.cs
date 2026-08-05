using OpsFlow.Application.Authentication;
using OpsFlow.Application.Authorization;
using OpsFlow.Application.UnitTests.TestSupport;

namespace OpsFlow.Application.UnitTests.Authentication;

public sealed class LoginServiceTests
{
    private static readonly DateTimeOffset AccessTokenExpiresAt =
        new(2026, 1, 1, 12, 15, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset RefreshTokenExpiresAt =
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private const string ValidAccessToken = "access-token-value";
    private const string ValidRefreshToken = "refresh-token-value";
    private const string SampleConcurrencyStamp = "CONCURRENCY-STAMP-VALUE";

    private static AuthenticatedUser SampleUser(
        IReadOnlyCollection<string>? roles = null,
        string concurrencyStamp = SampleConcurrencyStamp)
    {
        IReadOnlyCollection<string> effectiveRoles = roles ?? [OpsFlowRoles.Viewer];
        return new AuthenticatedUser(
            UserId: Guid.NewGuid(),
            Email: "user@opsflow.local",
            DisplayName: "Test User",
            OrganizationId: Guid.NewGuid(),
            OrganizationName: "Test Organization",
            SecurityStamp: "SECURITY-STAMP-VALUE",
            ConcurrencyStamp: concurrencyStamp,
            Roles: effectiveRoles);
    }

    private static LoginCommand SampleCommand() =>
        new("user@opsflow.local", "correct horse battery staple");

    private static (LoginService Service,
                    FakeUserAuthenticator Authenticator,
                    FakeAccessTokenService AccessTokenService,
                    FakeLoginSessionIssuer SessionIssuer) CreateHappyPath(
        AuthenticatedUser user,
        IList<string>? invocationLog = null)
    {
        var authenticator = new FakeUserAuthenticator(invocationLog)
        {
            ResultToReturn = AuthenticationResult.Success(user),
        };
        var accessTokenService = new FakeAccessTokenService(invocationLog)
        {
            ResultToReturn = new AccessTokenResult(ValidAccessToken, AccessTokenExpiresAt),
        };
        var sessionIssuer = new FakeLoginSessionIssuer(invocationLog)
        {
            ResultToReturn = SessionIssueResult.Issued(ValidRefreshToken, RefreshTokenExpiresAt),
        };
        var service = new LoginService(authenticator, accessTokenService, sessionIssuer);
        return (service, authenticator, accessTokenService, sessionIssuer);
    }

    [Fact]
    public void Constructor_rejects_null_user_authenticator()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new LoginService(null!, new FakeAccessTokenService(), new FakeLoginSessionIssuer()));

        Assert.Equal("userAuthenticator", exception.ParamName);
    }

    [Fact]
    public void Constructor_rejects_null_access_token_service()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new LoginService(new FakeUserAuthenticator(), null!, new FakeLoginSessionIssuer()));

        Assert.Equal("accessTokenService", exception.ParamName);
    }

    [Fact]
    public void Constructor_rejects_null_login_session_issuer()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new LoginService(new FakeUserAuthenticator(), new FakeAccessTokenService(), null!));

        Assert.Equal("loginSessionIssuer", exception.ParamName);
    }

    [Fact]
    public async Task LoginAsync_rejects_null_command()
    {
        var (service, _, _, _) = CreateHappyPath(SampleUser());

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.LoginAsync(null!, CancellationToken.None));

        Assert.Equal("command", exception.ParamName);
    }

    [Fact]
    public async Task LoginAsync_invokes_each_collaborator_once_on_success()
    {
        var (service, authenticator, accessTokenService, sessionIssuer) = CreateHappyPath(SampleUser());

        var result = await service.LoginAsync(SampleCommand(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, authenticator.CallCount);
        Assert.Equal(1, accessTokenService.CallCount);
        Assert.Equal(1, sessionIssuer.CallCount);
    }

    [Fact]
    public async Task LoginAsync_invokes_collaborators_in_authenticate_then_access_then_issue_order()
    {
        var invocationLog = new List<string>();
        var (service, _, _, _) = CreateHappyPath(SampleUser(), invocationLog);

        var result = await service.LoginAsync(SampleCommand(), CancellationToken.None);

        Assert.True(result.Succeeded);
        string[] expectedOrder = ["authenticate", "access", "issue"];
        Assert.Equal(expectedOrder, invocationLog);
    }

    [Fact]
    public async Task LoginAsync_forwards_email_and_password_unchanged_to_the_authenticator()
    {
        var (service, authenticator, _, _) = CreateHappyPath(SampleUser());

        var result = await service.LoginAsync(
            new LoginCommand("User@OpsFlow.Local", "S3cret-P@ss"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("User@OpsFlow.Local", authenticator.ReceivedEmail);
        Assert.Equal("S3cret-P@ss", authenticator.ReceivedPassword);
    }

    [Fact]
    public async Task LoginAsync_passes_complete_user_data_including_the_security_stamp_to_the_access_token_descriptor()
    {
        string[] roles = [OpsFlowRoles.Coordinator, OpsFlowRoles.Technician];
        var user = SampleUser(roles);
        var (service, _, accessTokenService, _) = CreateHappyPath(user);

        var result = await service.LoginAsync(SampleCommand(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var descriptor = accessTokenService.ReceivedDescriptor;
        Assert.NotNull(descriptor);
        Assert.Equal(user.UserId, descriptor.UserId);
        Assert.Equal(user.Email, descriptor.Email);
        Assert.Equal(user.DisplayName, descriptor.DisplayName);
        Assert.Equal(user.OrganizationId, descriptor.OrganizationId);
        Assert.Equal(user.SecurityStamp, descriptor.SecurityStamp);
        Assert.Equal(user.Roles, descriptor.Roles);
    }

    [Fact]
    public async Task LoginAsync_passes_userid_organizationid_and_security_stamp_to_the_login_session_request()
    {
        var user = SampleUser();
        var (service, _, _, sessionIssuer) = CreateHappyPath(user);

        var result = await service.LoginAsync(SampleCommand(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = sessionIssuer.ReceivedRequest;
        Assert.NotNull(request);
        Assert.Equal(user.UserId, request.UserId);
        Assert.Equal(user.OrganizationId, request.OrganizationId);
        Assert.Equal(user.SecurityStamp, request.ExpectedSecurityStamp);
    }

    [Fact]
    public async Task LoginAsync_passes_authenticated_concurrency_stamp_as_expected_concurrency_stamp()
    {
        var user = SampleUser(concurrencyStamp: "concurrency-A");
        var (service, _, _, sessionIssuer) = CreateHappyPath(user);

        var result = await service.LoginAsync(SampleCommand(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = sessionIssuer.ReceivedRequest;
        Assert.NotNull(request);
        Assert.Equal("concurrency-A", request.ExpectedConcurrencyStamp);
        Assert.Equal(user.ConcurrencyStamp, request.ExpectedConcurrencyStamp);
    }

    [Fact]
    public async Task LoginAsync_returns_tokens_expirations_and_safe_user_on_success()
    {
        var user = SampleUser();
        var (service, _, _, _) = CreateHappyPath(user);

        var result = await service.LoginAsync(SampleCommand(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(ValidAccessToken, result.AccessToken);
        Assert.Equal(AccessTokenExpiresAt, result.AccessTokenExpiresAt);
        Assert.Equal(ValidRefreshToken, result.RefreshToken);
        Assert.Equal(RefreshTokenExpiresAt, result.RefreshTokenExpiresAt);

        var resultUser = result.User;
        Assert.NotNull(resultUser);
        Assert.Equal(user.UserId, resultUser.UserId);
        Assert.Equal(user.Email, resultUser.Email);
        Assert.Equal(user.DisplayName, resultUser.DisplayName);
        Assert.Equal(user.OrganizationId, resultUser.OrganizationId);
        Assert.Equal(user.OrganizationName, resultUser.OrganizationName);
        Assert.Equal(user.Roles, resultUser.Roles);
    }

    [Fact]
    public void LoginResultUser_does_not_expose_a_security_stamp()
    {
        Assert.Null(typeof(LoginResultUser).GetProperty("SecurityStamp"));
    }

    [Theory]
    [InlineData(AuthenticationStatus.InvalidCredentials)]
    [InlineData(AuthenticationStatus.LockedOut)]
    [InlineData(AuthenticationStatus.UserInactive)]
    [InlineData(AuthenticationStatus.OrganizationInactive)]
    public async Task LoginAsync_returns_failure_and_skips_token_and_session_when_authentication_fails(
        AuthenticationStatus status)
    {
        var authenticator = new FakeUserAuthenticator { ResultToReturn = AuthenticationResult.Failure(status) };
        var accessTokenService = new FakeAccessTokenService();
        var sessionIssuer = new FakeLoginSessionIssuer();
        var service = new LoginService(authenticator, accessTokenService, sessionIssuer);

        var result = await service.LoginAsync(SampleCommand(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.User);
        Assert.Equal(0, accessTokenService.CallCount);
        Assert.Equal(0, sessionIssuer.CallCount);
    }

    [Fact]
    public async Task LoginAsync_returns_failure_when_session_issuance_is_rejected()
    {
        var user = SampleUser();
        var authenticator = new FakeUserAuthenticator { ResultToReturn = AuthenticationResult.Success(user) };
        var accessTokenService = new FakeAccessTokenService
        {
            ResultToReturn = new AccessTokenResult(ValidAccessToken, AccessTokenExpiresAt),
        };
        var sessionIssuer = new FakeLoginSessionIssuer { ResultToReturn = SessionIssueResult.Rejected() };
        var service = new LoginService(authenticator, accessTokenService, sessionIssuer);

        var result = await service.LoginAsync(SampleCommand(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.User);
        Assert.Equal(1, accessTokenService.CallCount);
        Assert.Equal(1, sessionIssuer.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LoginAsync_throws_when_the_access_token_is_missing(string token)
    {
        var user = SampleUser();
        var authenticator = new FakeUserAuthenticator { ResultToReturn = AuthenticationResult.Success(user) };
        var accessTokenService = new FakeAccessTokenService
        {
            ResultToReturn = new AccessTokenResult(token, AccessTokenExpiresAt),
        };
        var sessionIssuer = new FakeLoginSessionIssuer();
        var service = new LoginService(authenticator, accessTokenService, sessionIssuer);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoginAsync(SampleCommand(), CancellationToken.None));

        Assert.Equal(0, sessionIssuer.CallCount);
    }

    [Fact]
    public async Task LoginAsync_throws_when_the_access_token_result_is_null()
    {
        var user = SampleUser();
        var authenticator = new FakeUserAuthenticator { ResultToReturn = AuthenticationResult.Success(user) };
        var accessTokenService = new FakeAccessTokenService { ResultToReturn = null };
        var sessionIssuer = new FakeLoginSessionIssuer();
        var service = new LoginService(authenticator, accessTokenService, sessionIssuer);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoginAsync(SampleCommand(), CancellationToken.None));

        Assert.Equal(0, sessionIssuer.CallCount);
    }

    [Fact]
    public async Task LoginAsync_propagates_authenticator_exceptions()
    {
        var failure = new FakeInfrastructureException("authenticator failed");
        var authenticator = new FakeUserAuthenticator { ExceptionToThrow = failure };
        var accessTokenService = new FakeAccessTokenService();
        var sessionIssuer = new FakeLoginSessionIssuer();
        var service = new LoginService(authenticator, accessTokenService, sessionIssuer);

        var caught = await Assert.ThrowsAsync<FakeInfrastructureException>(() =>
            service.LoginAsync(SampleCommand(), CancellationToken.None));

        Assert.Same(failure, caught);
        Assert.Equal(0, accessTokenService.CallCount);
        Assert.Equal(0, sessionIssuer.CallCount);
    }

    [Fact]
    public async Task LoginAsync_propagates_access_token_service_exceptions()
    {
        var failure = new FakeInfrastructureException("access-token service failed");
        var user = SampleUser();
        var authenticator = new FakeUserAuthenticator { ResultToReturn = AuthenticationResult.Success(user) };
        var accessTokenService = new FakeAccessTokenService { ExceptionToThrow = failure };
        var sessionIssuer = new FakeLoginSessionIssuer();
        var service = new LoginService(authenticator, accessTokenService, sessionIssuer);

        var caught = await Assert.ThrowsAsync<FakeInfrastructureException>(() =>
            service.LoginAsync(SampleCommand(), CancellationToken.None));

        Assert.Same(failure, caught);
        Assert.Equal(0, sessionIssuer.CallCount);
    }

    [Fact]
    public async Task LoginAsync_propagates_session_issuer_exceptions()
    {
        var failure = new FakeInfrastructureException("session issuer failed");
        var user = SampleUser();
        var authenticator = new FakeUserAuthenticator { ResultToReturn = AuthenticationResult.Success(user) };
        var accessTokenService = new FakeAccessTokenService
        {
            ResultToReturn = new AccessTokenResult(ValidAccessToken, AccessTokenExpiresAt),
        };
        var sessionIssuer = new FakeLoginSessionIssuer { ExceptionToThrow = failure };
        var service = new LoginService(authenticator, accessTokenService, sessionIssuer);

        var caught = await Assert.ThrowsAsync<FakeInfrastructureException>(() =>
            service.LoginAsync(SampleCommand(), CancellationToken.None));

        Assert.Same(failure, caught);
    }

    [Fact]
    public async Task LoginAsync_forwards_the_cancellation_token_to_the_authenticator_and_session_issuer()
    {
        var (service, authenticator, _, sessionIssuer) = CreateHappyPath(SampleUser());
        using var cancellationTokenSource = new CancellationTokenSource();

        var result = await service.LoginAsync(SampleCommand(), cancellationTokenSource.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(cancellationTokenSource.Token, authenticator.ReceivedCancellationToken);
        Assert.Equal(cancellationTokenSource.Token, sessionIssuer.ReceivedCancellationToken);
    }
}
