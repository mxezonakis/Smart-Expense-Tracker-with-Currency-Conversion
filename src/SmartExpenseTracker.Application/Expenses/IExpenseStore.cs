namespace SmartExpenseTracker.Application.Expenses
{
    public sealed record ExpenseInput(decimal Amount, string? Currency, int CategoryId, DateOnly? Date, string? Description);

    public sealed record ConversionSnapshot(string BaseCurrency, decimal AppliedRate, decimal ConvertedAmount,
        string? Provider, DateTimeOffset? PublishedAt, bool IsFallback);

    public sealed record Expense(int Id, decimal Amount, string Currency, int CategoryId, DateOnly Date,
        string? Description, ConversionSnapshot Conversion);

    public sealed record DatasetCurrency(string Code);

    public interface IExpenseStore
    {
        Task<bool> HasConflictingDatasetCurrencyAsync(string baseCurrency, CancellationToken cancellationToken);
        Task<Expense> CreateAsync(Expense expense, CancellationToken cancellationToken);
        Task<Expense?> FindAsync(int id, CancellationToken cancellationToken);
        Task<IReadOnlyList<Expense>> ListAsync(ExpenseQuery query, CancellationToken cancellationToken);
        Task<bool> UpdateAsync(Expense previous, Expense replacement, CancellationToken cancellationToken);
        Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
    }

    public sealed class ExpenseValidationException : Exception
    {
        public ExpenseValidationException(string field, string message)
            : base(message)
        {
            Field = field;
        }

        public string Field { get; }
    }

    public sealed class ExpenseConflictException : Exception
    {
        public ExpenseConflictException(string message, Exception? cause = null)
            : base(message, cause)
        {
        }
    }
}
