using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartExpenseTracker.Persistence.Categories;

namespace SmartExpenseTracker.Persistence
{
    internal sealed class CategoryRow
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }

    internal sealed class CategoryConfiguration : IEntityTypeConfiguration<CategoryRow>
    {
        public void Configure(EntityTypeBuilder<CategoryRow> builder)
        {
            // SQL Server's default TRIM only removes spaces. Match the whitespace handled by string.Trim().
            const string whitespace = "9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288";
            builder.ToTable("Categories", table => table.HasCheckConstraint("CK_Categories_CanonicalName",
                $"DATALENGTH([Name]) > 0 AND UNICODE(LEFT([Name], 1)) NOT IN ({whitespace}) AND UNICODE(RIGHT([Name], 1)) NOT IN ({whitespace})"));
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Name).IsRequired().HasMaxLength(Category.MaximumNameLength)
                .UseCollation("Latin1_General_100_CI_AS_SC");
            builder.HasIndex(row => row.Name).IsUnique();
        }
    }
}
