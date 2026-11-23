using System.Collections.Frozen;
using System.Globalization;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Application.ExchangeRates;
using SmartExpenseTracker.Application.Expenses;

namespace SmartExpenseTracker.Application.UnitTests
{
    public sealed class ExpenseServiceTests
    {
        private IExpenseStore _store = null!;
        private ICategoryStore _categories = null!;
        private IRateResolver _resolver = null!;
        private ExpenseService _service = null!;
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;
        private static readonly ExpenseInput _input = new ExpenseInput(10m, " usd ", 1, new DateOnly(2030, 1, 2), "  Food  ");

        [SetUp]
        public void Configure()
        {
            _store = A.Fake<IExpenseStore>();
            _resolver = A.Fake<IRateResolver>();
            _categories = A.Fake<ICategoryStore>();
            A.CallTo(() => _categories.FindAsync(1, Token)).Returns(new Category(1, "Travel"));
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token))
                .ReturnsLazily((Expense expense, CancellationToken _) => new ExpenseSnapshot(expense, new byte[8]));
            _service = new ExpenseService(_store, _categories, _resolver, new DatasetCurrency("EUR"));
        }

        private void Quote(decimal rate, bool fallback = false)
        {
            var publication = DateTimeOffset.UnixEpoch.AddYears(55);
            var table = new ExchangeRateTable("Test", "EUR", publication,
                new Dictionary<string, decimal> { ["USD"] = rate, ["EUR"] = 1m }.ToFrozenDictionary());
            A.CallTo(() => _resolver.ResolveAsync(Token)).Returns(new ResolvedRates(new RateSnapshot(table, publication), fallback));
        }

        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("0.001")]
        [TestCase("1.001")]
        [TestCase("100000000")]
        public void InvalidAmountNeverResolvesOrWrites(string value)
        {
            // Arrange
            var input = _input with { Amount = decimal.Parse(value, CultureInfo.InvariantCulture) };

            // Act
            var actual = Assert.ThrowsAsync<ExpenseValidationException>(() => _service.CreateAsync(input, Token))!.Field;

            // Assert
            actual.Should().Be("amount");
            Fake.GetCalls(_resolver).Should().BeEmpty();
            Fake.GetCalls(_store).Should().BeEmpty();
        }

        [TestCase(null)]
        [TestCase("€")]
        [TestCase("UＳD")]
        public void RejectsMalformedCurrency(string? currency)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            AsyncTestDelegate act = () => _service.CreateAsync(_input with { Currency = currency }, Token);

            // Assert
            Assert.ThrowsAsync<ExpenseValidationException>(act);
            Fake.GetCalls(_resolver).Should().BeEmpty();
        }

        [Test]
        public async Task IdentityUsesExactAmountWithoutResolution()
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var expense = (await _service.CreateAsync(_input with { Amount = 99999999.99m, Currency = " eur " }, Token)).Expense;

            // Assert
            expense.Conversion.Should().Be(new ConversionSnapshot("EUR", 1m, 99999999.99m, null, null, false));
            Fake.GetCalls(_resolver).Should().BeEmpty();
        }

        [TestCase("10", "0.9255", "9.26")]
        [TestCase("10", "0.9245", "9.24")]
        [TestCase("0.01", "0.5", "0")]
        [TestCase("99999999.99", "999999.999999999999", "99999999990000.00")]
        public async Task MultipliesUsingTheAppliedRateAndRoundsOnce(string amount, string rate, string expected)
        {
            // Arrange
            decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
            Quote(Parse(rate), true);

            // Act
            var expense = (await _service.CreateAsync(_input with { Amount = Parse(amount) }, Token)).Expense;

            // Assert
            expense.Conversion.ConvertedAmount.Should().Be(Parse(expected));
            expense.Conversion.IsFallback.Should().BeTrue();
            expense.Currency.Should().Be("USD");
            expense.Description.Should().Be("Food");
            A.CallTo(() => _resolver.ResolveAsync(Token)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task RoundsRateBeforeMultiplyingAndWritesThatRateToTheSnapshot()
        {
            // Arrange
            Quote(0.9245000000004m);

            // Act
            await _service.CreateAsync(_input with { Amount = 10.00m }, Token);

            // Assert
            A.CallTo(() => _store.CreateAsync(A<Expense>.That.Matches(expense =>
                expense.Conversion.AppliedRate == 0.924500000000m && expense.Conversion.ConvertedAmount == 9.24m), Token))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void EstablishedMismatchPrecedesResolutionAndInsertion()
        {
            // Arrange
            A.CallTo(() => _store.HasConflictingDatasetCurrencyAsync("EUR", Token)).Returns(true);
            A.CallTo(() => _resolver.ResolveAsync(Token)).ThrowsAsync(new RateUnavailableException());

            // Act
            AsyncTestDelegate act = () => _service.CreateAsync(_input, Token);

            // Assert
            Assert.ThrowsAsync<ExpenseConflictException>(act);

            Fake.GetCalls(_resolver).Should().BeEmpty();
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [TestCase("0.0000000000005")]
        [TestCase("999999.9999999999999")]
        [TestCase("79228162514264337593543950335")]
        public void RejectsUnrepresentableRateBeforeArithmetic(string rate)
        {
            // Arrange
            Quote(decimal.Parse(rate, CultureInfo.InvariantCulture));

            // Act
            AsyncTestDelegate act = () => _service.CreateAsync(_input, Token);

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [Test]
        public void CancelledResolutionDoesNotWrite()
        {
            // Arrange
            A.CallTo(() => _resolver.ResolveAsync(Token)).ThrowsAsync(new OperationCanceledException(Token));

            // Act
            var actual = Assert.ThrowsAsync<OperationCanceledException>(() => _service.CreateAsync(_input, Token))!
                .CancellationToken;

            // Assert
            actual.Should().Be(Token);
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [Test]
        public void InvalidMetadataAndMissingCategoryDoNotResolveOrWrite()
        {
            // Arrange
            A.CallTo(() => _categories.FindAsync(7, Token)).Returns((Category?)null);
            foreach (var input in new[] { _input with { Date = null }, _input with { Description = new string('x', 501) },
                _input with { CategoryId = 7 } })
            {
                // Act
                AsyncTestDelegate act = () => _service.CreateAsync(input, Token);

                // Assert
                Assert.ThrowsAsync<ExpenseValidationException>(act);
            }

            // Assert
            Fake.GetCalls(_resolver).Should().BeEmpty();
            Fake.GetCalls(_store).Should().BeEmpty();
        }

        [Test]
        public void UnsupportedCurrencyIsValidationFailure()
        {
            // Arrange
            Quote(0.9m);

            // Act
            var actual = Assert.ThrowsAsync<ExpenseValidationException>(() => _service.CreateAsync(_input with { Currency = "ZZZ" }, Token))!
                .Field;

            // Assert
            actual.Should().Be("currency");
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [Test]
        public void WrongBaseRateTableCannotBeUsedForConversion()
        {
            // Arrange
            var publication = DateTimeOffset.UnixEpoch.AddYears(55);
            var table = new ExchangeRateTable("Test", "GBP", publication,
                new Dictionary<string, decimal> { ["GBP"] = 1m, ["USD"] = 0.8m }.ToFrozenDictionary());
            A.CallTo(() => _resolver.ResolveAsync(Token)).Returns(new ResolvedRates(new RateSnapshot(table, publication), false));

            // Act
            AsyncTestDelegate act = () => _service.CreateAsync(_input, Token);

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);

            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t\u2003 ")]
        public async Task EmptyDescriptionsArePersistedAsNull(string? description)
        {
            // Arrange
            // Use the shared fixture and test-case inputs.

            // Act
            var result = await _service.CreateAsync(_input with { Currency = "EUR", Description = description }, Token);

            // Assert
            result.Expense.Description.Should().BeNull();
            result.Expense.Conversion.Should().Be(new ConversionSnapshot("EUR", 1m, 10m, null, null, false));
        }

        [Test]
        public async Task DescriptionLengthIsCheckedAfterTrimmingAndPublicationIsStoredInUtc()
        {
            // Arrange
            var publication = new DateTimeOffset(2030, 1, 2, 14, 0, 0, TimeSpan.FromHours(2));
            var table = new ExchangeRateTable("Controlled provider", "EUR", publication,
                new Dictionary<string, decimal> { ["EUR"] = 1m, ["USD"] = 0.9m }.ToFrozenDictionary());
            A.CallTo(() => _resolver.ResolveAsync(Token)).Returns(new ResolvedRates(new RateSnapshot(table, publication), true));

            // Act
            var result = await _service.CreateAsync(_input with { Description = " " + new string('x', 500) + "\t" }, Token);

            // Assert
            result.Expense.Description.Should().Be(new string('x', 500));
            result.Expense.Conversion.Should().Be(new ConversionSnapshot("EUR", 0.9m, 9m, "Controlled provider",
                new DateTimeOffset(2030, 1, 2, 12, 0, 0, TimeSpan.Zero), true));
            result.Expense.Conversion.PublishedAt!.Value.Offset.Should().Be(TimeSpan.Zero);
        }

        [Test]
        public async Task CurrencyOnlyEditRecalculatesAndReturnsThePersistedNewVersion()
        {
            // Arrange
            var original = new Expense(7, 10m, "EUR", 1, _input.Date!.Value, "Food",
                new ConversionSnapshot("EUR", 1m, 10m, null, null, false));
            var snapshot = new ExpenseSnapshot(original, [0, 0, 0, 0, 0, 0, 0, 7]);
            byte[] newVersion = [0, 0, 0, 0, 0, 0, 0, 8];
            A.CallTo(() => _store.FindAsync(7, Token)).Returns(snapshot);
            Expense? persisted = null;
            A.CallTo(() => _store.UpdateAsync(snapshot, A<Expense>._, Token))
                .Invokes((ExpenseSnapshot _, Expense replacement, CancellationToken _) => persisted = replacement)
                .Returns(newVersion);
            Quote(0.9m);

            // Act
            var result = await _service.UpdateAsync(7, _input, snapshot.Version, Token);

            // Assert
            result!.Updated.Should().BeTrue();
            result.Version.Should().Equal(newVersion);
            persisted.Should().Be(original with
            {
                Currency = "USD",
                Conversion = new ConversionSnapshot("EUR", 0.9m, 9m, "Test", DateTimeOffset.UnixEpoch.AddYears(55), false)
            });
            A.CallTo(() => _store.FindAsync(7, Token)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _resolver.ResolveAsync(Token)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task StaleAndMissingUpdatesDoNotValidateOrResolveRates()
        {
            // Arrange
            A.CallTo(() => _store.FindAsync(7, Token)).Returns((ExpenseSnapshot?)null);

            // Act
            var actual = (await _service.UpdateAsync(7, _input, new byte[8], Token));

            // Assert
            actual.Should().BeNull();

            // Arrange
            var original = new Expense(7, 10m, "EUR", 1, _input.Date!.Value, null,
                new ConversionSnapshot("EUR", 1m, 10m, null, null, false));
            A.CallTo(() => _store.FindAsync(7, Token)).Returns(new ExpenseSnapshot(original, [0, 0, 0, 0, 0, 0, 0, 1]));

            // Act
            AsyncTestDelegate act = () => _service.UpdateAsync(7, _input, new byte[8], Token);

            // Assert
            Assert.ThrowsAsync<ExpensePreconditionFailedException>(act);

            Fake.GetCalls(_categories).Should().BeEmpty();
            Fake.GetCalls(_resolver).Should().BeEmpty();
            A.CallTo(() => _store.UpdateAsync(A<ExpenseSnapshot>._, A<Expense>._, Token)).MustNotHaveHappened();
        }

        [Test]
        public async Task NormalizedUnchangedReplacementNeverCallsPersistenceUpdate()
        {
            // Arrange
            Quote(0.8234567890125m);

            // Act
            var original = (await _service.CreateAsync(_input, Token)).Expense with { Id = 7 };

            // Arrange
            var snapshot = new ExpenseSnapshot(original, [0, 0, 0, 0, 0, 0, 0, 7]);
            A.CallTo(() => _store.FindAsync(7, Token)).Returns(snapshot);

            // Act
            var result = await _service.UpdateAsync(7, _input with { Amount = 10.000m, Currency = "USD", Description = "Food" }, snapshot.Version, Token);

            // Assert
            result!.Updated.Should().BeFalse();
            result.Version.Should().Equal(snapshot.Version);
            A.CallTo(() => _store.UpdateAsync(A<ExpenseSnapshot>._, A<Expense>._, Token)).MustNotHaveHappened();
            A.CallTo(() => _categories.FindAsync(1, Token)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _resolver.ResolveAsync(Token)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task MetadataEditsKeepSnapshotAndMonetaryEditsResolveAgain()
        {
            // Arrange
            Quote(0.8234567890125m);

            // Act
            var expense = (await _service.CreateAsync(_input, Token)).Expense with { Id = 7 };

            // Assert
            expense.Conversion.AppliedRate.Should().Be(0.823456789012m);

            // Arrange
            var snapshot = new ExpenseSnapshot(expense, [0, 0, 0, 0, 0, 0, 0, 7]);
            A.CallTo(() => _store.FindAsync(7, Token)).Returns(snapshot);

            // Act
            await _service.UpdateAsync(7, _input with { Date = _input.Date!.Value.AddDays(1), Description = null }, snapshot.Version, Token);

            // Assert
            A.CallTo(() => _store.UpdateAsync(snapshot, A<Expense>.That.Matches(e => e.Conversion == expense.Conversion), Token))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _resolver.ResolveAsync(Token)).MustHaveHappenedOnceExactly();

            // Arrange
            Quote(0.9m);

            // Act
            await _service.UpdateAsync(7, _input with { Amount = 20m }, snapshot.Version, Token);

            // Assert
            A.CallTo(() => _store.UpdateAsync(snapshot, A<Expense>.That.Matches(e => e.Conversion.ConvertedAmount == 18m), Token))
                .MustHaveHappenedOnceExactly();

            // Arrange
            A.CallTo(() => _resolver.ResolveAsync(Token)).ThrowsAsync(new RateUnavailableException());

            // Act
            AsyncTestDelegate act = () => _service.UpdateAsync(7, _input with { Amount = 21m }, snapshot.Version, Token);

            // Assert
            Assert.ThrowsAsync<RateUnavailableException>(act);
            A.CallTo(() => _store.UpdateAsync(snapshot, A<Expense>._, Token)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _store.FindAsync(7, Token)).MustHaveHappened(3, Times.Exactly);
        }
    }
}
