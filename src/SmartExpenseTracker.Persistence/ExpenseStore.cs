using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartExpenseTracker.Application.Expenses;

namespace SmartExpenseTracker.Persistence
{
    internal sealed class ExpenseStore : IExpenseStore
    {
        private readonly SmartExpenseTrackerDbContext _context;

        public ExpenseStore(SmartExpenseTrackerDbContext context)
        {
            _context = context;
        }

        private DbSet<ExpenseRow> Expenses => _context.Set<ExpenseRow>();

        private static readonly Expression<Func<ExpenseRow, Expense>> _projection = row => new Expense(
            row.Id, row.Amount, row.Currency, row.CategoryId, row.Date, row.Description,
            new ConversionSnapshot(row.BaseCurrency, row.AppliedRate, row.ConvertedAmount, row.Provider, row.PublishedAt, row.IsFallback));

        public Task<bool> HasConflictingDatasetCurrencyAsync(string baseCurrency, CancellationToken cancellationToken)
        {
            return _context.Set<DatasetRow>().AnyAsync(row => row.BaseCurrency != baseCurrency, cancellationToken);
        }

        public async Task<ExpenseSnapshot> CreateAsync(Expense expense, CancellationToken cancellationToken)
        {
            var currency = expense.Conversion.BaseCurrency;
            var dataset = _context.Set<DatasetRow>();
            if (await dataset.AnyAsync(row => row.BaseCurrency == currency, cancellationToken))
            {
                return await InsertAsync(expense, cancellationToken);
            }

            // Serialize only initialization. The foreign key protects subsequent ordinary inserts.
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"""IF NOT EXISTS (SELECT 1 FROM [ExpenseDataset] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = 1) INSERT INTO [ExpenseDataset] ([Id], [BaseCurrency]) VALUES (1, {currency})""",
                cancellationToken);
            if (!await _context.Set<DatasetRow>().AnyAsync(r => r.BaseCurrency == currency, cancellationToken))
            {
                throw new ExpenseConflictException("The configured base currency differs from the dataset currency.");
            }

            var created = await InsertAsync(expense, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return created;
        }

        private async Task<ExpenseSnapshot> InsertAsync(Expense expense, CancellationToken cancellationToken)
        {
            var row = new ExpenseRow { Currency = expense.Currency, BaseCurrency = expense.Conversion.BaseCurrency };
            Apply(row, expense);
            Expenses.Add(row);
            await SaveAsync(cancellationToken);
            return new ExpenseSnapshot(expense with { Id = row.Id }, row.Version);
        }

        public async Task<ExpenseSnapshot?> FindAsync(int id, CancellationToken cancellationToken)
        {
            var row = await Expenses.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
            return row is null ? null : new ExpenseSnapshot(_toExpense(row), row.Version);
        }

        public async Task<IReadOnlyList<Expense>> ListAsync(ExpenseQuery query, CancellationToken cancellationToken)
        {
            return await BuildListQuery(query).ToListAsync(cancellationToken);
        }

        private IQueryable<Expense> BuildListQuery(ExpenseQuery query)
        {
            var rows = Expenses.AsNoTracking();
            if (query.From is { } from)
            {
                rows = rows.Where(row => row.Date >= from);
            }

            if (query.To is { } to)
            {
                rows = rows.Where(row => row.Date <= to);
            }

            if (query.CategoryId is { } categoryId)
            {
                rows = rows.Where(row => row.CategoryId == categoryId);
            }

            if (query.Currency is { } currency)
            {
                rows = rows.Where(row => row.Currency == currency);
            }

            if (query.SortBy is null)
            {
                return rows.OrderBy(row => row.Id).Select(_projection);
            }

            var sorted = (query.SortBy, query.Descending) switch
            {
                (ExpenseSort.Amount, true) => rows.OrderByDescending(row => row.ConvertedAmount),
                (ExpenseSort.Amount, false) => rows.OrderBy(row => row.ConvertedAmount),
                (ExpenseSort.Date, true) => rows.OrderByDescending(row => row.Date),
                (ExpenseSort.Date, false) => rows.OrderBy(row => row.Date),
                _ => throw new ArgumentOutOfRangeException(nameof(query))
            };
            return sorted.ThenBy(row => row.Id).Select(_projection);
        }

        public async Task<byte[]> UpdateAsync(ExpenseSnapshot previous, Expense replacement, CancellationToken cancellationToken)
        {
            var original = previous.Expense;
            var row = new ExpenseRow
            {
                Id = original.Id,
                Currency = original.Currency,
                BaseCurrency = original.Conversion.BaseCurrency,
                Version = previous.Version.ToArray()
            };
            Apply(row, original);
            var entry = Expenses.Attach(row);
            entry.Property(r => r.Version).OriginalValue = previous.Version.ToArray();
            Apply(row, replacement);
            await SaveAsync(cancellationToken);
            return row.Version;
        }

        private static readonly Func<ExpenseRow, Expense> _toExpense = _projection.Compile();

        private static void Apply(ExpenseRow row, Expense expense)
        {
            row.Amount = expense.Amount;
            row.Currency = expense.Currency;
            row.CategoryId = expense.CategoryId;
            row.Date = expense.Date;
            row.Description = expense.Description;
            row.AppliedRate = expense.Conversion.AppliedRate;
            row.ConvertedAmount = expense.Conversion.ConvertedAmount;
            row.Provider = expense.Conversion.Provider;
            row.PublishedAt = expense.Conversion.PublishedAt;
            row.IsFallback = expense.Conversion.IsFallback;
        }

        private async Task SaveAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new ExpensePreconditionFailedException(exception);
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqlException sql &&
                (IsCategoryReferenceFailure(sql, "INSERT") || IsCategoryReferenceFailure(sql, "UPDATE")))
            {
                throw new ExpenseValidationException("categoryId", "An existing category is required.");
            }
        }

        internal static bool IsCategoryReferenceFailure(SqlException exception, string operation)
        {
            var kind = operation == "DELETE" ? "REFERENCE" : "FOREIGN KEY";
            var prefix = $"The {operation} statement conflicted with the {kind} constraint \"{ExpenseConfiguration.CategoryForeignKey}\".";
            return exception.Errors.Cast<SqlError>().Any(error =>
                error.Number == 547 && error.Message.StartsWith(prefix, StringComparison.Ordinal));
        }

        public async Task<bool> DeleteAsync(int id, byte[] expectedVersion, CancellationToken cancellationToken)
        {
            return await Expenses.Where(row => row.Id == id && row.Version == expectedVersion)
                .ExecuteDeleteAsync(cancellationToken) != 0;
        }
    }
}
