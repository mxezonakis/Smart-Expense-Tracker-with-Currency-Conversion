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
            var input = _input with { Amount = decimal.Parse(value, CultureInfo.InvariantCulture) };
            Assert.ThrowsAsync<ExpenseValidationException>(() => _service.CreateAsync(input, Token))!.Field.Should().Be("amount");
            Fake.GetCalls(_resolver).Should().BeEmpty();
            Fake.GetCalls(_store).Should().BeEmpty();
        }

        [TestCase(null)]
        [TestCase("€")]
        [TestCase("UＳD")]
        public void RejectsMalformedCurrency(string? currency)
        {
            Assert.ThrowsAsync<ExpenseValidationException>(() => _service.CreateAsync(_input with { Currency = currency }, Token));
            Fake.GetCalls(_resolver).Should().BeEmpty();
        }

        [Test]
        public async Task IdentityUsesExactAmountWithoutResolution()
        {
            var expense = (await _service.CreateAsync(_input with { Amount = 99999999.99m, Currency = " eur " }, Token)).Expense;
            expense.Conversion.Should().Be(new ConversionSnapshot("EUR", 1m, 99999999.99m, null, null, false));
            Fake.GetCalls(_resolver).Should().BeEmpty();
        }

        [TestCase("10", "0.9255", "9.26")]
        [TestCase("10", "0.9245", "9.24")]
        [TestCase("0.01", "0.5", "0")]
        [TestCase("99999999.99", "999999.999999999999", "99999999990000.00")]
        public async Task MultipliesUsingTheAppliedRateAndRoundsOnce(string amount, string rate, string expected)
        {
            decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
            Quote(Parse(rate), true);
            var expense = (await _service.CreateAsync(_input with { Amount = Parse(amount) }, Token)).Expense;
            expense.Conversion.ConvertedAmount.Should().Be(Parse(expected));
            expense.Conversion.IsFallback.Should().BeTrue();
            expense.Currency.Should().Be("USD");
            expense.Description.Should().Be("Food");
            A.CallTo(() => _resolver.ResolveAsync(Token)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task RoundsRateBeforeMultiplyingAndWritesThatRateToTheSnapshot()
        {
            Quote(0.9245000000004m);

            await _service.CreateAsync(_input with { Amount = 10.00m }, Token);

            A.CallTo(() => _store.CreateAsync(A<Expense>.That.Matches(expense =>
                expense.Conversion.AppliedRate == 0.924500000000m && expense.Conversion.ConvertedAmount == 9.24m), Token))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void EstablishedMismatchPrecedesResolutionAndInsertion()
        {
            A.CallTo(() => _store.HasConflictingDatasetCurrencyAsync("EUR", Token)).Returns(true);
            A.CallTo(() => _resolver.ResolveAsync(Token)).ThrowsAsync(new RateUnavailableException());

            Assert.ThrowsAsync<ExpenseConflictException>(() => _service.CreateAsync(_input, Token));

            Fake.GetCalls(_resolver).Should().BeEmpty();
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [TestCase("0.0000000000005")]
        [TestCase("999999.9999999999999")]
        [TestCase("79228162514264337593543950335")]
        public void RejectsUnrepresentableRateBeforeArithmetic(string rate)
        {
            Quote(decimal.Parse(rate, CultureInfo.InvariantCulture));
            Assert.ThrowsAsync<RateUnavailableException>(() => _service.CreateAsync(_input, Token));
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [Test]
        public void CancelledResolutionDoesNotWrite()
        {
            A.CallTo(() => _resolver.ResolveAsync(Token)).ThrowsAsync(new OperationCanceledException(Token));
            Assert.ThrowsAsync<OperationCanceledException>(() => _service.CreateAsync(_input, Token))!
                .CancellationToken.Should().Be(Token);
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [Test]
        public void InvalidMetadataAndMissingCategoryDoNotResolveOrWrite()
        {
            A.CallTo(() => _categories.FindAsync(7, Token)).Returns((Category?)null);
            foreach (var input in new[] { _input with { Date = null }, _input with { Description = new string('x', 501) },
                _input with { CategoryId = 7 } })
            {
                Assert.ThrowsAsync<ExpenseValidationException>(() => _service.CreateAsync(input, Token));
            }

            Fake.GetCalls(_resolver).Should().BeEmpty();
            Fake.GetCalls(_store).Should().BeEmpty();
        }

        [Test]
        public void UnsupportedCurrencyIsValidationFailure()
        {
            Quote(0.9m);
            Assert.ThrowsAsync<ExpenseValidationException>(() => _service.CreateAsync(_input with { Currency = "ZZZ" }, Token))!
                .Field.Should().Be("currency");
            A.CallTo(() => _store.CreateAsync(A<Expense>._, Token)).MustNotHaveHappened();
        }

        [Test]
        public async Task NormalizedUnchangedReplacementNeverCallsPersistenceUpdate()
        {
            Quote(0.8234567890125m);
            var original = (await _service.CreateAsync(_input, Token)).Expense with { Id = 7 };
            var snapshot = new ExpenseSnapshot(original, [0, 0, 0, 0, 0, 0, 0, 7]);
            A.CallTo(() => _store.FindAsync(7, Token)).Returns(snapshot);
            var result = await _service.UpdateAsync(7, _input with { Amount = 10.000m, Currency = "USD", Description = "Food" }, snapshot.Version, Token);
            result!.Updated.Should().BeFalse();
            result.Version.Should().Equal(snapshot.Version);
            A.CallTo(() => _store.UpdateAsync(A<ExpenseSnapshot>._, A<Expense>._, Token)).MustNotHaveHappened();
            A.CallTo(() => _categories.FindAsync(1, Token)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _resolver.ResolveAsync(Token)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task MetadataEditsKeepSnapshotAndMonetaryEditsResolveAgain()
        {
            Quote(0.8234567890125m);
            var expense = (await _service.CreateAsync(_input, Token)).Expense with { Id = 7 };
            expense.Conversion.AppliedRate.Should().Be(0.823456789012m);
            var snapshot = new ExpenseSnapshot(expense, [0, 0, 0, 0, 0, 0, 0, 7]);
            A.CallTo(() => _store.FindAsync(7, Token)).Returns(snapshot);
            await _service.UpdateAsync(7, _input with { Date = _input.Date!.Value.AddDays(1), Description = null }, snapshot.Version, Token);
            A.CallTo(() => _store.UpdateAsync(snapshot, A<Expense>.That.Matches(e => e.Conversion == expense.Conversion), Token))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _resolver.ResolveAsync(Token)).MustHaveHappenedOnceExactly();

            Quote(0.9m);
            await _service.UpdateAsync(7, _input with { Amount = 20m }, snapshot.Version, Token);
            A.CallTo(() => _store.UpdateAsync(snapshot, A<Expense>.That.Matches(e => e.Conversion.ConvertedAmount == 18m), Token))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _resolver.ResolveAsync(Token)).ThrowsAsync(new RateUnavailableException());
            Assert.ThrowsAsync<RateUnavailableException>(() => _service.UpdateAsync(7, _input with { Amount = 21m }, snapshot.Version, Token));
            A.CallTo(() => _store.UpdateAsync(snapshot, A<Expense>._, Token)).MustHaveHappenedTwiceExactly();
            A.CallTo(() => _store.FindAsync(7, Token)).MustHaveHappened(3, Times.Exactly);
        }
    }
}
