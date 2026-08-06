using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Authentication;
using OpsFlow.Application.Authorization;
using OpsFlow.Contracts.Authentication;

namespace OpsFlow.Api.Authentication;

/// <summary>Handles authentication endpoints for the OpsFlow API.</summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthenticationController : ControllerBase
{
    /// <summary>Authenticates a user and returns an access token with a secure refresh-token cookie.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginAsync(
        [FromBody] LoginRequest? request,
        [FromServices] LoginService loginService,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return ValidationProblem();
        }

        var result = await loginService.LoginAsync(
            new LoginCommand(request.Email, request.Password),
            cancellationToken);

        if (!result.Succeeded)
        {
            return UnauthorizedWithoutBody();
        }

        if (result.AccessToken is null
            || result.AccessTokenExpiresAt is null
            || result.RefreshToken is null
            || result.RefreshTokenExpiresAt is null
            || result.User is null)
        {
            throw new InvalidOperationException(
                "The login service returned an inconsistent successful result.");
        }

        var user = result.User;
        var response = new LoginResponse(
            result.AccessToken,
            result.AccessTokenExpiresAt.Value,
            new LoginUserResponse(
                user.UserId,
                user.Email,
                user.DisplayName,
                user.OrganizationId,
                user.OrganizationName,
                user.Roles));

        Response.Cookies.Append(
            RefreshTokenCookie.Name,
            result.RefreshToken,
            RefreshTokenCookie.BuildOptions(result.RefreshTokenExpiresAt.Value));

        return Ok(response);
    }

    /// <summary>Rotates the caller's refresh-token session and returns a fresh access token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> RefreshAsync(
        [FromServices] RefreshService refreshService,
        CancellationToken cancellationToken)
    {
        var rawToken = RefreshTokenCookie.ReadFrom(Request);
        if (rawToken is null)
        {
            return UnauthorizedWithoutBody();
        }

        var result = await refreshService.RefreshAsync(
            new RefreshCommand(rawToken),
            cancellationToken);

        if (!result.Succeeded)
        {
            return UnauthorizedWithoutBody();
        }

        if (result.AccessToken is null
            || result.AccessTokenExpiresAt is null
            || result.RefreshToken is null
            || result.RefreshTokenExpiresAt is null
            || result.User is null)
        {
            throw new InvalidOperationException(
                "The refresh service returned an inconsistent successful result.");
        }

        var user = result.User;
        var response = new RefreshResponse(
            result.AccessToken,
            result.AccessTokenExpiresAt.Value,
            new LoginUserResponse(
                user.UserId,
                user.Email,
                user.DisplayName,
                user.OrganizationId,
                user.OrganizationName,
                user.Roles));

        Response.Cookies.Append(
            RefreshTokenCookie.Name,
            result.RefreshToken,
            RefreshTokenCookie.BuildOptions(result.RefreshTokenExpiresAt.Value));

        return Ok(response);
    }

    /// <summary>
    /// Terminates the caller's refresh-token session. The presented refresh
    /// cookie is used to identify the session; the response is always
    /// 204 No Content on successful handling. The browser cookie is deleted
    /// only after the logout service completes successfully — an
    /// unexpected infrastructure failure propagates without touching the
    /// cookie so the client is not misled about the persisted state.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> LogoutAsync(
        [FromServices] LogoutService logoutService,
        CancellationToken cancellationToken)
    {
        var rawToken = RefreshTokenCookie.ReadFrom(Request);

        await logoutService.LogoutAsync(
            new LogoutCommand(rawToken),
            cancellationToken);

        // Cookie deletion happens ONLY after logout completes successfully.
        // On an unexpected exception above, this line does not execute and
        // the existing UseExceptionHandler produces a neutral 500 without
        // any Set-Cookie.
        RefreshTokenCookie.DeleteFrom(Response);

        return NoContent();
    }

    /// <summary>
    /// Returns the caller's current, authoritative profile resolved from the
    /// database. The endpoint requires a valid access token, never reads the
    /// refresh cookie, never issues tokens, and performs no writes. Any
    /// mismatch between the presented JWT and the current DB state
    /// (missing/invalid <c>sub</c>, missing/blank <c>sstamp</c>, user missing
    /// or disabled or locked, organization missing or disabled, SecurityStamp
    /// change) yields a neutral 401 without indicating the reason. The
    /// response is marked non-storable so intermediaries and browsers cannot
    /// serve a cached copy in place of the authoritative DB check.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> MeAsync(
        [FromServices] CurrentUserService currentUserService,
        CancellationToken cancellationToken)
    {
        var subjectClaim = User.FindFirst(OpsFlowClaimTypes.Subject)?.Value;
        if (string.IsNullOrWhiteSpace(subjectClaim)
            || !Guid.TryParse(subjectClaim, out var userId)
            || userId == Guid.Empty)
        {
            return UnauthorizedWithoutBody();
        }

        var presentedSecurityStamp = User.FindFirst(OpsFlowClaimTypes.SecurityStamp)?.Value;
        if (string.IsNullOrWhiteSpace(presentedSecurityStamp))
        {
            return UnauthorizedWithoutBody();
        }

        var result = await currentUserService.GetCurrentUserAsync(
            new CurrentUserQuery(userId, presentedSecurityStamp),
            cancellationToken);

        if (!result.Succeeded || result.User is null)
        {
            return UnauthorizedWithoutBody();
        }

        var user = result.User;
        var response = new LoginUserResponse(
            user.UserId,
            user.Email,
            user.DisplayName,
            user.OrganizationId,
            user.OrganizationName,
            user.Roles);

        return Ok(response);
    }

    private EmptyResult UnauthorizedWithoutBody()
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return new EmptyResult();
    }
}
