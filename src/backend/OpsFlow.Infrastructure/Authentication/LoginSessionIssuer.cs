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

        var resetResult = await _userManager.ResetAccessFailedCountAsync(user);
        if (!resetResult.Succeeded)
        {
            var concurrencyCode = _userManager.ErrorDescriber.ConcurrencyFailure().Code;
            if (resetResult.Errors.Any(e =>
                    string.Equals(e.Code, concurrencyCode, StringComparison.Ordinal)))
            {
                return SessionIssueResult.Rejected();
            }

            throw new InvalidOperationException(
                "The login session issuer could not persist the successful-login state.");
        }

        var now = _clock.UtcNow;
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
