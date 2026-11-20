using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.ExchangeRates;
using SmartExpenseTracker.Persistence;
using Testcontainers.MsSql;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    [NUnit.Framework.Category("SqlServer")]
    public sealed class ExpenseApiTests
    {
        private const string _route = "/api/expenses";
        private MsSqlContainer _server = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private ControlledRates _rates = null!;
        private string _connection = null!;
        private int _category;
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

        [OneTimeSetUp]
        public async Task StartServer()
        {
            _server = SqlServerTestDatabase.Create();
            await _server.StartAsync(Token);
        }

        [SetUp]
        public async Task MigrateDatabase()
        {
            _connection = new SqlConnectionStringBuilder(_server.GetConnectionString())
            {
                InitialCatalog = $"SmartExpenseTracker_{Guid.NewGuid():N}",
                CurrentLanguage = "German"
            }.ConnectionString;
            _rates = new ControlledRates();
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SmartExpenseTracker"] = _connection
                }))
                .ConfigureServices(services => services.AddSingleton<IRateResolver>(_rates)));
            _client = _factory.CreateClient();
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            await context.Database.MigrateAsync(Token);
            var language = await context.Database.SqlQuery<string>($"SELECT @@LANGUAGE AS [Value]").SingleAsync(Token);
            language.Should().Be("us_english");
            (await context.Database.GetAppliedMigrationsAsync(Token)).Should().Contain(
                new[] { "20261120180100_AddExpenses", "20261120210000_AddExpenseQueryIndexes" });
            using var created = await _client.PostAsJsonAsync("/api/categories", new { name = "Travel" }, Token);
            created.EnsureSuccessStatusCode();
            _category = (await created.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;
        }

        [TearDown]
        public async Task DisposeHost()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [OneTimeTearDown]
        public async Task DisposeServer()
        {
            await _server.DisposeAsync();
        }

        private ExpenseRequest Input(string currency) => new ExpenseRequest(10m, currency, _category, new DateOnly(2030, 1, 2), null);

        [Test]
        public async Task MigratedExpensesTableHasExpectedQueryIndexKeys()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            var keys = await context.Database.SqlQuery<string>($"""
                SELECT CONCAT(i.name, '.', c.name) AS [Value]
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'dbo.Expenses', N'U')
                  AND i.name IN ('IX_Expenses_Date', 'IX_Expenses_Currency', 'IX_Expenses_ConvertedAmount')
                  AND ic.key_ordinal > 0
                """).ToListAsync(Token);

            keys.Should().BeEquivalentTo("IX_Expenses_Date.Date", "IX_Expenses_Currency.Currency",
                "IX_Expenses_ConvertedAmount.ConvertedAmount");
        }

        [Test]
        public async Task ListCombinesInclusiveFiltersAndSortsStoredValues()
        {
            async Task<int> Add(ExpenseRequest expense)
            {
                using var response = await _client.PostAsJsonAsync(_route, expense, Token);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!.Id;
            }

            async Task<int[]> Ids(string query)
            {
                var rows = await _client.GetFromJsonAsync<ExpenseResponse[]>($"{_route}?{query}", Token);
                return rows!.Select(row => row.Id).ToArray();
            }

            using var createCategory = await _client.PostAsJsonAsync("/api/categories", new { name = "Other" }, Token);
            createCategory.EnsureSuccessStatusCode();
            int otherCategory = (await createCategory.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;
            var usd = Input("USD");
            int first = await Add(usd);
            int second = await Add(usd);
            int euro = await Add(Input("EUR") with { Amount = 9.50m });
            int later = await Add(usd with { Date = usd.Date.AddDays(1) });
            int other = await Add(usd with { CategoryId = otherCategory });
            _rates.Available = false;

            var usdIds = await Ids("currency=USD");
            usdIds.Should().Equal(first, second, later, other);
            (await Ids("currency=%20uSd%20")).Should().Equal(usdIds);
            using var malformed = await _client.GetAsync($"{_route}?currency=%20US1%20", Token);
            await AssertProblem(malformed, HttpStatusCode.BadRequest);

            string filters = $"from=2030-01-02&to=2030-01-02&categoryId={_category}&currency=usd";
            (await Ids(filters)).Should().Equal(first, second);
            (await Ids("from=2030-01-03")).Should().Equal(later);
            (await Ids("to=2030-01-01")).Should().BeEmpty();
            (await Ids("sortBy=amount&direction=asc")).Should().Equal(first, second, later, other, euro);
            (await Ids("sortBy=amount&direction=desc")).Should().Equal(euro, first, second, later, other);
            (await Ids("sortBy=date&direction=asc")).Should().Equal(first, second, euro, other, later);
            (await Ids("sortBy=date")).Should().Equal(later, first, second, euro, other);
            (await Ids("")).Should().Equal(first, second, euro, later, other);
            _rates.Calls.Should().Be(4);
        }

        [Test]
        public async Task ListReturnsEveryMatchingExpenseInIdOrder()
        {
            var ids = new List<int>();
            for (int index = 0; index < 101; index++)
            {
                using var created = await _client.PostAsJsonAsync(_route, Input("EUR"), Token);
                created.EnsureSuccessStatusCode();
                ids.Add((await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!.Id);
            }

            var matches = await _client.GetFromJsonAsync<ExpenseResponse[]>($"{_route}?categoryId={_category}&currency=EUR", Token);
            matches!.Select(row => row.Id).Should().Equal(ids);
            _rates.Calls.Should().Be(0);
        }

        [Test]
        public async Task ReportsSumPersistedSnapshotsInSqlAcrossCalendarBoundaries()
        {
            using var category = await _client.PostAsJsonAsync("/api/categories", new { name = "Other" }, Token);
            category.EnsureSuccessStatusCode();
            int other = (await category.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;
            var january = new DateOnly(2028, 1, 1);
            var sunday = new DateOnly(2028, 12, 31);
            var monday = sunday.AddDays(1);
            var saturday = sunday.AddDays(-1);

            // Insert in reverse chronological order. CHF 0.01 at 0.5 rounds half-even to EUR 0.00.
            foreach (var expense in new[]
            {
                Input("EUR") with { Date = monday, Amount = 8m },
                Input("EUR") with { Date = sunday, Amount = 1m },
                Input("EUR") with { Date = saturday, Amount = 1m },
                Input("USD") with { Date = sunday },
                Input("CHF") with { Date = january, Amount = 0.01m, CategoryId = other }
            })
            {
                using var result = await _client.PostAsJsonAsync(_route, expense, Token);
                result.EnsureSuccessStatusCode();
            }

            var before = await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token);
            _rates.Available = false;
            int calls = _rates.Calls;
            var commands = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var reportHost = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.ConfigureDbContext<SmartExpenseTrackerDbContext>(options => options.LogTo(commands.Enqueue,
                    new[] { RelationalEventId.CommandExecuted }))));
            using var reports = reportHost.CreateClient();
            var categories = await reports.GetFromJsonAsync<CategoryTotalResponse[]>("/api/reports/by-category", Token);
            categories!.Should().Equal(new CategoryTotalResponse(_category, 19.26m, "EUR"), new CategoryTotalResponse(other, 0m, "EUR"));

            async Task CheckPeriods(string period, params PeriodTotalResponse[] expected)
            {
                var totals = await reports.GetFromJsonAsync<PeriodTotalResponse[]>($"/api/reports/by-period?period={period}", Token);
                totals!.Should().Equal(expected);
            }

            await CheckPeriods("day", new(january, 0m, "EUR"), new(saturday, 1m, "EUR"),
                new(sunday, 10.26m, "EUR"), new(monday, 8m, "EUR"));
            await CheckPeriods("week", new(new DateOnly(2027, 12, 27), 0m, "EUR"),
                new(new DateOnly(2028, 12, 25), 11.26m, "EUR"), new(monday, 8m, "EUR"));
            await CheckPeriods("month", new(january, 0m, "EUR"), new(new DateOnly(2028, 12, 1), 11.26m, "EUR"), new(monday, 8m, "EUR"));

            commands.Should().HaveCount(4);
            foreach (var command in commands)
            {
                command.Should().Contain("GROUP BY").And.Contain("SUM(").And.Contain("ConvertedAmount").And.Contain("ORDER BY");
            }

            (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!.Should().Equal(before!);
            _rates.Calls.Should().Be(calls);
        }

        [Test]
        public async Task ReportsReturnEmptyArraysForEmptyDataset()
        {
            foreach (string route in new[] { "by-category", "by-period?period=day", "by-period?period=week", "by-period?period=month" })
            {
                using var response = await _client.GetAsync($"/api/reports/{route}", Token);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                (await response.Content.ReadFromJsonAsync<object[]>(Token)).Should().BeEmpty();
            }

            using var invalid = await _client.GetAsync("/api/reports/by-period?period=year", Token);
            await AssertProblem(invalid, HttpStatusCode.BadRequest);
            _rates.Calls.Should().Be(0);
        }

        [Test]
        public async Task IdentityCrudAndReferencedCategoryRestriction()
        {
            var input = Input(" eur ");
            using var created = await _client.PostAsJsonAsync(_route, input, Token);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var location = created.Headers.Location!;
            var item = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            location.AbsolutePath.Should().Be($"{_route}/{item.Id}");
            item.Currency.Should().Be("EUR");
            item.ConvertedAmount.Should().Be(10m);
            item.AppliedRate.Should().Be(1m);
            item.Provider.Should().BeNull();
            item.PublishedAt.Should().BeNull();
            item.Description.Should().BeNull();
            (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!.Should().Equal(item);
            using var blocked = await _client.DeleteAsync($"/api/categories/{_category}", Token);
            await AssertProblem(blocked, HttpStatusCode.Conflict);
            using var updated = await _client.PutAsJsonAsync(location, input with { Description = "  Train  " }, Token);
            updated.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await _client.GetFromJsonAsync<ExpenseResponse>(location, Token))!.Description.Should().Be("Train");
            using var deleted = await _client.DeleteAsync(location, Token);
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
            using var missing = await _client.GetAsync(location, Token);
            await AssertProblem(missing, HttpStatusCode.NotFound);
            using var categoryDeleted = await _client.DeleteAsync($"/api/categories/{_category}", Token);
            categoryDeleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
            _rates.Calls.Should().Be(0);
        }

        [Test]
        public async Task StoredDecimalsAndProvenanceSurviveOutageAndMetadataEdits()
        {
            var input = Input("usd");
            using var created = await _client.PostAsJsonAsync(_route, input, Token);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var location = created.Headers.Location!;
            var first = (await created.Content.ReadFromJsonAsync<ExpenseResponse>(Token))!;
            first.AppliedRate.Should().Be(0.925500000001m);
            first.ConvertedAmount.Should().Be(9.26m);
            first.Provider.Should().Be("Test");
            first.IsFallback.Should().BeTrue();
            first.PublishedAt.Should().Be(ControlledRates.Publication);

            await using var connection = new SqlConnection(_connection);
            await connection.OpenAsync(Token);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Amount, AppliedRate, ConvertedAmount FROM Expenses WHERE Id = @id";
            command.Parameters.AddWithValue("@id", first.Id);
            await using (var reader = await command.ExecuteReaderAsync(Token))
            {
                (await reader.ReadAsync(Token)).Should().BeTrue();
                reader.GetDecimal(0).Should().Be(10m);
                reader.GetDecimal(1).Should().Be(0.925500000001m);
                reader.GetDecimal(2).Should().Be(9.26m);
            }

            _rates.Available = false;
            var metadata = input with { Description = "Ticket", Date = input.Date.AddYears(-10) };
            using var updated = await _client.PutAsJsonAsync(location, metadata, Token);
            updated.StatusCode.Should().Be(HttpStatusCode.NoContent);
            _rates.Calls.Should().Be(1);
            var expected = first with { Description = metadata.Description, Date = metadata.Date };
            (await _client.GetFromJsonAsync<ExpenseResponse>(location, Token)).Should().Be(expected);
            using var failed = await _client.PutAsJsonAsync(location, input with { Amount = 20m }, Token);
            await AssertProblem(failed, HttpStatusCode.ServiceUnavailable);
            (await _client.GetFromJsonAsync<ExpenseResponse>(location, Token)).Should().Be(expected);

            _rates.Available = true;
            using var repriced = await _client.PutAsJsonAsync(location, input with { Currency = "GBP" }, Token);
            repriced.StatusCode.Should().Be(HttpStatusCode.NoContent);
            var latest = (await _client.GetFromJsonAsync<ExpenseResponse>(location, Token))!;
            latest.AppliedRate.Should().Be(1.25m);
            latest.ConvertedAmount.Should().Be(12.50m);
            _rates.Calls.Should().Be(3);
        }

        [Test]
        public async Task UnknownCurrencyMissingCategoryAndUnavailableQuotesLeaveNoExpenses()
        {
            using var invalid = await _client.PostAsJsonAsync(_route, Input("ZZZ"), Token);
            await AssertProblem(invalid, HttpStatusCode.BadRequest);
            using var missing = await _client.PostAsJsonAsync(_route, Input("EUR") with { CategoryId = int.MaxValue }, Token);
            await AssertProblem(missing, HttpStatusCode.BadRequest);
            _rates.Available = false;
            using var unavailable = await _client.PostAsJsonAsync(_route, Input("GBP"), Token);
            await AssertProblem(unavailable, HttpStatusCode.ServiceUnavailable);
            (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!.Should().BeEmpty();
        }

        [Test]
        public async Task CategoryDeletedDuringRateResolutionLeavesNoExpense()
        {
            (await ReadDatasetAsync()).Should().BeEmpty();
            var observer = new FirstExpenseWriteObserver();
            await using var observed = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.ConfigureDbContext<SmartExpenseTrackerDbContext>(options => options.AddInterceptors(observer))));
            using var observedClient = observed.CreateClient();
            var categoryDeleted = false;
            _rates.BeforeQuote = async token =>
            {
                using var removed = await _client.DeleteAsync($"/api/categories/{_category}", token);
                removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
                categoryDeleted = true;
            };
            using var create = await observedClient.PostAsJsonAsync(_route, Input("USD"), Token);
            create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            create.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            _rates.Calls.Should().Be(1);
            categoryDeleted.Should().BeTrue();
            using var missing = await _client.GetAsync($"/api/categories/{_category}", Token);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var validation = await create.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(Token);
            validation!.Status.Should().Be(400);
            validation.Title.Should().NotBeNullOrWhiteSpace();
            validation.Detail.Should().BeNull();
            validation.Errors.Should().ContainKey("categoryId").WhoseValue.Should().NotBeEmpty();
            observer.PendingBase.Should().Be("EUR");
            observer.FailedTransaction.Should().NotBeNull().And.Be(observer.PendingTransaction);
            observer.SqlError.Should().Be(547);
            (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!.Should().BeEmpty();
            (await ReadDatasetAsync()).Should().BeEmpty();

            await using var changed = WithBaseCurrency("GBP");
            using var client = changed.CreateClient();
            using var category = await client.PostAsJsonAsync("/api/categories", new { name = "Meals" }, Token);
            category.EnsureSuccessStatusCode();
            var replacementCategory = (await category.Content.ReadFromJsonAsync<CategoryResponse>(Token))!.Id;
            using var retry = await client.PostAsJsonAsync(_route, Input("GBP") with { CategoryId = replacementCategory }, Token);
            retry.StatusCode.Should().Be(HttpStatusCode.Created);
            (await ReadDatasetAsync()).Should().Equal("GBP");
        }

        [Test]
        public async Task DatasetCurrencySurvivesLastDeletionAndRejectsConflictingBase()
        {
            using var created = await _client.PostAsJsonAsync(_route, Input("EUR"), Token);
            created.EnsureSuccessStatusCode();
            using var removed = await _client.DeleteAsync(created.Headers.Location, Token);
            removed.EnsureSuccessStatusCode();
            await using var changed = WithBaseCurrency("GBP");
            using var client = changed.CreateClient();
            _rates.Available = false;
            foreach (var currency in new[] { "GBP", "USD" })
            {
                using var conflict = await client.PostAsJsonAsync(_route, Input(currency), Token);
                await AssertProblem(conflict, HttpStatusCode.Conflict);
            }

            _rates.Calls.Should().Be(0);
            (await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, Token))!.Should().BeEmpty();
            (await ReadDatasetAsync()).Should().Equal("EUR");
            _rates.Available = true;
            using var matching = await _client.PostAsJsonAsync(_route, Input("USD"), Token);
            matching.StatusCode.Should().Be(HttpStatusCode.Created);
            _rates.Calls.Should().Be(1);
        }

        private WebApplicationFactory<Program> WithBaseCurrency(string currency)
        {
            return _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["RateCache:BaseCurrency"] = currency })));
        }

        private async Task<string[]> ReadDatasetAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            return await db.Database.SqlQuery<string>($"SELECT BaseCurrency AS [Value] FROM dbo.ExpenseDataset").ToArrayAsync(Token);
        }

        [Test]
        public async Task ConcurrentFirstInsertsCommitOnlyTheWinningBaseAndExpense()
        {
            await using var changed = WithBaseCurrency("GBP");
            using var client = changed.CreateClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var token = deadline.Token;
            await using var observer = new SqlConnection(_connection);
            await observer.OpenAsync(token);
            await using var gate = (SqlTransaction)await observer.BeginTransactionAsync(token);
            using var command = observer.CreateCommand();
            command.Transaction = gate;
            // This shared table lock admits ordinary reads but blocks the initialization update locks.
            command.CommandText = "SELECT COUNT(*) FROM dbo.ExpenseDataset WITH (TABLOCK, HOLDLOCK)";
            ((int)(await command.ExecuteScalarAsync(token))!).Should().Be(0);

            var eur = _client.PostAsJsonAsync(_route, Input("EUR"), token);
            var gbp = client.PostAsJsonAsync(_route, Input("GBP"), token);
            try
            {
                // Observe both SQL sessions inside the initialization batch before releasing either writer.
                command.CommandText = """
                    SELECT COUNT(DISTINCT r.session_id)
                    FROM sys.dm_exec_requests AS r
                    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS sqltext
                    WHERE r.database_id = DB_ID() AND r.session_id <> @@SPID AND r.wait_type LIKE 'LCK%'
                      AND sqltext.text LIKE '%UPDLOCK, HOLDLOCK%';
                    """;
                while ((int)(await command.ExecuteScalarAsync(token))! < 2)
                {
                    token.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                await gate.RollbackAsync(CancellationToken.None);
            }

            await Task.WhenAll(eur, gbp).WaitAsync(token);
            using var euroResponse = await eur;
            using var poundResponse = await gbp;
            new[] { euroResponse.StatusCode, poundResponse.StatusCode }.Should()
                .BeEquivalentTo(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            var euroWon = euroResponse.StatusCode == HttpStatusCode.Created;
            var winner = euroWon ? "EUR" : "GBP";
            await AssertProblem(euroWon ? poundResponse : euroResponse, HttpStatusCode.Conflict);
            (await ReadDatasetAsync()).Should().Equal(winner);
            var expenses = await _client.GetFromJsonAsync<ExpenseResponse[]>(_route, token);
            expenses!.Should().ContainSingle().Which.BaseCurrency.Should().Be(winner);
        }

        private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode expected)
        {
            response.StatusCode.Should().Be(expected);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(Token);
            problem!.Status.Should().Be((int)expected);
            problem.Title.Should().NotBeNullOrWhiteSpace();
            problem.Detail.Should().BeNull();
        }

        // Installed only on the failing POST host. Read the binding from the expense write's own transaction.
        private sealed class FirstExpenseWriteObserver : SaveChangesInterceptor
        {
            internal string? PendingBase { get; private set; }
            internal Guid? PendingTransaction { get; private set; }
            internal Guid? FailedTransaction { get; private set; }
            internal int? SqlError { get; private set; }

            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                var database = eventData.Context!.Database;
                PendingTransaction = database.CurrentTransaction?.TransactionId;
                PendingBase = await database.SqlQuery<string>($"SELECT BaseCurrency AS [Value] FROM dbo.ExpenseDataset")
                    .SingleOrDefaultAsync(cancellationToken);
                return result;
            }

            public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
            {
                FailedTransaction = eventData.Context!.Database.CurrentTransaction?.TransactionId;
                SqlError = (eventData.Exception.GetBaseException() as SqlException)?.Number;
                return Task.CompletedTask;
            }
        }

        private sealed class ControlledRates : IRateResolver
        {
            internal static DateTimeOffset Publication => DateTimeOffset.UnixEpoch.AddYears(55);
            internal bool Available { get; set; } = true;
            internal int Calls { get; private set; }
            internal Func<CancellationToken, Task>? BeforeQuote { get; set; }

            public async Task<ResolvedRates> ResolveAsync(CancellationToken cancellationToken)
            {
                Calls++;
                if (BeforeQuote is not null)
                {
                    await BeforeQuote(cancellationToken);
                }

                if (!Available)
                {
                    throw new RateUnavailableException();
                }

                var table = new ExchangeRateTable("Test", "EUR", Publication, new Dictionary<string, decimal>
                {
                    ["EUR"] = 1m,
                    ["USD"] = 0.925500000001m,
                    ["CHF"] = 0.5m,
                    ["GBP"] = 1.25m
                }.ToFrozenDictionary());
                return new ResolvedRates(new RateSnapshot(table, Publication), true);
            }
        }
    }
}
