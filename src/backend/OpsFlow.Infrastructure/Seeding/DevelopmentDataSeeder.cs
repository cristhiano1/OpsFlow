using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authorization;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Observability;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Seeding;

/// <summary>
/// Creates deterministic, idempotent development/test data: the four roles, two
/// organizations and a small set of users. Safe to run repeatedly. The configured
/// passwords are authoritative for the seeded accounts: an existing account whose
/// password differs is updated (rotating its security stamp). Passwords are
/// never logged.
/// </summary>
public sealed class DevelopmentDataSeeder
{
    private static readonly Guid NorthwindId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContosoId = new("22222222-2222-2222-2222-222222222222");
    private const string ViewerEmail = "viewer@opsflow.local";

    private readonly OpsFlowDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<IdentityRole<Guid>> _roles;
    private readonly SeedOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentDataSeeder> _logger;

    /// <summary>Creates the seeder.</summary>
    public DevelopmentDataSeeder(
        OpsFlowDbContext db,
        UserManager<ApplicationUser> users,
        RoleManager<IdentityRole<Guid>> roles,
        IOptions<SeedOptions> options,
        IClock clock,
        ILogger<DevelopmentDataSeeder> logger)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Runs the seeding process. Idempotent. Credentials are validated for
    /// <paramref name="mode"/> before anything is written.
    /// </summary>
    public async Task SeedAsync(SeedMode mode = SeedMode.Development, CancellationToken cancellationToken = default)
    {
        var credentials = SeedCredentials.Resolve(_options, mode);

        if (mode == SeedMode.PublicDemo && credentials is not null)
        {
            await EnsureViewerDoesNotHoldPrivilegedPasswordAsync(credentials.PrivilegedPassword);
        }

        await SeedRolesAsync();

        var northwind = await SeedOrganizationAsync(
            NorthwindId, "Northwind Field Services", "northwind-field-services", cancellationToken);
        var contoso = await SeedOrganizationAsync(
            ContosoId, "Contoso Operations", "contoso-operations", cancellationToken);

        if (credentials is null)
        {
            _logger.LogWarning(
                "Seed:DefaultPassword is not configured; skipping user seeding. Roles and organizations were seeded.");
            return;
        }

        var privilegedPassword = credentials.PrivilegedPassword;

        await SeedUserAsync("admin@opsflow.local", "Alice Admin", northwind.Id, OpsFlowRoles.OrganizationAdministrator, privilegedPassword);
        await SeedUserAsync("coordinator@opsflow.local", "Cora Coordinator", northwind.Id, OpsFlowRoles.Coordinator, privilegedPassword);
        await SeedUserAsync("technician@opsflow.local", "Toby Technician", northwind.Id, OpsFlowRoles.Technician, privilegedPassword);
        await SeedUserAsync(ViewerEmail, "Vera Viewer", northwind.Id, OpsFlowRoles.Viewer, credentials.ViewerPassword);
        await SeedUserAsync("admin@contoso.local", "Otto Admin", contoso.Id, OpsFlowRoles.OrganizationAdministrator, privilegedPassword);

        await SeedRagMetricsAsync(northwind.Id, cancellationToken);
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in OpsFlowRoles.All)
        {
            if (!await _roles.RoleExistsAsync(role))
            {
                var result = await _roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() });
                EnsureSucceeded(result, $"create role '{role}'");
            }
        }
    }

    private async Task<Organization> SeedOrganizationAsync(
        Guid id, string name, string slug, CancellationToken cancellationToken)
    {
        var existing = await _db.Organizations.FirstOrDefaultAsync(o => o.Slug == slug, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var organization = new Organization
        {
            Id = id,
            Name = name,
            Slug = slug,
            IsActive = true,
            CreatedAt = _clock.UtcNow,
        };

        _db.Organizations.Add(organization);
        await _db.SaveChangesAsync(cancellationToken);
        return organization;
    }

    private async Task SeedUserAsync(string email, string displayName, Guid organizationId, string role, string password)
    {
        var user = await _users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
                OrganizationId = organizationId,
                IsActive = true,
                CreatedAt = _clock.UtcNow,
            };

            var createResult = await _users.CreateAsync(user, password);
            EnsureSucceeded(createResult, $"create user '{email}'");
        }
        else
        {
            await EnsurePasswordAsync(user, email, password);
        }

        if (!await _users.IsInRoleAsync(user, role))
        {
            var roleResult = await _users.AddToRoleAsync(user, role);
            EnsureSucceeded(roleResult, $"assign role '{role}' to '{email}'");
        }
    }

    // Configuration is authoritative for seeded accounts, so an existing database
    // picks up rotated or newly separated credentials.
    private async Task EnsurePasswordAsync(ApplicationUser user, string email, string password)
    {
        if (await _users.CheckPasswordAsync(user, password))
        {
            return;
        }

        foreach (var validator in _users.PasswordValidators)
        {
            EnsureSucceeded(
                await validator.ValidateAsync(_users, user, password),
                $"validate the configured password for '{email}'");
        }

        // A single update writes the new hash and a rotated security stamp, which
        // revokes access and refresh tokens issued under the previous password.
        user.PasswordHash = _users.PasswordHasher.HashPassword(user, password);
        EnsureSucceeded(await _users.UpdateSecurityStampAsync(user), $"update the password for '{email}'");
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Updated the password of seeded account {Email} to match configuration", email);
        }
    }

    // Before separate viewer credentials existed, every seeded account (the public
    // Viewer included) used Seed:DefaultPassword. Refuse to keep that password for
    // privileged accounts when it may already have been published.
    private async Task EnsureViewerDoesNotHoldPrivilegedPasswordAsync(string privilegedPassword)
    {
        var viewer = await _users.FindByEmailAsync(ViewerEmail);
        if (viewer is not null && await _users.CheckPasswordAsync(viewer, privilegedPassword))
        {
            throw new InvalidOperationException(
                $"Seed configuration error: the existing {ViewerEmail} account still accepts Seed:DefaultPassword, " +
                "so that password may already have been shared publicly. Choose a new Seed:DefaultPassword " +
                "(SEED_DEFAULT_PASSWORD) and run demo seeding again; seeded accounts are then updated to the " +
                "configured passwords.");
        }
    }

    private async Task SeedRagMetricsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (await _db.RagMetricEntries.AnyAsync(m => m.OrganizationId == organizationId, cancellationToken))
        {
            return;
        }

        var rng = new Random(42);
        var now = _clock.UtcNow;

        for (var dayOffset = 29; dayOffset >= 0; dayOffset--)
        {
            var baseDate = now.AddDays(-dayOffset);
            var queriesThisDay = rng.Next(3, 12);

            for (var q = 0; q < queriesThisDay; q++)
            {
                var roll = rng.NextDouble();
                string outcome;
                string? failureCategory = null;

                if (roll < 0.72)
                {
                    outcome = "Answered";
                }
                else if (roll < 0.88)
                {
                    outcome = "InsufficientEvidence";
                }
                else
                {
                    outcome = "Failed";
                    var categories = new[] { "EmbeddingProviderFailure", "AnswerProviderFailure", "Internal" };
                    failureCategory = categories[rng.Next(categories.Length)];
                }

                var totalDuration = 0.8 + (rng.NextDouble() * 3.2);
                var retrievalDuration = 0.2 + (rng.NextDouble() * 1.0);
                double? generationDuration = outcome == "Answered"
                    ? 0.5 + (rng.NextDouble() * 2.0)
                    : null;
                int? evidenceCount = outcome != "Failed"
                    ? rng.Next(2, 8)
                    : null;

                _db.RagMetricEntries.Add(new RagMetricEntry
                {
                    OrganizationId = organizationId,
                    RecordedAt = baseDate.AddHours(rng.Next(8, 18)).AddMinutes(rng.Next(0, 60)),
                    Outcome = outcome,
                    RetrievalMode = outcome == "Failed" ? "NotApplicable" : "Hybrid",
                    FailureCategory = failureCategory,
                    SelectedEvidenceCount = evidenceCount,
                    TotalDurationSeconds = Math.Round(totalDuration, 3),
                    RetrievalDurationSeconds = Math.Round(retrievalDuration, 3),
                    GenerationDurationSeconds = generationDuration.HasValue
                        ? Math.Round(generationDuration.Value, 3)
                        : null,
                });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
            throw new InvalidOperationException($"Seeding failed to {action}: {errors}");
        }
    }
}
