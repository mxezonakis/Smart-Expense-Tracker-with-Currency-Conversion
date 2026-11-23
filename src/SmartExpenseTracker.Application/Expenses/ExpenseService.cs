using SmartExpenseTracker.Caching;
using SmartExpenseTracker.Persistence.Categories;
using SmartExpenseTracker.Persistence.Expenses;

namespace SmartExpenseTracker.Application.Expenses
{
    public sealed class ExpenseService
    {
        private readonly IExpenseStore _expenses;
        private readonly ICategoryStore _categories;
        private readonly IRateResolver _rates;
        private readonly DatasetCurrency _baseCurrency;

        public ExpenseService(IExpenseStore expenses, ICategoryStore categories, IRateResolver rates, DatasetCurrency baseCurrency)
        {
            _expenses = expenses;
            _categories = categories;
            _rates = rates;
            _baseCurrency = baseCurrency;
        }

        public async Task<ExpenseSnapshot> CreateAsync(ExpenseInput input, CancellationToken cancellationToken)
        {
            var expense = await PrepareAsync(input, null, cancellationToken);
            return await _expenses.CreateAsync(expense, cancellationToken);
        }

        public async Task<ExpenseUpdateResult?> UpdateAsync(int id, ExpenseInput input, byte[] expectedVersion, CancellationToken cancellationToken)
        {
            var previous = await _expenses.FindAsync(id, cancellationToken);
            if (previous is null)
            {
                return null;
            }

            if (!previous.Version.AsSpan().SequenceEqual(expectedVersion))
            {
                throw new ExpensePreconditionFailedException();
            }

            if (previous.Expense.Conversion.BaseCurrency != _baseCurrency.Code)
            {
                throw new ExpenseConflictException("The configured base currency differs from the dataset currency.");
            }

            var replacement = await PrepareAsync(input, previous.Expense, cancellationToken);
            if (replacement == previous.Expense)
            {
                return new ExpenseUpdateResult(false, previous.Version);
            }

            var version = await _expenses.UpdateAsync(previous, replacement, cancellationToken);
            return new ExpenseUpdateResult(true, version);
        }

        public Task<ExpenseSnapshot?> FindAsync(int id, CancellationToken cancellationToken)
        {
            return _expenses.FindAsync(id, cancellationToken);
        }

        public Task<IReadOnlyList<Expense>> ListAsync(ExpenseQuery query, CancellationToken cancellationToken)
        {
            return _expenses.ListAsync(query, cancellationToken);
        }

        public async Task DeleteAsync(int id, byte[] expectedVersion, CancellationToken cancellationToken)
        {
            if (!await _expenses.DeleteAsync(id, expectedVersion, cancellationToken))
            {
                throw new ExpensePreconditionFailedException();
            }
        }

        private async Task<Expense> PrepareAsync(ExpenseInput input, Expense? previous, CancellationToken cancellationToken)
        {
            var (currency, date, description) = ValidateFields(input);
            if (input.CategoryId <= 0 || await _categories.FindAsync(input.CategoryId, cancellationToken) is null)
            {
                throw new ExpenseValidationException("categoryId", "An existing category is required.");
            }

            if (previous is null && currency != _baseCurrency.Code &&
                await _expenses.HasConflictingDatasetCurrencyAsync(_baseCurrency.Code, cancellationToken))
            {
                throw new ExpenseConflictException("The configured base currency differs from the dataset currency.");
            }

            var conversion = previous is not null && previous.Amount == input.Amount && previous.Currency == currency
                ? previous.Conversion
                : await ConvertAsync(input.Amount, currency, cancellationToken);
            return new Expense(previous?.Id ?? 0, input.Amount, currency, input.CategoryId, date, description, conversion);
        }

        private static (string Currency, DateOnly Date, string? Description) ValidateFields(ExpenseInput input)
        {
            if (input.Amount is < 0.01m or > 99999999.99m || decimal.Round(input.Amount, 2) != input.Amount)
            {
                throw new ExpenseValidationException("amount", "Amount must be between 0.01 and 99999999.99 in whole cents.");
            }

            var code = input.Currency?.Trim();
            if (code is not { Length: 3 } || !code.All(char.IsAsciiLetter))
            {
                throw new ExpenseValidationException("currency", "Currency must contain three ASCII letters.");
            }

            if (input.Date is not { } date)
            {
                throw new ExpenseValidationException("date", "An expense date is required.");
            }

            var description = input.Description?.Trim();
            if (description?.Length > 500)
            {
                throw new ExpenseValidationException("description", "Description may contain at most 500 characters after trimming.");
            }

            return (code.ToUpperInvariant(), date, string.IsNullOrEmpty(description) ? null : description);
        }

        private async Task<ConversionSnapshot> ConvertAsync(decimal amount, string currency, CancellationToken cancellationToken)
        {
            if (currency == _baseCurrency.Code)
            {
                return new ConversionSnapshot(currency, 1m, amount, null, null, false);
            }

            var resolved = await _rates.ResolveAsync(cancellationToken);
            var table = resolved.Snapshot.Table;
            if (table.BaseCurrency != _baseCurrency.Code)
            {
                throw new RateUnavailableException();
            }

            var quote = resolved.ForCurrency(currency) ??
                throw new ExpenseValidationException("currency", "Currency is unsupported by the exchange-rate table.");
            var convertedAmount = decimal.Round(amount * quote.AppliedRate, 2, MidpointRounding.ToEven);
            return new ConversionSnapshot(table.BaseCurrency, quote.AppliedRate, convertedAmount,
                table.Provider, quote.PublishedAt.ToUniversalTime(), quote.IsFallback);
        }
    }
}
