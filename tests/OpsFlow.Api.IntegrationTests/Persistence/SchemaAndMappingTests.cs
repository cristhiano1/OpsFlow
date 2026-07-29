using Microsoft.EntityFrameworkCore;
using OpsFlow.Api.IntegrationTests.Infrastructure;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Api.IntegrationTests.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class SchemaAndMappingTests
{
    private readonly SqlServerFixture _fixture;

    public SchemaAndMappingTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_applies_and_all_tables_are_queryable()
    {
        await using var db = _fixture.CreateContext();

        Assert.True(await db.Database.CanConnectAsync());
        _ = await db.Organizations.AsNoTracking().ToListAsync();
        _ = await db.RefreshTokens.AsNoTracking().ToListAsync();
        _ = await db.Users.AsNoTracking().ToListAsync();
        _ = await db.Roles.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Organization_slug_has_a_unique_index()
    {
        var slug = "dup-" + Guid.NewGuid().ToString("N");

        await using (var db = _fixture.CreateContext())
        {
            db.Organizations.Add(NewOrganization(slug));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            db.Organizations.Add(NewOrganization(slug));
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task RefreshToken_hash_has_a_unique_index()
    {
        var hash = "hash-" + Guid.NewGuid().ToString("N");

        await using var db = _fixture.CreateContext();
        var (org, user) = NewOrganizationWithUser();
        db.Organizations.Add(org);
        db.Users.Add(user);
        db.RefreshTokens.Add(NewToken(user.Id, hash));
        await db.SaveChangesAsync();

        await using var db2 = _fixture.CreateContext();
        db2.RefreshTokens.Add(NewToken(user.Id, hash));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_user_cascades_to_refresh_tokens()
    {
        Guid userId;

        await using (var db = _fixture.CreateContext())
        {
            var (org, user) = NewOrganizationWithUser();
            userId = user.Id;
            db.Organizations.Add(org);
            db.Users.Add(user);
            db.RefreshTokens.Add(NewToken(user.Id, "hash-" + Guid.NewGuid().ToString("N")));
            await db.SaveChangesAsync();

            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Empty(await db.RefreshTokens.Where(t => t.UserId == userId).ToListAsync());
        }
    }

    [Fact]
    public async Task Deleting_an_organization_that_has_users_is_restricted()
    {
        var (org, user) = NewOrganizationWithUser();

        await using (var seed = _fixture.CreateContext())
        {
            seed.Organizations.Add(org);
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        }

        await using (var deleteContext = _fixture.CreateContext())
        {
            // Load only the organization. The dependent user is not tracked, so
            // EF issues a raw DELETE that SQL Server rejects through the FK
            // NO ACTION constraint (a database-level check, not client-side).
            var loaded = await deleteContext.Organizations.SingleAsync(o => o.Id == org.Id);
            deleteContext.Organizations.Remove(loaded);
            await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());
        }

        await using var verify = _fixture.CreateContext();
        Assert.True(await verify.Organizations.AnyAsync(o => o.Id == org.Id));
        Assert.True(await verify.Users.AnyAsync(u => u.Id == user.Id));
    }

    [Fact]
    public async Task RefreshToken_rowversion_detects_a_concurrent_update()
    {
        Guid tokenId;

        await using (var db = _fixture.CreateContext())
        {
            var (org, user) = NewOrganizationWithUser();
            var token = NewToken(user.Id, "hash-" + Guid.NewGuid().ToString("N"));
            tokenId = token.Id;
            db.Organizations.Add(org);
            db.Users.Add(user);
            db.RefreshTokens.Add(token);
            await db.SaveChangesAsync();
        }

        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();

        var tokenA = await contextA.RefreshTokens.SingleAsync(t => t.Id == tokenId);
        var tokenB = await contextB.RefreshTokens.SingleAsync(t => t.Id == tokenId);

        tokenA.RevokedAt = DateTimeOffset.UtcNow;
        tokenA.ReasonRevoked = RefreshTokenRevocationReason.Rotated;
        await contextA.SaveChangesAsync();

        tokenB.RevokedAt = DateTimeOffset.UtcNow;
        tokenB.ReasonRevoked = RefreshTokenRevocationReason.Logout;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());
    }

    [Fact]
    public async Task Users_with_the_same_normalized_email_cannot_coexist()
    {
        var email = "shared-" + Guid.NewGuid().ToString("N") + "@test.local";

        await using (var db = _fixture.CreateContext())
        {
            var org = NewOrganization("org-" + Guid.NewGuid().ToString("N"));
            db.Organizations.Add(org);
            db.Users.Add(NewUser(org.Id, "user1-" + Guid.NewGuid().ToString("N"), email));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var org = NewOrganization("org-" + Guid.NewGuid().ToString("N"));
            db.Organizations.Add(org);
            // Different username, same normalized email: must be rejected by the unique EmailIndex.
            db.Users.Add(NewUser(org.Id, "user2-" + Guid.NewGuid().ToString("N"), email));
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    private static Organization NewOrganization(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Organization",
        Slug = slug,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static (Organization Organization, ApplicationUser User) NewOrganizationWithUser()
    {
        var org = NewOrganization("org-" + Guid.NewGuid().ToString("N"));
        var email = "u" + Guid.NewGuid().ToString("N") + "@test.local";
        return (org, NewUser(org.Id, email, email));
    }

    private static ApplicationUser NewUser(Guid organizationId, string userName, string email) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        DisplayName = "Test User",
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        SecurityStamp = Guid.NewGuid().ToString("N"),
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static RefreshToken NewToken(Guid userId, string hash) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = hash,
        TokenFamilyId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
    };
}
