using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authentication;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Revalidates the authenticated state inside a RepeatableRead transaction and
/// persists the successful-login writes atomically: access-failed-count reset,
/// last-login timestamp, and refresh-token hash. The raw refresh token is
/// returned only through <see cref="SessionIssueResult.Issued"/> and is never
/// persisted or logged.
/// <para>
/// The transaction begins by acquiring a SQL Server UPDLOCK on the user row
/// through <see cref="SqlServerAuthenticationLocks.AcquireUserUpdateLockAsync"/>.
/// Serializing same-user writers this way eliminates the classic
/// shared-to-exclusive lock conversion deadlock between two concurrent
/// successful logins for the same user.
/// </para>
/// <para>
/// The successful-login state is written by directly assigning
/// <c>AccessFailedCount</c> and <c>LastLoginAt</c> on the tracked entity,
/// deliberately bypassing <see cref="UserManager{TUser}.ResetAccessFailedCountAsync"/>.
/// Any Identity call that flows through <c>UserStore.UpdateAsync</c> rotates
/// <c>ConcurrencyStamp</c>; rotating it here would self-invalidate any
/// concurrent same-user login whose snapshot predates this commit. Direct
/// assignment is safe in this narrowly scoped path because the transaction,
/// the user-row UPDLOCK, and the fresh reload together guarantee that no
/// competing writer can update the row until we commit.
/// </para>
/// <para>
/// Revalidating <c>ConcurrencyStamp</c> catches role/password/email/lockout
/// mutations that Identity performed via <c>UserStore.UpdateAsync</c> between
/// authentication and session issuance. Role mutations in OpsFlow must flow
/// through <see cref="UserManager{TUser}"/> so that the user row's
/// <c>ConcurrencyStamp</c> is rotated; direct SQL modifications of
/// <c>AspNetUserRoles</c> would bypass this revalidation and are not part of
/// the supported authentication design.
/// </para>
/// </summary>
internal sealed class LoginSessionIssuer : ILoginSessionIssuer
{
    private readonly OpsFlowDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IRefreshTokenGenerator _tokenGenerator;
    private readonly IRefreshTokenHasher _tokenHasher;
    private readonly IClock _clock;
    private readonly JwtOptions _jwtOptions;

    public LoginSessionIssuer(
        OpsFlowDbContext db,
        UserManager<ApplicationUser> userManager,
        IRefreshTokenGenerator tokenGenerator,
        IRefreshTokenHasher tokenHasher,
        IClock clock,
        IOptions<JwtOptions> jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(tokenGenerator);
        ArgumentNullException.ThrowIfNull(tokenHasher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        _db = db;
        _userManager = userManager;
        _tokenGenerator = tokenGenerator;
        _tokenHasher = tokenHasher;
        _clock = clock;
        _jwtOptions = jwtOptions.Value;
    }

    public async Task<SessionIssueResult> IssueAsync(
        LoginSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        await using var transaction = await _db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        // Serialize same-user writers before any read so that a second
        // concurrent transaction cannot escalate a shared lock into a deadlock
        // when it later needs an exclusive lock for the successful-login UPDATE.
        await SqlServerAuthenticationLocks
            .AcquireUserUpdateLockAsync(_db, request.UserId, cancellationToken);

        var user = await ReloadOrQueryUserAsync(request.UserId, cancellationToken);
        if (user is null)
        {
            return SessionIssueResult.Rejected();
        }

        if (!user.IsActive)
        {
            return SessionIssueResult.Rejected();
        }

        if (user.OrganizationId != request.OrganizationId)
        {
            return SessionIssueResult.Rejected();
        }

        var organization = await _db.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == user.OrganizationId, cancellationToken);

        if (organization is null || !organization.IsActive)
        {
            return SessionIssueResult.Rejected();
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            return SessionIssueResult.Rejected();
        }

        var currentStamp = user.SecurityStamp;
        if (string.IsNullOrWhiteSpace(currentStamp))
        {
            return SessionIssueResult.Rejected();
        }

        if (!string.Equals(currentStamp, request.ExpectedSecurityStamp, StringComparison.Ordinal))
        {
            return SessionIssueResult.Rejected();
        }

        var currentConcurrencyStamp = user.ConcurrencyStamp;
        if (string.IsNullOrWhiteSpace(currentConcurrencyStamp))
        {
            return SessionIssueResult.Rejected();
        }

        if (!string.Equals(currentConcurrencyStamp, request.ExpectedConcurrencyStamp, StringComparison.Ordinal))
        {
            return SessionIssueResult.Rejected();
        }

        var now = _clock.UtcNow;

        // Direct tracked-property assignment: EF Core includes ConcurrencyStamp
        // in the UPDATE predicate (optimistic concurrency check) but does not
        // modify it in the SET clause, so a successful login does not rotate
        // the stamp. See the type-level remarks for why this is required.
        user.AccessFailedCount = 0;
        user.LastLoginAt = now;

        var rawToken = _tokenGenerator.Generate();
        var tokenHash = _tokenHasher.Hash(rawToken);
        var expiresAt = now.AddDays(_jwtOptions.RefreshTokenLifetimeDays);

        _ = _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenHash,
            TokenFamilyId = Guid.NewGuid(),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });

        _ = await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return SessionIssueResult.Issued(rawToken, expiresAt);
    }

    private async Task<ApplicationUser?> ReloadOrQueryUserAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var trackedEntry = _db.ChangeTracker.Entries<ApplicationUser>()
            .FirstOrDefault(e => e.Entity.Id == userId);

        if (trackedEntry is not null)
        {
            await trackedEntry.ReloadAsync(cancellationToken);
            if (trackedEntry.State is EntityState.Deleted or EntityState.Detached)
            {
                return null;
            }

            return trackedEntry.Entity;
        }

        return await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }
}
