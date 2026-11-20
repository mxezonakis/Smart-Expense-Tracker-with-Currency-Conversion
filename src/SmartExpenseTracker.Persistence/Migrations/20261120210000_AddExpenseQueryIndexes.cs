using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartExpenseTracker.Persistence.Migrations
{
    public partial class AddExpenseQueryIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex("IX_Expenses_ConvertedAmount", "Expenses", "ConvertedAmount");
            migrationBuilder.CreateIndex("IX_Expenses_Currency", "Expenses", "Currency");
            migrationBuilder.CreateIndex("IX_Expenses_Date", "Expenses", "Date");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex("IX_Expenses_ConvertedAmount", "Expenses");
            migrationBuilder.DropIndex("IX_Expenses_Currency", "Expenses");
            migrationBuilder.DropIndex("IX_Expenses_Date", "Expenses");
        }
    }
}
