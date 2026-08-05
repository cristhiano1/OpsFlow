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
/// Atomically rotates a refresh-token session. The entire flow — validation,
/// family/reuse handling, user/organization/lockout revalidation,
/// SecurityStamp binding check, current-role read, access-token minting, and
/// successor persistence — runs inside a single RepeatableRead transaction
/// that follows the project-wide User → RefreshToken lock order.
/// <para>
/// A concurrency race is deterministically distinguished from a replay by
/// performing a pre-transaction <c>AsNoTracking</c> lookup and recording
/// whether the token appeared active at that moment. A loser that observed
/// an active token at initial read but sees a rotated token under the lock
/// is treated as a concurrency race (neutral 401, no family revocation). A
/// request that already saw the token revoked at initial read is treated as
/// a replay (family revoked with <see cref="RefreshTokenRevocationReason.ReuseDetected"/>).
/// </para>
/// <para>
/// The refresh access token is minted inside the transaction, BEFORE the
/// commit, so a failure of <see cref="IAccessTokenService"/> rolls back all
/// database changes. A caller can therefore rely on the invariant that a
/// returned <see cref="RefreshRotationStatus.Rotated"/> result means both
/// the new refresh-token row and the new access token were produced from a
/// single transactionally validated snapshot.
/// </para>
/// <para>
/// A missing or blank <c>IssuedSecurityStamp</c> on the reloaded token is
/// treated as a security-state mismatch. Legacy rows are never backfilled
/// with the user's current SecurityStamp, because they might have been
/// issued before a security-sensitive change.
/// </para>
/// </summary>
internal sealed class RefreshSessionRotator : IRefreshSessionRotator
{
    private readonly OpsFlowDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IRefreshTokenGenerator _tokenGenerator;
    private readonly IRefreshTokenHasher _tokenHasher;
    private readonly IAccessTokenService _accessTokenService;
    private readonly IClock _clock;
    private readonly JwtOptions _jwtOptions;

    public RefreshSessionRotator(
        OpsFlowDbContext db,
        UserManager<ApplicationUser> userManager,
        IRefreshTokenGenerator tokenGenerator,
        IRefreshTokenHasher tokenHasher,
        IAccessTokenService accessTokenService,
        IClock clock,
        IOptions<JwtOptions> jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(tokenGenerator);
        ArgumentNullException.ThrowIfNull(tokenHasher);
        ArgumentNullException.ThrowIfNull(accessTokenService);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        _db = db;
        _userManager = userManager;
        _tokenGenerator = tokenGenerator;
        _tokenHasher = tokenHasher;
        _accessTokenService = accessTokenService;
        _clock = clock;
        _jwtOptions = jwtOptions.Value;
    }

