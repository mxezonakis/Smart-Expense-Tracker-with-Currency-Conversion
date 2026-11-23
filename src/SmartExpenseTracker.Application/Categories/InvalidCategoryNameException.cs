namespace SmartExpenseTracker.Application.Categories
{
    public sealed class InvalidCategoryNameException : Exception
    {
        public InvalidCategoryNameException()
            : base("Name must contain 1 to 100 characters after trimming.")
        {
        }
    }
}
