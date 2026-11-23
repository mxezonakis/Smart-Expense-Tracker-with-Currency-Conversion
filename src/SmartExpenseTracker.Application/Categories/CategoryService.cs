using SmartExpenseTracker.Persistence.Categories;

namespace SmartExpenseTracker.Application.Categories
{
    public sealed class CategoryService
    {
        private readonly ICategoryStore _store;

        public CategoryService(ICategoryStore store)
        {
            _store = store;
        }

        public Task<Category> CreateAsync(string? name, CancellationToken cancellationToken)
        {
            return _store.CreateAsync(Normalize(name), cancellationToken);
        }

        public Task<bool> RenameAsync(int id, string? name, CancellationToken cancellationToken)
        {
            return _store.RenameAsync(id, Normalize(name), cancellationToken);
        }

        public Task<Category?> FindAsync(int id, CancellationToken cancellationToken)
        {
            return _store.FindAsync(id, cancellationToken);
        }

        public Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken)
        {
            return _store.ListAsync(cancellationToken);
        }

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
        {
            return _store.DeleteAsync(id, cancellationToken);
        }

        private static string Normalize(string? name)
        {
            var trimmed = name?.Trim();
            if (trimmed is null || trimmed.Length is 0 or > Category.MaximumNameLength)
            {
                throw new InvalidCategoryNameException();
            }

            return trimmed;
        }
    }
}
