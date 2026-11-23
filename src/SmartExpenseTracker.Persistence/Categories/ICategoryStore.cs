namespace SmartExpenseTracker.Persistence.Categories
{
    public sealed record Category(int Id, string Name)
    {
        public const int MaximumNameLength = 100;
    }

    public interface ICategoryStore
    {
        Task<Category> CreateAsync(string name, CancellationToken cancellationToken);
        Task<Category?> FindAsync(int id, CancellationToken cancellationToken);
        Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken);
        Task<bool> RenameAsync(int id, string name, CancellationToken cancellationToken);
        Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
    }

    public sealed class DuplicateCategoryNameException : Exception
    {
        public DuplicateCategoryNameException(Exception innerException)
            : base("A category with this name already exists.", innerException)
        {
        }
    }
}
