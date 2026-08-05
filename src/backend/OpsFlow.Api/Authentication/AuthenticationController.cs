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
    private const string RefreshTokenCookieName = "opsflow_refresh_token";

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

        Response.Cookies.Append(RefreshTokenCookieName, result.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/auth",
            Expires = result.RefreshTokenExpiresAt.Value,
        });

        return Ok(response);
    }

    private EmptyResult UnauthorizedWithoutBody()
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return new EmptyResult();
    }
}
