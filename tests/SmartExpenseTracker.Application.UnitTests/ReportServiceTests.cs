using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using SmartExpenseTracker.Application.Reporting;

namespace SmartExpenseTracker.Application.UnitTests
{
    public sealed class ReportServiceTests
    {
        [TestCase("day", ReportPeriod.Day)]
        [TestCase("week", ReportPeriod.Week)]
        [TestCase("month", ReportPeriod.Month)]
        public void ParsesSupportedPeriods(string value, ReportPeriod expected)
        {
            ReportService.ParsePeriod(value).Should().Be(expected);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("0")]
        [TestCase("Day")]
        [TestCase("quarter")]
        public void RejectsOtherPeriods(string? value)
        {
            ReportService.ParsePeriod(value).Should().BeNull();
        }

        [Test]
        public async Task PassesGroupingAndCallerTokenToStoreAndReturnsStoredTotals()
        {
            using var source = new CancellationTokenSource();
            var store = A.Fake<IReportStore>(options => options.Strict());
            var service = new ReportService(store);
            IReadOnlyList<CategoryTotal> categories = [];
            IReadOnlyList<PeriodTotal> periods = [new PeriodTotal(new DateOnly(2028, 1, 1), 0m, "USD")];
            A.CallTo(() => store.ByCategoryAsync(source.Token)).Returns(categories);
            A.CallTo(() => store.ByPeriodAsync(ReportPeriod.Month, source.Token)).Returns(periods);

            (await service.ByCategoryAsync(source.Token)).Should().BeEmpty();
            (await service.ByPeriodAsync(ReportPeriod.Month, source.Token)).Should().Equal(periods);
        }
    }
}
