using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class DocumentChunkSetConfiguration : IEntityTypeConfiguration<DocumentChunkSet>
{
    public void Configure(EntityTypeBuilder<DocumentChunkSet> builder)
    {
        builder.ToTable("DocumentChunkSets");

        builder.HasKey(s => s.DocumentId);

        builder.Property(s => s.ChunkingVersion).IsRequired();
        builder.Property(s => s.ChunkCount).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasOne<DocumentExtraction>()
            .WithMany()
            .HasForeignKey(s => s.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
