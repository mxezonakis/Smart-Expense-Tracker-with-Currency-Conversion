namespace SmartExpenseTracker.Application.Expenses
{
    public sealed record ExpenseInput(decimal Amount, string? Currency, int CategoryId, DateOnly? Date, string? Description);

    public sealed record DatasetCurrency(string Code);

    public sealed record ExpenseUpdateResult(bool Updated, byte[] Version);
}
