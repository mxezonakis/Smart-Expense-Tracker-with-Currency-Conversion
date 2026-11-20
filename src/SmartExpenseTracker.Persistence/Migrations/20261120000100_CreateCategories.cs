using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartExpenseTracker.Persistence.Migrations
{
    [DbContext(typeof(SmartExpenseTrackerDbContext))]
    [Migration("20261120000100_CreateCategories")]
    public sealed class CreateCategories : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_CI_AS_SC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", row => row.Id);
                    table.CheckConstraint("CK_Categories_CanonicalName", "DATALENGTH([Name]) > 0 AND UNICODE(LEFT([Name], 1)) NOT IN (9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288) AND UNICODE(RIGHT([Name], 1)) NOT IN (9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)");
                });

            migrationBuilder.CreateIndex(name: "IX_Categories_Name", table: "Categories", column: "Name", unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("Categories");
        }

        protected override void BuildTargetModel(ModelBuilder modelBuilder)
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
