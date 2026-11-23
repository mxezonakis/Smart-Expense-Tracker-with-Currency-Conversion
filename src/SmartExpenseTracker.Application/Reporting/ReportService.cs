using SmartExpenseTracker.Persistence.Reporting;

namespace SmartExpenseTracker.Application.Reporting
{
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
