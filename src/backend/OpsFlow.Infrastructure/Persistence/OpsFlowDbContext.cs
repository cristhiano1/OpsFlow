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

    /// <summary>Document text extractions.</summary>
    public DbSet<DocumentExtraction> DocumentExtractions => Set<DocumentExtraction>();

    /// <summary>Document chunk sets.</summary>
    public DbSet<DocumentChunkSet> DocumentChunkSets => Set<DocumentChunkSet>();

    /// <summary>Document chunks.</summary>
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    /// <summary>Document embedding sets.</summary>
    public DbSet<DocumentEmbeddingSet> DocumentEmbeddingSets => Set<DocumentEmbeddingSet>();

    /// <summary>Refresh tokens.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(OpsFlowDbContext).Assembly);
    }
}
