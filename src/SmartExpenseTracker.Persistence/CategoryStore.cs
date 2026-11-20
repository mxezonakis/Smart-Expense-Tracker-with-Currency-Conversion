using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartExpenseTracker.Application.Categories;

namespace SmartExpenseTracker.Persistence
{
    internal sealed class CategoryStore : ICategoryStore
    {
        private readonly SmartExpenseTrackerDbContext _context;

        public CategoryStore(SmartExpenseTrackerDbContext context)
        {
            _context = context;
        }

        private DbSet<CategoryRow> Categories => _context.Set<CategoryRow>();

        public async Task<Category> CreateAsync(string name, CancellationToken cancellationToken)
        {
            var row = new CategoryRow { Name = name };
            Categories.Add(row);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqlException sql && IsNameConflict(sql))
            {
                throw new DuplicateCategoryNameException(exception);
            }

            return new Category(row.Id, row.Name);
        }

        public Task<Category?> FindAsync(int id, CancellationToken cancellationToken)
        {
            return Categories.Where(row => row.Id == id).Select(row => new Category(row.Id, row.Name))
                .SingleOrDefaultAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken)
        {
            return await Categories.OrderBy(row => row.Id).Select(row => new Category(row.Id, row.Name))
                .ToListAsync(cancellationToken);
        }

        public async Task<bool> RenameAsync(int id, string name, CancellationToken cancellationToken)
        {
            try
            {
                return await Categories.Where(row => row.Id == id)
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.Name, name), cancellationToken) != 0;
            }
            catch (SqlException exception) when (IsNameConflict(exception))
            {
                throw new DuplicateCategoryNameException(exception);
            }
        }

        private static bool IsNameConflict(SqlException exception)
        {
            // SQL Server exposes the index in localized text only. Fail closed for unknown formats.
            // Match the diagnostic prefix before the user-controlled duplicate value.
            const string prefix = "Cannot insert duplicate key row in object 'dbo.Categories' with unique index 'IX_Categories_Name'.";
            return exception.Errors.Cast<SqlError>().Any(error =>
                error.Number == 2601 && error.Message.StartsWith(prefix, StringComparison.Ordinal));
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
        {
            return await Categories.Where(row => row.Id == id).ExecuteDeleteAsync(cancellationToken) != 0;
        }
    }
}
