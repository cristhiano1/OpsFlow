using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Authentication;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Authoritative DB read for the /auth/me use case. Loads the caller's user
/// row, applies every activation / lockout / organization / SecurityStamp
/// check, and returns a DB-sourced <see cref="LoginResultUser"/>. All reads
/// are performed with <c>AsNoTracking</c> because the endpoint performs no
/// writes. A blank current SecurityStamp is treated as a failure: an empty
/// stamp in the DB is not a valid session-binding anchor and must not be
/// accepted, even when the presented value happens to be blank as well.
/// <para>
/// The reader deliberately depends on <see cref="UserManager{TUser}"/> only
/// for lockout evaluation and role enumeration (behaviors implemented by the
/// Identity user/role store). No writes flow through the manager on this path.
/// </para>
/// </summary>
internal sealed class CurrentUserReader : ICurrentUserReader
{
    private readonly OpsFlowDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public CurrentUserReader(OpsFlowDbContext db, UserManager<ApplicationUser> userManager)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(userManager);

        _db = db;
        _userManager = userManager;
    }

    public async Task<CurrentUserResult> ReadAsync(
        CurrentUserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        cancellationToken.ThrowIfCancellationRequested();

        if (query.UserId == Guid.Empty)
        {
            return CurrentUserResult.Failure();
        }

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == query.UserId, cancellationToken);

        if (user is null)
        {
            return CurrentUserResult.Failure();
        }

        if (!user.IsActive)
        {
            return CurrentUserResult.Failure();
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            return CurrentUserResult.Failure();
        }

        var currentStamp = user.SecurityStamp;
        if (string.IsNullOrWhiteSpace(currentStamp))
        {
            return CurrentUserResult.Failure();
        }

        if (!string.Equals(currentStamp, query.PresentedSecurityStamp, StringComparison.Ordinal))
        {
            return CurrentUserResult.Failure();
        }

        var organization = await _db.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == user.OrganizationId, cancellationToken);

        if (organization is null)
        {
            return CurrentUserResult.Failure();
        }

        if (!organization.IsActive)
        {
            return CurrentUserResult.Failure();
        }

        var roleNames = await _userManager.GetRolesAsync(user);
        IReadOnlyCollection<string> roles = [.. roleNames];

        return CurrentUserResult.Success(new LoginResultUser(
            UserId: user.Id,
            Email: user.Email ?? string.Empty,
            DisplayName: user.DisplayName,
            OrganizationId: organization.Id,
            OrganizationName: organization.Name,
            Roles: roles));
    }
}
