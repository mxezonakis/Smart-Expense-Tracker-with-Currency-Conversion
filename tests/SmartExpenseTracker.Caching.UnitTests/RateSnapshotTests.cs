using System.Collections.Frozen;
using FluentAssertions;
using NUnit.Framework;
using SmartExpenseTracker.Persistence.ExchangeRates;

namespace SmartExpenseTracker.Caching.UnitTests
{
    public sealed class RateSnapshotTests
    {
        private static readonly DateTimeOffset _now = new DateTimeOffset(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);

        [TestCase(-1, true)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        public void PublicationClockSkewHasAnInclusiveTwoMinuteBoundary(int extraTicks, bool expected)
        {
            // Arrange
            var snapshot = ValidSnapshot();
            snapshot = snapshot with { Table = snapshot.Table with { PublishedAt = _now.AddMinutes(2).AddTicks(extraTicks) } };

            // Act
            var actual = snapshot.IsValidAt(_now);

            // Assert
            actual.Should().Be(expected);
        }

        [TestCase(-1, true)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        public void RetrievalClockSkewHasAnInclusiveTwoMinuteBoundary(int extraTicks, bool expected)
        {
            // Arrange
            var snapshot = ValidSnapshot() with { RetrievedAt = _now.AddMinutes(2).AddTicks(extraTicks) };

            // Act
            var actual = snapshot.IsValidAt(_now);

            // Assert
            actual.Should().Be(expected);
        }

        [TestCase(0, true)]
        [TestCase(1, false)]
        public void PublicationCannotExceedRetrievalByMoreThanTwoMinutes(int extraTicks, bool expected)
        {
            // Arrange
            var snapshot = ValidSnapshot() with { RetrievedAt = _now.AddMinutes(-2).AddTicks(-extraTicks) };

            // Act
            var actual = snapshot.IsValidAt(_now);

            // Assert
            actual.Should().Be(expected);
        }

        [TestCase(64, true)]
        [TestCase(65, false)]
        public void ProviderNameHonorsStorageLengthBoundary(int length, bool expected)
        {
            // Arrange
            var snapshot = ValidSnapshot();
            snapshot = snapshot with { Table = snapshot.Table with { Provider = new string('p', length) } };

            // Act
            var actual = snapshot.IsValidAt(_now);

            // Assert
            actual.Should().Be(expected);
        }

        [TestCase("usd")]
        [TestCase("US")]
        [TestCase("UＳD")]
        public void InvalidRateCurrencyIsRejectedEvenWhenBaseIdentityIsValid(string currency)
        {
            // Arrange
            var snapshot = ValidSnapshot();
            snapshot = snapshot with
            {
                Table = snapshot.Table with
                {
                    BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal> { ["EUR"] = 1m, [currency] = 0.9m }.ToFrozenDictionary()
                }
            };

            // Act
            var actual = snapshot.IsValidAt(_now);

            // Assert
            actual.Should().BeFalse();
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void NonpositiveForeignRateInvalidatesTheWholeSnapshot(int rate)
        {
            // Arrange
            var snapshot = ValidSnapshot();
            snapshot = snapshot with
            {
                Table = snapshot.Table with
                {
                    BaseUnitsPerCurrencyUnit = new Dictionary<string, decimal> { ["EUR"] = 1m, ["USD"] = rate }.ToFrozenDictionary()
                }
            };

            // Act
            var actual = snapshot.IsValidAt(_now);

            // Assert
            actual.Should().BeFalse();
        }

        private static RateSnapshot ValidSnapshot()
        {
            return new RateSnapshot(new StoredExchangeRateTable("Test", "EUR", _now,
                new Dictionary<string, decimal> { ["EUR"] = 1m, ["USD"] = 0.9m }.ToFrozenDictionary()), _now);
        }
    }
}
