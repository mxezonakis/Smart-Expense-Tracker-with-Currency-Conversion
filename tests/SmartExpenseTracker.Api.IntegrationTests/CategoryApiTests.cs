using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Persistence;
using Testcontainers.MsSql;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    [NUnit.Framework.Category("SqlServer")]
    public sealed class CategoryApiTests
    {
        private const string _route = "/api/categories";
        private MsSqlContainer _server = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

        [OneTimeSetUp]
        public async Task StartSqlServer()
        {
            _server = SqlServerTestDatabase.Create();
            await _server.StartAsync(Token);
        }

        [SetUp]
        public async Task MigrateEmptyDatabase()
        {
            var connection = new SqlConnectionStringBuilder(_server.GetConnectionString())
            {
                InitialCatalog = $"SmartExpenseTracker_{Guid.NewGuid():N}"
            };

            _factory = new CategoryApiFactory(connection.ConnectionString);
            _client = _factory.CreateClient();
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            await context.Database.MigrateAsync(Token);
            (await context.Database.GetAppliedMigrationsAsync(Token)).Should().Contain("20261120000100_CreateCategories");
        }

        private sealed class CategoryApiFactory : WebApplicationFactory<Program>
        {
            private readonly string _connectionString;

            public CategoryApiFactory(string connectionString)
            {
                _connectionString = connectionString;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:SmartExpenseTracker"] = _connectionString }));
            }
        }

        [TearDown]
        public async Task DisposeHost()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [OneTimeTearDown]
        public async Task DisposeDatabaseContainer()
        {
            await _server.DisposeAsync();
        }

        [Test]
        public async Task CategorySurvivesIndependentRequestsAndCanBeRenamedAndDeleted()
        {
            using var created = await Create("  Food ");
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var location = created.Headers.Location!;
            var category = (await created.Content.ReadFromJsonAsync<CategoryResponse>(Token))!;
            category.Id.Should().BePositive();
            category.Name.Should().Be("Food");
            location.AbsolutePath.Should().Be($"{_route}/{category.Id}");
            (await _client.GetFromJsonAsync<CategoryResponse>(location, Token)).Should().Be(category);
            using var second = await Create("Books");
            second.EnsureSuccessStatusCode();
            var list = await _client.GetFromJsonAsync<CategoryResponse[]>(_route, Token);
            list!.Select(item => item.Name).Should().Equal("Food", "Books");

            using var updated = await _client.PutAsJsonAsync(location, new { name = " Dining " }, Token);
            updated.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await _client.GetFromJsonAsync<CategoryResponse>(location, Token))!.Name.Should().Be("Dining");
            using var deleted = await _client.DeleteAsync(location, Token);
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

            using var missing = await _client.GetAsync(location, Token);
            await AssertProblem(missing, HttpStatusCode.NotFound);
            using var missingUpdate = await _client.PutAsJsonAsync(location, new { name = "Dining" }, Token);
            await AssertProblem(missingUpdate, HttpStatusCode.NotFound);
            using var missingDelete = await _client.DeleteAsync(location, Token);
            await AssertProblem(missingDelete, HttpStatusCode.NotFound);
        }

        [Test]
        public async Task InvalidNamesProduceValidationErrors()
        {
            foreach (var name in new[] { null, "", " \t", new string('x', 101) })
            {
                using var response = await Create(name);
                var problem = await AssertProblem(response, HttpStatusCode.BadRequest);
                problem.GetProperty("errors").GetProperty("name").GetArrayLength().Should().BePositive();
            }
        }

        [Test]
        public async Task ConcurrentDuplicateCreatesAndDuplicateUpdateRespectUniqueIndex()
        {
            var barrier = new CategoryInsertBarrier();
            await using var concurrentHost = _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services.ConfigureDbContext<SmartExpenseTrackerDbContext>(
                    options => options.AddInterceptors(barrier))));
            using var client = concurrentHost.CreateClient();
            var requests = new[]
            {
                client.PostAsJsonAsync(_route, new { name = " Travel " }, Token),
                client.PostAsJsonAsync(_route, new { name = "trAVEL" }, Token)
            };
            var responses = await Task.WhenAll(requests);
            barrier.BothWritesArrived.Should().BeTrue();
            using var created = responses.Single(response => response.StatusCode == HttpStatusCode.Created);
            using var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            await AssertProblem(conflict, HttpStatusCode.Conflict);
            var list = await _client.GetFromJsonAsync<CategoryResponse[]>(_route, Token);
            list.Should().ContainSingle();

            using var second = await Create("Food");
            using var update = await _client.PutAsJsonAsync(second.Headers.Location, new { name = " TRAVEL " }, Token);
            await AssertProblem(update, HttpStatusCode.Conflict);
            (await _client.GetFromJsonAsync<CategoryResponse>(second.Headers.Location, Token))!.Name.Should().Be("Food");
        }

        [Test]
        public async Task SqlConstraintRejectsNoncanonicalNamesFromDirectWriters()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            foreach (var name in new[] { "", "\u2003Food", "Food\t" })
            {
                Func<Task> insert = () => context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO [Categories] ([Name]) VALUES ({name})", Token);

                var failure = await insert.Should().ThrowAsync<SqlException>();
                failure.Which.Number.Should().Be(547);
            }
        }

        [TestCase("application/json")]
        [TestCase("application/problem+json")]
        [TestCase("application/xml")]
        public async Task CategoryFailuresRespectAcceptHeader(string accept)
        {
            using var created = await Create("Dining");
            created.EnsureSuccessStatusCode();
            _client.DefaultRequestHeaders.Accept.ParseAdd(accept);

            using var invalid = await Create(" ");
            using var duplicate = await Create("DINING");
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
            if (accept == "application/xml")
            {
                (await invalid.Content.ReadAsByteArrayAsync(Token)).Should().BeEmpty();
                (await duplicate.Content.ReadAsByteArrayAsync(Token)).Should().BeEmpty();
                return;
            }

            var validation = await AssertProblem(invalid, HttpStatusCode.BadRequest);
            validation.GetProperty("errors").GetProperty("name").GetArrayLength().Should().BePositive();
            var conflict = await AssertProblem(duplicate, HttpStatusCode.Conflict);
            conflict.GetProperty("title").GetString().Should().Be("A category with this name already exists.");
        }

        [Test]
        public async Task NamesPreserveAccentsAndIgnoreCaseAfterTrimming()
        {
            using var plain = await Create(" Cafe ");
            using var accented = await Create(" Café ");
            plain.StatusCode.Should().Be(HttpStatusCode.Created);
            accented.StatusCode.Should().Be(HttpStatusCode.Created);

            using var upper = await Create(" CAFÉ ");
            await AssertProblem(upper, HttpStatusCode.Conflict);
            var list = await _client.GetFromJsonAsync<CategoryResponse[]>(_route, Token);
            list!.Select(category => category.Name).Should().Equal("Cafe", "Café");
        }

        [Test]
        public async Task PrimaryKeyCollisionPreservesSqlError()
        {
            using var created = await Create("Travel");
            created.EnsureSuccessStatusCode();
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            await context.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('dbo.Categories', RESEED, 0)", Token);

            var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();
            Func<Task> insert = () => store.CreateAsync("IX_Categories_Name", Token);
            var failure = await insert.Should().ThrowAsync<DbUpdateException>();
            failure.Which.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().Be(2627);
        }

        [Test]
        public async Task UnrelatedUniqueIndexOnRenamePreservesSqlError()
        {
            using var first = await Create("A");
            using var second = await Create("B");
            first.EnsureSuccessStatusCode();
            second.EnsureSuccessStatusCode();
            var category = (await second.Content.ReadFromJsonAsync<CategoryResponse>(Token))!;
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartExpenseTrackerDbContext>();
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE dbo.Categories ADD Initial AS LEFT(Name, 1) PERSISTED", Token);
            await context.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IX_Test_Initial ON dbo.Categories(Initial)", Token);

            var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();
            Func<Task> rename = () => store.RenameAsync(category.Id, "Another", Token);
            var failure = await rename.Should().ThrowAsync<SqlException>();
            failure.Which.Number.Should().Be(2601);
        }

        private Task<HttpResponseMessage> Create(string? name)
        {
            return _client.PostAsJsonAsync(_route, new { name }, Token);
        }

        private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
        {
            response.StatusCode.Should().Be(status);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            problem.GetProperty("status").GetInt32().Should().Be((int)status);
            problem.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
            return problem;
        }
    }
}
