namespace SmartExpenseTracker.Api.Contracts
{
    /// <summary>Stored base-currency spending for one category.</summary>
    /// <param name="CategoryId">The category identifier.</param>
    /// <param name="TotalAmount">Sum of persisted converted amounts for this category; no new conversion or rounding is applied.</param>
    /// <param name="BaseCurrency">The dataset base currency of the total.</param>
    public sealed record CategoryTotalResponse(int CategoryId, decimal TotalAmount, string BaseCurrency);
    /// <summary>Stored base-currency spending for one calendar period.</summary>
    /// <param name="PeriodStart">The calendar date of the day, Monday of the week, or first day of the month in yyyy-MM-dd format.</param>
    /// <param name="TotalAmount">Sum of persisted converted amounts for expenses in this period.</param>
    /// <param name="BaseCurrency">The dataset base currency of the total.</param>
    public sealed record PeriodTotalResponse(DateOnly PeriodStart, decimal TotalAmount, string BaseCurrency);
}
