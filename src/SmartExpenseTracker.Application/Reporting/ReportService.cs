namespace SmartExpenseTracker.Application.Reporting
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

    public sealed class ReportService
    {
        private readonly IReportStore _store;

        public ReportService(IReportStore store)
        {
            _store = store;
        }

        public static ReportPeriod? ParsePeriod(string? value) => value switch
        {
            "day" => ReportPeriod.Day,
            "week" => ReportPeriod.Week,
            "month" => ReportPeriod.Month,
            _ => null
        };

        public Task<IReadOnlyList<CategoryTotal>> ByCategoryAsync(CancellationToken cancellationToken)
            => _store.ByCategoryAsync(cancellationToken);

        public Task<IReadOnlyList<PeriodTotal>> ByPeriodAsync(ReportPeriod period, CancellationToken cancellationToken)
            => _store.ByPeriodAsync(period, cancellationToken);
    }
}