    public async Task<RefreshRotationResult> RotateAsync(
        RefreshRotationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.RawRefreshToken))
        {
            return RefreshRotationResult.Rejected();
        }

        var hash = _tokenHasher.Hash(request.RawRefreshToken);
        var now = _clock.UtcNow;

        // Pre-transaction observation to deterministically classify a
        // concurrency race vs. a replay attempt.
        var initial = await _db.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (initial is null)
        {
            return RefreshRotationResult.Rejected();
        }

        var wasActiveAtInitialRead =
            initial.RevokedAt is null && initial.ExpiresAt > now;
        var userId = initial.UserId;

        await using var transaction = await _db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        // Global lock order: User first, then RefreshToken.
        await SqlServerAuthenticationLocks
            .AcquireUserUpdateLockAsync(_db, userId, cancellationToken);
        await SqlServerAuthenticationLocks
            .AcquireRefreshTokenUpdateLockByHashAsync(_db, hash, cancellationToken);

        var reloaded = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (reloaded is null)
        {
            return RefreshRotationResult.Rejected();
        }

        if (reloaded.RevokedAt is not null)
        {
            if (wasActiveAtInitialRead)
            {
                // Legitimate concurrency race: another winner rotated this
                // token between our initial read and our lock acquisition.
                // Return neutral rejection without revoking the family.
                return RefreshRotationResult.Rejected();
            }

            // Real replay: the token was already revoked when we first
            // observed it. Revoke the entire family as a defense in depth.
            await RevokeFamilyAsync(
                reloaded.TokenFamilyId,
                RefreshTokenRevocationReason.ReuseDetected,
                now,
                cancellationToken);
            _ = await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RefreshRotationResult.Rejected();
        }

        if (now >= reloaded.ExpiresAt)
        {
            return RefreshRotationResult.Rejected();
        }

        // Reload the user (may already be tracked from an earlier scope).
        var user = await ReloadOrQueryUserAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return RefreshRotationResult.Rejected();
        }

        var organization = await _db.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == user.OrganizationId, cancellationToken);
        if (organization is null || !organization.IsActive)
        {
            return RefreshRotationResult.Rejected();
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            return RefreshRotationResult.Rejected();
        }

        var currentSecurityStamp = user.SecurityStamp;
        if (string.IsNullOrWhiteSpace(currentSecurityStamp))
        {
            return RefreshRotationResult.Rejected();
        }

        // SecurityStamp binding. A null/blank IssuedSecurityStamp is a
        // legacy row from before the field existed; treat exactly like a
        // real mismatch (do not backfill). Both cases revoke the family.
        if (string.IsNullOrWhiteSpace(reloaded.IssuedSecurityStamp)
            || !string.Equals(reloaded.IssuedSecurityStamp, currentSecurityStamp, StringComparison.Ordinal))
        {
            await RevokeFamilyAsync(
                reloaded.TokenFamilyId,
                RefreshTokenRevocationReason.SecurityChange,
                now,
                cancellationToken);
            _ = await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RefreshRotationResult.Rejected();
        }

        var roles = await _userManager.GetRolesAsync(user);

        // Mint the access token INSIDE the transaction, before the commit,
        // so a failure here rolls back the rotation atomically.
        var accessTokenResult = _accessTokenService.CreateAccessToken(
            new AccessTokenDescriptor(
                user.Id,
                user.Email!,
                user.DisplayName,
                user.OrganizationId,
                [.. roles],
                currentSecurityStamp));

        if (accessTokenResult is null || string.IsNullOrWhiteSpace(accessTokenResult.Token))
        {
            throw new InvalidOperationException(
                "The access-token service returned an invalid token.");
        }

        // Rotate: mark the old token revoked and insert the successor.
        var newTokenId = Guid.NewGuid();
        var newRawToken = _tokenGenerator.Generate();
        var newTokenHash = _tokenHasher.Hash(newRawToken);
        var newExpiresAt = now.AddDays(_jwtOptions.RefreshTokenLifetimeDays);

        reloaded.RevokedAt = now;
        reloaded.ReasonRevoked = RefreshTokenRevocationReason.Rotated;
        reloaded.ReplacedByTokenId = newTokenId;

        _ = _db.RefreshTokens.Add(new RefreshToken
        {
            Id = newTokenId,
            UserId = user.Id,
            TokenHash = newTokenHash,
            TokenFamilyId = reloaded.TokenFamilyId,
            IssuedSecurityStamp = currentSecurityStamp,
            CreatedAt = now,
            ExpiresAt = newExpiresAt,
        });

        _ = await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var resultUser = new LoginResultUser(
            user.Id,
            user.Email!,
            user.DisplayName,
            user.OrganizationId,
            organization.Name,
            [.. roles]);

        return RefreshRotationResult.Rotated(
            accessTokenResult.Token,
            accessTokenResult.ExpiresAt,
            newRawToken,
            newExpiresAt,
            resultUser);
    }

    private async Task RevokeFamilyAsync(
        Guid tokenFamilyId,
        RefreshTokenRevocationReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var familyTokens = await _db.RefreshTokens
            .Where(t => t.TokenFamilyId == tokenFamilyId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in familyTokens)
        {
            token.RevokedAt = now;
            token.ReasonRevoked = reason;
        }
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
