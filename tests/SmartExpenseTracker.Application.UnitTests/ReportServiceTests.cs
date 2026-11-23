using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using SmartExpenseTracker.Application.Reporting;
using SmartExpenseTracker.Persistence.Reporting;

namespace SmartExpenseTracker.Application.UnitTests
{
    public sealed class ReportServiceTests
    {
        [TestCase("day", ReportPeriod.Day)]
        [TestCase("week", ReportPeriod.Week)]
        [TestCase("month", ReportPeriod.Month)]
        public void ParsesSupportedPeriods(string value, ReportPeriod expected)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var actual = ReportService.ParsePeriod(value);

            // Assert
            actual.Should().Be(expected);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("0")]
        [TestCase("Day")]
        [TestCase("quarter")]
        public void RejectsOtherPeriods(string? value)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var actual = ReportService.ParsePeriod(value);

            // Assert
            actual.Should().BeNull();
        }

        [Test]
        public async Task PassesGroupingAndCallerTokenToStoreAndReturnsStoredTotals()
        {
            // Arrange
            using var source = new CancellationTokenSource();
            var store = A.Fake<IReportStore>(options => options.Strict());
            var service = new ReportService(store);
            IReadOnlyList<CategoryTotal> categories = [];
            IReadOnlyList<PeriodTotal> periods = [new PeriodTotal(new DateOnly(2028, 1, 1), 0m, "USD")];
            A.CallTo(() => store.ByCategoryAsync(source.Token)).Returns(categories);
            A.CallTo(() => store.ByPeriodAsync(ReportPeriod.Month, source.Token)).Returns(periods);

            // Act
            var actual = (await service.ByCategoryAsync(source.Token));

            // Assert
            actual.Should().BeEmpty();

            // Act
            var actual2 = (await service.ByPeriodAsync(ReportPeriod.Month, source.Token));

            // Assert
            actual2.Should().Equal(periods);
        }
    }
}
