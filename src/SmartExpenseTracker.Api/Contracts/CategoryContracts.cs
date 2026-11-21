namespace SmartExpenseTracker.Api.Contracts
{
    /// <summary>Fields used to create or rename a category.</summary>
    /// <param name="Name">Required name, trimmed to 1-100 UTF-16 code units. Uniqueness is case-insensitive and accent-sensitive; display casing and internal whitespace are preserved.</param>
    public sealed record CategoryRequest(string? Name)
    {
        internal const string NameDescription = "Required. Surrounding whitespace is trimmed before validation. " +
            "The trimmed name must contain 1–100 UTF-16 code units. Display casing, accents and internal spaces are preserved. " +
            "Uniqueness is case-insensitive and accent-sensitive.";
    }
    /// <summary>A stored expense category.</summary>
    /// <param name="Id">The database-generated category identifier.</param>
    /// <param name="Name">The trimmed display name.</param>
    public sealed record CategoryResponse(int Id, string Name);
}
