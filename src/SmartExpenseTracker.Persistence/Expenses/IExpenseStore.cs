namespace SmartExpenseTracker.Persistence.Expenses
{
    public sealed record ConversionSnapshot(string BaseCurrency, decimal AppliedRate, decimal ConvertedAmount,
        string? Provider, DateTimeOffset? PublishedAt, bool IsFallback);

    public sealed record Expense(int Id, decimal Amount, string Currency, int CategoryId, DateOnly Date,
        string? Description, ConversionSnapshot Conversion);

    // Internal workflow state, never part of the HTTP request or response contract.
    public sealed record ExpenseSnapshot(Expense Expense, byte[] Version);

    public interface IExpenseStore
    {
        Task<bool> HasConflictingDatasetCurrencyAsync(string baseCurrency, CancellationToken cancellationToken);
        Task<ExpenseSnapshot> CreateAsync(Expense expense, CancellationToken cancellationToken);
        Task<ExpenseSnapshot?> FindAsync(int id, CancellationToken cancellationToken);
        Task<IReadOnlyList<Expense>> ListAsync(ExpenseQuery query, CancellationToken cancellationToken);
        Task<byte[]> UpdateAsync(ExpenseSnapshot previous, Expense replacement, CancellationToken cancellationToken);
        Task<bool> DeleteAsync(int id, byte[] expectedVersion, CancellationToken cancellationToken);
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

    public sealed class ExpensePreconditionFailedException : Exception
    {
        public ExpensePreconditionFailedException(Exception? cause = null)
            : base("The expense no longer matches the supplied precondition.", cause)
        {
        }
    }
}
