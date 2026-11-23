using Microsoft.EntityFrameworkCore;
using SmartExpenseTracker.Persistence.Reporting;

namespace SmartExpenseTracker.Persistence
{
    internal sealed class ReportStore : IReportStore
    {
        private readonly SmartExpenseTrackerDbContext _context;

        public ReportStore(SmartExpenseTrackerDbContext context)
        {
            _context = context;
        }

        public async Task<IReadOnlyList<CategoryTotal>> ByCategoryAsync(CancellationToken cancellationToken)
        {
            return await _context.Set<ExpenseRow>()
                .GroupBy(row => new { row.CategoryId, row.BaseCurrency })
                .OrderBy(group => group.Key.CategoryId)
                .Select(group => new CategoryTotal(group.Key.CategoryId, group.Sum(row => row.ConvertedAmount), group.Key.BaseCurrency))
                .ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<PeriodTotal>> ByPeriodAsync(ReportPeriod period, CancellationToken cancellationToken)
        {
            var rows = _context.Set<ExpenseRow>();
            // 0001-01-01 was a Monday. The nonnegative day difference is independent of DATEFIRST.
            var buckets = period switch
            {
                ReportPeriod.Day => rows.Select(row => new { Start = row.Date, row.BaseCurrency, row.ConvertedAmount }),
                ReportPeriod.Week => rows.Select(row => new
                {
                    Start = row.Date.AddDays(-(EF.Functions.DateDiffDay(DateOnly.MinValue, row.Date) % 7)),
                    row.BaseCurrency,
                    row.ConvertedAmount
                }),
                ReportPeriod.Month => rows.Select(row => new
                {
                    Start = row.Date.AddDays(1 - row.Date.Day),
                    row.BaseCurrency,
                    row.ConvertedAmount
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(period))
            };
            return await buckets.GroupBy(row => new { row.Start, row.BaseCurrency })
                .OrderBy(group => group.Key.Start)
                .Select(group => new PeriodTotal(group.Key.Start, group.Sum(row => row.ConvertedAmount), group.Key.BaseCurrency))
                .ToListAsync(cancellationToken);
        }
    }
}
