using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartExpenseTracker.Persistence
{
    internal sealed class RateSnapshotRow
    {
        public required string Provider { get; set; }
        public required string BaseCurrency { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public DateTimeOffset RetrievedAt { get; set; }
        public DateTimeOffset? NextUpdateAt { get; set; }
        public required string Rates { get; set; }
    }

    internal sealed class RateSnapshotConfiguration : IEntityTypeConfiguration<RateSnapshotRow>
    {
        public void Configure(EntityTypeBuilder<RateSnapshotRow> builder)
        {
            builder.ToTable("RateSnapshots", table => table.HasCheckConstraint("CK_RateSnapshots_Rates", "ISJSON([Rates], OBJECT) = 1"));
            builder.HasKey(row => new { row.Provider, row.BaseCurrency });
            builder.Property(row => row.Provider).HasMaxLength(64);
            builder.Property(row => row.BaseCurrency).HasMaxLength(3);
        }
    }
}
