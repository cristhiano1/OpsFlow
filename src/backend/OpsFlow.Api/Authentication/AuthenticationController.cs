using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Authentication;
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

    private EmptyResult UnauthorizedWithoutBody()
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return new EmptyResult();
    }
}
