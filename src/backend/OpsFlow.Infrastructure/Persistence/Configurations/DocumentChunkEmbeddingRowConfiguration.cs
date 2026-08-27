using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Application.Documents;
using OpsFlow.Domain.Documents;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class DocumentChunkEmbeddingRowConfiguration : IEntityTypeConfiguration<DocumentChunkEmbeddingRow>
{
    public void Configure(EntityTypeBuilder<DocumentChunkEmbeddingRow> builder)
    {
        builder.ToTable("DocumentChunkEmbeddings");

        builder.HasKey(r => new { r.EmbeddingSetId, r.DocumentChunkId });

        builder.Property(r => r.Embedding)
            .HasColumnType($"vector({EmbeddingProfiles.SemanticV1Dimensions})")
            .IsRequired();

        builder.HasOne<DocumentEmbeddingSet>()
            .WithMany()
            .HasForeignKey(r => r.EmbeddingSetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<DocumentChunk>()
            .WithMany()
            .HasForeignKey(r => r.DocumentChunkId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.DocumentChunkId);
    }
}
