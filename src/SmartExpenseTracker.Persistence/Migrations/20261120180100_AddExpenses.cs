using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartExpenseTracker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpenseDataset",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    BaseCurrency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseDataset", x => x.Id);
                    table.UniqueConstraint("AK_ExpenseDataset_BaseCurrency", x => x.BaseCurrency);
                    table.CheckConstraint("CK_ExpenseDataset_Singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "Expenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BaseCurrency = table.Column<string>(type: "char(3)", nullable: false),
                    AppliedRate = table.Column<decimal>(type: "decimal(18,12)", nullable: false),
                    ConvertedAmount = table.Column<decimal>(type: "decimal(16,2)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    IsFallback = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expenses", x => x.Id);
                    table.CheckConstraint("CK_Expenses_Amounts", "[Amount] > 0 AND [AppliedRate] > 0 AND [ConvertedAmount] >= 0");
                    table.ForeignKey(
                        name: "FK_Expenses_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Expenses_ExpenseDataset_BaseCurrency",
                        column: x => x.BaseCurrency,
                        principalTable: "ExpenseDataset",
                        principalColumn: "BaseCurrency",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_BaseCurrency",
                table: "Expenses",
                column: "BaseCurrency");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_CategoryId",
                table: "Expenses",
                column: "CategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Expenses");

            migrationBuilder.DropTable(
                name: "ExpenseDataset");
        }
    }
}
