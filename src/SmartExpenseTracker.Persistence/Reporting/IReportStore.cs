namespace SmartExpenseTracker.Persistence.Reporting
{
    public enum ReportPeriod
    {
        Day,
        Week,
        Month
    }

    public sealed record CategoryTotal(int CategoryId, decimal TotalAmount, string BaseCurrency);
    public sealed record PeriodTotal(DateOnly PeriodStart, decimal TotalAmount, string BaseCurrency);

    public interface IReportStore
    {
        Task<IReadOnlyList<CategoryTotal>> ByCategoryAsync(CancellationToken cancellationToken);
        Task<IReadOnlyList<PeriodTotal>> ByPeriodAsync(ReportPeriod period, CancellationToken cancellationToken);
    }
}
