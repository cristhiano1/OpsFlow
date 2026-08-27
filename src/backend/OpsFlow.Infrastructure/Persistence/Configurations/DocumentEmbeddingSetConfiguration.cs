using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class DocumentEmbeddingSetConfiguration : IEntityTypeConfiguration<DocumentEmbeddingSet>
{
    public void Configure(EntityTypeBuilder<DocumentEmbeddingSet> builder)
    {
        builder.ToTable("DocumentEmbeddingSets");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.DocumentId).IsRequired();
        builder.Property(s => s.ChunkingVersion).IsRequired();
        builder.Property(s => s.ProfileId)
            .IsRequired()
            .HasMaxLength(DocumentEmbeddingSet.MaxProfileIdLength);
        builder.Property(s => s.ModelId)
            .IsRequired()
            .HasMaxLength(DocumentEmbeddingSet.MaxModelIdLength);
        builder.Property(s => s.Dimensions).IsRequired();
        builder.Property(s => s.EmbeddingCount).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasIndex(s => new { s.DocumentId, s.ProfileId }).IsUnique();

        builder.HasOne<DocumentChunkSet>()
            .WithMany()
            .HasForeignKey(s => s.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
