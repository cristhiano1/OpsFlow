namespace OpsFlow.Application.Authentication;

/// <summary>
/// Coordinates the login use case. It authenticates the supplied credentials,
/// creates the access token in memory, validates it, and only then delegates
/// the atomic successful-login persistence to
/// <see cref="ILoginSessionIssuer"/>. The service remains technology-neutral:
/// it references no HTTP, cookie, persistence, or Identity type and never logs
/// credential or token values.
/// </summary>
public sealed class LoginService
{
    private readonly IUserAuthenticator _userAuthenticator;
    private readonly IAccessTokenService _accessTokenService;
    private readonly ILoginSessionIssuer _loginSessionIssuer;

    /// <summary>Creates the service with its authentication collaborators.</summary>
    /// <param name="userAuthenticator">Verifies credentials and account state.</param>
    /// <param name="accessTokenService">Creates signed access tokens.</param>
    /// <param name="loginSessionIssuer">
    /// Persists the login session and issues the refresh token.
    /// </param>
    public LoginService(
        IUserAuthenticator userAuthenticator,
        IAccessTokenService accessTokenService,
        ILoginSessionIssuer loginSessionIssuer)
    {
        ArgumentNullException.ThrowIfNull(userAuthenticator);
        ArgumentNullException.ThrowIfNull(accessTokenService);
        ArgumentNullException.ThrowIfNull(loginSessionIssuer);

        _userAuthenticator = userAuthenticator;
        _accessTokenService = accessTokenService;
        _loginSessionIssuer = loginSessionIssuer;
    }

    /// <summary>
    /// Authenticates the caller and, on success, creates an access token and
    /// issues a login session. Authentication or session-state rejection
    /// returns the same neutral <see cref="LoginResult.Failure"/> result.
    /// Infrastructure failures propagate as exceptions.
    /// </summary>
    /// <param name="command">The login credentials. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A successful result with tokens, or a neutral failure result.</returns>
    public async Task<LoginResult> LoginAsync(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticationResult =
            await _userAuthenticator.AuthenticateAsync(
                command.Email,
                command.Password,
                cancellationToken);

        if (authenticationResult.Status != AuthenticationStatus.Success
            || authenticationResult.User is null)
        {
            return LoginResult.Failure();
        }

        var user = authenticationResult.User;

        var accessToken = _accessTokenService.CreateAccessToken(
            new AccessTokenDescriptor(
                user.UserId,
                user.Email,
                user.DisplayName,
                user.OrganizationId,
                user.Roles,
                user.SecurityStamp));

        if (accessToken is null || string.IsNullOrWhiteSpace(accessToken.Token))
        {
            throw new InvalidOperationException(
                "The access-token service returned an invalid token.");
        }

        var sessionResult = await _loginSessionIssuer.IssueAsync(
            new LoginSessionRequest(
                user.UserId,
                user.OrganizationId,
                user.SecurityStamp),
            cancellationToken);

        if (sessionResult.Status != SessionIssueStatus.Issued
            || string.IsNullOrWhiteSpace(sessionResult.RefreshToken)
            || sessionResult.RefreshTokenExpiresAt is null)
        {
            return LoginResult.Failure();
        }

        var resultUser = new LoginResultUser(
            user.UserId,
            user.Email,
            user.DisplayName,
            user.OrganizationId,
            user.OrganizationName,
            user.Roles);

        return LoginResult.Success(
            accessToken.Token,
            accessToken.ExpiresAt,
            sessionResult.RefreshToken,
            sessionResult.RefreshTokenExpiresAt.Value,
            resultUser);
    }
}
