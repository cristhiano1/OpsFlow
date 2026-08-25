using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Documents;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class DocumentExtractionConfiguration : IEntityTypeConfiguration<DocumentExtraction>
{
    public void Configure(EntityTypeBuilder<DocumentExtraction> builder)
    {
        builder.ToTable("DocumentExtractions");

        builder.HasKey(e => e.DocumentId);

        builder.Property(e => e.ExtractedText).IsRequired();
        builder.Property(e => e.ExtractedAt).IsRequired();

        // A DocumentExtraction is a derived artifact of its Document. Cascading
        // the delete is correct: when a Document is removed, its extraction
        // result has no independent value and should be removed automatically.
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(e => e.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
