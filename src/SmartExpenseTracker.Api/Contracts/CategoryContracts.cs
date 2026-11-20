namespace SmartExpenseTracker.Api.Contracts
{
    public sealed record CategoryRequest(string? Name)
    {
        internal const string NameDescription = "Required. Surrounding whitespace is trimmed before validation. " +
            "The trimmed name must contain 1–100 UTF-16 code units. Display casing, accents and internal spaces are preserved. " +
            "Uniqueness is case-insensitive and accent-sensitive.";
    }
    public sealed record CategoryResponse(int Id, string Name);
}
