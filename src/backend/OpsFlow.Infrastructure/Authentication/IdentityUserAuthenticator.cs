using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Authentication;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Verifies credentials and account state against ASP.NET Core Identity and
/// returns a technology-neutral <see cref="AuthenticatedUser"/> snapshot.
/// <para>
/// The authenticator performs no application-level successful-login writes:
/// resetting the failed-access count, updating LastLoginAt, and issuing a
/// refresh token belong to <see cref="ILoginSessionIssuer"/>.
/// <see cref="UserManager{TUser}.CheckPasswordAsync"/> may still persist an
/// Identity password-hash upgrade when the configured hasher returns
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>; that secure
/// framework behavior is intentionally preserved.
/// </para>
/// </summary>
internal sealed class IdentityUserAuthenticator : IUserAuthenticator
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly OpsFlowDbContext _db;
    private readonly IDummyPasswordVerifier _dummyPasswordVerifier;

    public IdentityUserAuthenticator(
        UserManager<ApplicationUser> userManager,
        OpsFlowDbContext db,
        IDummyPasswordVerifier dummyPasswordVerifier)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dummyPasswordVerifier);

        _userManager = userManager;
        _db = db;
        _dummyPasswordVerifier = dummyPasswordVerifier;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        // The initial UserManager APIs do not accept a CancellationToken, so
        // honor cancellation explicitly before starting any Identity work.
        cancellationToken.ThrowIfCancellationRequested();

        var user = await _userManager.FindByEmailAsync(email);
        if (user is null)
        {
            _dummyPasswordVerifier.Verify(password);
            return AuthenticationResult.Failure(AuthenticationStatus.InvalidCredentials);
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            // Do not verify against the locked user's real password hash and
            // do not mutate Identity state. The dummy verification keeps this
            // branch's cost close to the real one.
            _dummyPasswordVerifier.Verify(password);
            return AuthenticationResult.Failure(AuthenticationStatus.LockedOut);
        }

        var passwordOk = await _userManager.CheckPasswordAsync(user, password);
        if (!passwordOk)
        {
            var accessFailed = await _userManager.AccessFailedAsync(user);
            if (!accessFailed.Succeeded)
            {
                // A real Identity persistence failure must not be silently
                // reported as invalid credentials. Do not expose Identity
                // error descriptions.
                throw new InvalidOperationException(
                    "The authenticator could not record the failed access attempt.");
            }

            return AuthenticationResult.Failure(AuthenticationStatus.InvalidCredentials);
        }

        if (!user.IsActive)
        {
            return AuthenticationResult.Failure(AuthenticationStatus.UserInactive);
        }

        var organization = await _db.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == user.OrganizationId, cancellationToken);

        if (organization is null || !organization.IsActive)
        {
            return AuthenticationResult.Failure(AuthenticationStatus.OrganizationInactive);
        }

        var securityStamp = await _userManager.GetSecurityStampAsync(user);

        if (string.IsNullOrWhiteSpace(securityStamp))
        {
            // A missing security stamp is an internal server-side inconsistency
            // (Identity did not populate it, or a data-integrity issue). Do not
            // silently map this to InvalidCredentials or UserInactive.
            throw new InvalidOperationException(
                "The authenticator could not obtain the user's security state.");
        }

        var roles = await _userManager.GetRolesAsync(user);

        var snapshot = new AuthenticatedUser(
            UserId: user.Id,
            Email: user.Email!,
            DisplayName: user.DisplayName,
            OrganizationId: user.OrganizationId,
            OrganizationName: organization.Name,
            SecurityStamp: securityStamp,
            Roles: [.. roles]);

        return AuthenticationResult.Success(snapshot);
    }
}
