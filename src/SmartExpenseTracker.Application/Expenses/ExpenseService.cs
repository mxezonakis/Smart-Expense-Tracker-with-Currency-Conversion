using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Application.ExchangeRates;

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

        public async Task<Expense> CreateAsync(ExpenseInput input, CancellationToken cancellationToken)
        {
            var expense = await PrepareAsync(input, null, cancellationToken);
            return await _expenses.CreateAsync(expense, cancellationToken);
        }

        public async Task<bool> UpdateAsync(int id, ExpenseInput input, CancellationToken cancellationToken)
        {
            var previous = await _expenses.FindAsync(id, cancellationToken);
            if (previous is null)
            {
                return false;
            }

            if (previous.Conversion.BaseCurrency != _baseCurrency.Code)
            {
                throw new ExpenseConflictException("The configured base currency differs from the dataset currency.");
            }

            var replacement = await PrepareAsync(input, previous, cancellationToken);
            return await _expenses.UpdateAsync(previous, replacement, cancellationToken);
        }

        public Task<Expense?> FindAsync(int id, CancellationToken cancellationToken)
        {
            return _expenses.FindAsync(id, cancellationToken);
        }

        public Task<IReadOnlyList<Expense>> ListAsync(ExpenseQuery query, CancellationToken cancellationToken)
        {
            return _expenses.ListAsync(query, cancellationToken);
        }

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
        {
            return _expenses.DeleteAsync(id, cancellationToken);
        }

        private async Task<Expense> PrepareAsync(ExpenseInput input, Expense? previous, CancellationToken cancellationToken)
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

            if (input.CategoryId <= 0 || await _categories.FindAsync(input.CategoryId, cancellationToken) is null)
            {
                throw new ExpenseValidationException("categoryId", "An existing category is required.");
            }

            code = code.ToUpperInvariant();
            if (previous is null && code != _baseCurrency.Code &&
                await _expenses.HasConflictingDatasetCurrencyAsync(_baseCurrency.Code, cancellationToken))
            {
                throw new ExpenseConflictException("The configured base currency differs from the dataset currency.");
            }

            var conversion = previous is not null && previous.Amount == input.Amount && previous.Currency == code
                ? previous.Conversion
                : await ConvertAsync(input.Amount, code, cancellationToken);
            return new Expense(previous?.Id ?? 0, input.Amount, code, input.CategoryId, date,
                string.IsNullOrEmpty(description) ? null : description, conversion);
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

            if (!table.BaseUnitsPerCurrencyUnit.TryGetValue(currency, out var rate))
            {
                throw new ExpenseValidationException("currency", "Currency is unsupported by the exchange-rate table.");
            }

            var applied = decimal.Round(rate, 12, MidpointRounding.ToEven);
            if (applied is <= 0m or >= 1000000m)
            {
                throw new RateUnavailableException();
            }

            return new ConversionSnapshot(table.BaseCurrency, applied, decimal.Round(amount * applied, 2, MidpointRounding.ToEven),
                table.Provider, table.PublishedAt.ToUniversalTime(), resolved.IsFallback);
        }
    }
}
