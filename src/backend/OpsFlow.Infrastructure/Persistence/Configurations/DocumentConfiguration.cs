using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Documents;
using OpsFlow.Domain.Projects;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("Documents");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.OrganizationId).IsRequired();
        builder.Property(d => d.ProjectId).IsRequired();
        builder.Property(d => d.OriginalFileName)
            .IsRequired()
            .HasMaxLength(Document.OriginalFileNameMaxLength);
        builder.Property(d => d.ContentType)
            .IsRequired()
            .HasMaxLength(Document.ContentTypeMaxLength);
        builder.Property(d => d.SizeBytes).IsRequired();
        builder.Property(d => d.CreatedAt).IsRequired();

        // Composite FK: Document(ProjectId, OrganizationId) -> Project(Id, OrganizationId)
        // Prevents a Document from referencing a Project that belongs to a different
        // Organization at the database level (defence-in-depth tenant isolation).
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(d => new { d.ProjectId, d.OrganizationId })
            .HasPrincipalKey(p => new { p.Id, p.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);

        // Supports the tenant/project ordered list query.
        builder.HasIndex(d => new { d.OrganizationId, d.ProjectId, d.CreatedAt, d.Id });
    }
}
