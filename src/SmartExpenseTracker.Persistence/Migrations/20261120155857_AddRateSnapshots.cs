using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartExpenseTracker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRateSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RateSnapshots",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetrievedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextUpdateAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Rates = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateSnapshots", x => new { x.Provider, x.BaseCurrency });
                    table.CheckConstraint("CK_RateSnapshots_Rates", "ISJSON([Rates], OBJECT) = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RateSnapshots");
        }
    }
}
