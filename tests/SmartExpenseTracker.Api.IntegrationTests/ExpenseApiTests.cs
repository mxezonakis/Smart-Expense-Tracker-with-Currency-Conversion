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
            (await context.Database.GetAppliedMigrationsAsync(Token)).Should().Contain("20261120180100_AddExpenses");
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
            await AssertProblem(create, HttpStatusCode.BadRequest);
            _rates.Calls.Should().Be(1);
            categoryDeleted.Should().BeTrue();
            using var missing = await _client.GetAsync($"/api/categories/{_category}", Token);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var validation = await create.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(Token);
            validation!.Errors.Should().ContainKey("categoryId").WhoseValue.Should().NotBeEmpty();
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
                    ["GBP"] = 1.25m
                }.ToFrozenDictionary());
                return new ResolvedRates(new RateSnapshot(table, Publication), true);
            }
        }
    }
}
