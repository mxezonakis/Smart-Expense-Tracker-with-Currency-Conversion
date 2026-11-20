using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartExpenseTracker.Persistence
{
    internal sealed class ExpenseRow
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public required string Currency { get; set; }
        public int CategoryId { get; set; }
        public DateOnly Date { get; set; }
        public string? Description { get; set; }
        public required string BaseCurrency { get; set; }
        public decimal AppliedRate { get; set; }
        public decimal ConvertedAmount { get; set; }
        public string? Provider { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public bool IsFallback { get; set; }
        public byte[] Version { get; set; } = [];
    }

    internal sealed class DatasetRow
    {
        public int Id { get; set; }
        public required string BaseCurrency { get; set; }
    }

    internal sealed class DatasetConfiguration : IEntityTypeConfiguration<DatasetRow>
    {
        public void Configure(EntityTypeBuilder<DatasetRow> builder)
        {
            builder.ToTable("ExpenseDataset", t => t.HasCheckConstraint("CK_ExpenseDataset_Singleton", "[Id] = 1"));
            builder.HasKey(r => r.Id);
            builder.Property(r => r.Id).ValueGeneratedNever();
            builder.Property(r => r.BaseCurrency).HasColumnType("char(3)");
            builder.HasAlternateKey(r => r.BaseCurrency);
        }
    }

    internal sealed class ExpenseConfiguration : IEntityTypeConfiguration<ExpenseRow>
    {
        internal const string CategoryForeignKey = "FK_Expenses_Categories_CategoryId";

        public void Configure(EntityTypeBuilder<ExpenseRow> builder)
        {
            builder.ToTable("Expenses", t => t.HasCheckConstraint("CK_Expenses_Amounts",
                "[Amount] > 0 AND [AppliedRate] > 0 AND [ConvertedAmount] >= 0"));
            builder.HasKey(r => r.Id);
            builder.HasIndex(r => r.ConvertedAmount);
            builder.HasIndex(r => r.Currency);
            builder.HasIndex(r => r.Date);
            builder.Property(r => r.Amount).HasColumnType("decimal(10,2)");
            builder.Property(r => r.Currency).HasColumnType("char(3)");
            builder.Property(r => r.BaseCurrency).HasColumnType("char(3)");
            builder.Property(r => r.Date).HasColumnType("date");
            builder.Property(r => r.Description).HasMaxLength(500);
            builder.Property(r => r.AppliedRate).HasColumnType("decimal(18,12)");
            builder.Property(r => r.ConvertedAmount).HasColumnType("decimal(16,2)");
            builder.Property(r => r.Provider).HasMaxLength(64);
            builder.Property(r => r.PublishedAt).HasColumnType("datetimeoffset(7)");
            builder.Property(r => r.Version).IsRowVersion();
            builder.HasOne<CategoryRow>().WithMany().HasForeignKey(r => r.CategoryId)
                .HasConstraintName(CategoryForeignKey).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<DatasetRow>().WithMany().HasForeignKey(r => r.BaseCurrency)
                .HasPrincipalKey(r => r.BaseCurrency).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
