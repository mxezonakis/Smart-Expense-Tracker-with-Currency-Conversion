using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SmartExpenseTracker.Persistence.Migrations
{
    [DbContext(typeof(SmartExpenseTrackerDbContext))]
    internal sealed class SmartExpenseTrackerDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
            modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);
            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);
            modelBuilder.Entity("SmartExpenseTracker.Persistence.CategoryRow", entity =>
            {
                entity.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("int").UseIdentityColumn();
                entity.Property<string>("Name").IsRequired().HasMaxLength(100)
                    .HasColumnType("nvarchar(100)").UseCollation("Latin1_General_100_CI_AS_SC");
                entity.HasKey("Id");
                entity.HasIndex("Name").IsUnique();
                entity.ToTable("Categories", table => table.HasCheckConstraint("CK_Categories_CanonicalName", "DATALENGTH([Name]) > 0 AND UNICODE(LEFT([Name], 1)) NOT IN (9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288) AND UNICODE(RIGHT([Name], 1)) NOT IN (9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)"));
            });
        }
    }
}
