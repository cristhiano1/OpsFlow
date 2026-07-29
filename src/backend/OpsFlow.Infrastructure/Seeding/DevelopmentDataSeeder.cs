using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authorization;
using OpsFlow.Domain.Organizations;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Seeding;

/// <summary>
/// Creates deterministic, idempotent development/test data: the four roles, two
/// organizations and a small set of users. Safe to run repeatedly. The seed
/// password is never logged.
/// </summary>
public sealed class DevelopmentDataSeeder
{
    private static readonly Guid NorthwindId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContosoId = new("22222222-2222-2222-2222-222222222222");

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

    /// <summary>Runs the seeding process. Idempotent.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedRolesAsync();

        var northwind = await SeedOrganizationAsync(
            NorthwindId, "Northwind Field Services", "northwind-field-services", cancellationToken);
        var contoso = await SeedOrganizationAsync(
            ContosoId, "Contoso Operations", "contoso-operations", cancellationToken);

        if (string.IsNullOrWhiteSpace(_options.DefaultPassword))
        {
            _logger.LogWarning(
                "Seed:DefaultPassword is not configured; skipping user seeding. Roles and organizations were seeded.");
            return;
        }

        await SeedUserAsync("admin@opsflow.local", "Alice Admin", northwind.Id, OpsFlowRoles.OrganizationAdministrator);
        await SeedUserAsync("coordinator@opsflow.local", "Cora Coordinator", northwind.Id, OpsFlowRoles.Coordinator);
        await SeedUserAsync("technician@opsflow.local", "Toby Technician", northwind.Id, OpsFlowRoles.Technician);
        await SeedUserAsync("viewer@opsflow.local", "Vera Viewer", northwind.Id, OpsFlowRoles.Viewer);
        await SeedUserAsync("admin@contoso.local", "Otto Admin", contoso.Id, OpsFlowRoles.OrganizationAdministrator);
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

    private async Task SeedUserAsync(string email, string displayName, Guid organizationId, string role)
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

            var createResult = await _users.CreateAsync(user, _options.DefaultPassword!);
            EnsureSucceeded(createResult, $"create user '{email}'");
        }

        if (!await _users.IsInRoleAsync(user, role))
        {
            var roleResult = await _users.AddToRoleAsync(user, role);
            EnsureSucceeded(roleResult, $"assign role '{role}' to '{email}'");
        }
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
