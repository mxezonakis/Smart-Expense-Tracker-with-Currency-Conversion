namespace SmartExpenseTracker.Application.Expenses
{
    public enum ExpenseSort
    {
        Date,
        Amount
    }

    public sealed record ExpenseQuery(DateOnly? From, DateOnly? To, int? CategoryId, string? Currency,
        ExpenseSort? SortBy, bool Descending);
}
