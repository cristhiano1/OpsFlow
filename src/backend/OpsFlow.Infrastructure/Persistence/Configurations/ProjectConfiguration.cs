using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Projects;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.OrganizationId).IsRequired();
        builder.Property(p => p.Name).IsRequired().HasMaxLength(Project.NameMaxLength);
        builder.Property(p => p.Description).HasMaxLength(Project.DescriptionMaxLength);
        builder.Property(p => p.CreatedAt).IsRequired();

        builder.HasOne<OpsFlow.Domain.Organizations.Organization>()
            .WithMany()
            .HasForeignKey(p => p.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.OrganizationId, p.CreatedAt });

        // Alternate key so Documents can reference Project(Id, OrganizationId)
        // via a composite FK, preventing cross-tenant document-to-project links
        // at the database level.
        builder.HasAlternateKey(p => new { p.Id, p.OrganizationId });
    }
}
