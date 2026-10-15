using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Infrastructure.Observability;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class RagMetricEntryConfiguration : IEntityTypeConfiguration<RagMetricEntry>
{
    public void Configure(EntityTypeBuilder<RagMetricEntry> builder)
    {
        builder.ToTable("RagMetricEntries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrganizationId).IsRequired();
        builder.Property(e => e.RecordedAt).IsRequired();
        builder.Property(e => e.Outcome).IsRequired().HasMaxLength(50);
        builder.Property(e => e.RetrievalMode).IsRequired().HasMaxLength(50);
        builder.Property(e => e.FailureCategory).HasMaxLength(50);

        builder.HasIndex(e => new { e.OrganizationId, e.RecordedAt });
    }
}
