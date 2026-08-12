using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Domain.Documents;
using OpsFlow.Domain.Organizations;
using OpsFlow.Domain.Projects;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Infrastructure.Persistence;

/// <summary>
/// The OpsFlow EF Core database context. Extends the ASP.NET Core Identity
/// context (Guid keys) and adds the organization and refresh-token tables.
/// </summary>
public class OpsFlowDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    /// <summary>Creates the context with the supplied options.</summary>
    public OpsFlowDbContext(DbContextOptions<OpsFlowDbContext> options)
        : base(options)
    {
    }

    /// <summary>Organizations (tenants).</summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    /// <summary>Projects.</summary>
    public DbSet<Project> Projects => Set<Project>();

    /// <summary>Document metadata records.</summary>
    public DbSet<Document> Documents => Set<Document>();

    /// <summary>Refresh tokens.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(OpsFlowDbContext).Assembly);
    }
}
