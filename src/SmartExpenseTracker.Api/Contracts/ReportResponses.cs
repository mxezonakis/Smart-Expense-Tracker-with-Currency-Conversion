namespace SmartExpenseTracker.Api.Contracts
{
    public sealed record CategoryTotalResponse(int CategoryId, decimal TotalAmount, string BaseCurrency);
    public sealed record PeriodTotalResponse(DateOnly PeriodStart, decimal TotalAmount, string BaseCurrency);
}
