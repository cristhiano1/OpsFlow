using System.Data;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authentication;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// Atomically revokes the refresh-token family associated with the presented
/// refresh token, marking each currently active family member as
/// <see cref="RefreshTokenRevocationReason.Logout"/>. Previously revoked rows
/// are never touched, so an already-Rotated/ReuseDetected/SecurityChange
/// timestamp and reason are preserved verbatim.
/// <para>
/// The operation follows the project-wide User → RefreshToken lock order and
/// runs inside a single RepeatableRead transaction. The authoritative
/// revocation timestamp is captured AFTER both locks have been acquired.
/// </para>
/// <para>
/// The raw refresh token is hashed via <see cref="IRefreshTokenHasher"/> for
/// database lookup and is never persisted or logged. An unknown hash or a
/// token whose row disappeared under contention completes successfully
/// without any database mutation; logout is idempotent from the caller's
/// perspective.
/// </para>
/// </summary>
internal sealed class LogoutSessionRevoker : ILogoutSessionRevoker
{
    private readonly OpsFlowDbContext _db;
    private readonly IRefreshTokenHasher _tokenHasher;
    private readonly IClock _clock;

    public LogoutSessionRevoker(
        OpsFlowDbContext db,
        IRefreshTokenHasher tokenHasher,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokenHasher);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _tokenHasher = tokenHasher;
        _clock = clock;
    }

    public async Task RevokeAsync(
        LogoutRevocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.RawRefreshToken))
        {
            return;
        }

        var hash = _tokenHasher.Hash(request.RawRefreshToken);

        // Discover UserId with a lock-free lookup so we can obtain the User
        // UPDLOCK next. An unknown hash means "no session to close" — return
        // successfully without opening a transaction.
        var initial = await _db.RefreshTokens
            .AsNoTracking()
            .Select(t => new { t.TokenHash, t.UserId })
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (initial is null)
        {
            return;
        }

        await using var transaction = await _db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        // Global lock order: User first, then RefreshToken.
        await SqlServerAuthenticationLocks
            .AcquireUserUpdateLockAsync(_db, initial.UserId, cancellationToken);
        await SqlServerAuthenticationLocks
            .AcquireRefreshTokenUpdateLockByHashAsync(_db, hash, cancellationToken);

        // Post-lock authoritative time. Any wait on the locks above is now
        // behind us; this value stamps every RevokedAt we may write.
        var now = _clock.UtcNow;

        var reloaded = await _db.RefreshTokens
            .AsNoTracking()
            .Select(t => new { t.TokenHash, t.TokenFamilyId })
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (reloaded is null)
        {
            // Row disappeared between our initial read and lock acquisition.
            // Nothing to close; commit the empty transaction and return.
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // Family-scoped revocation. Only ACTIVE rows are touched, so any
        // previously assigned RevokedAt/ReasonRevoked (Rotated,
        // ReuseDetected, SecurityChange, or Logout) is preserved.
        var activeFamilyTokens = await _db.RefreshTokens
            .Where(t => t.TokenFamilyId == reloaded.TokenFamilyId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in activeFamilyTokens)
        {
            token.RevokedAt = now;
            token.ReasonRevoked = RefreshTokenRevocationReason.Logout;
        }

        _ = await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
