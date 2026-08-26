using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable("DocumentChunks");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.DocumentId).IsRequired();
        builder.Property(c => c.ChunkIndex).IsRequired();
        builder.Property(c => c.StartOffset).IsRequired();
        builder.Property(c => c.EndOffset).IsRequired();
        builder.Property(c => c.Text)
            .IsRequired()
            .HasMaxLength(DocumentChunk.MaxTextLength);

        builder.HasIndex(c => new { c.DocumentId, c.ChunkIndex }).IsUnique();

        builder.HasOne<DocumentChunkSet>()
            .WithMany()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
