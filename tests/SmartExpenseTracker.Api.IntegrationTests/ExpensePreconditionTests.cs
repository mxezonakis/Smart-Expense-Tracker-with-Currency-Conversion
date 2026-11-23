using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Application.Expenses;
using SmartExpenseTracker.Application.ExchangeRates;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    public sealed class ExpensePreconditionTests
    {
        private static IEnumerable<TestCaseData> InvalidHeaders()
        {
            foreach (var method in new[] { "PUT", "DELETE" })
            {
                yield return new TestCaseData(method, null, 428).SetName($"{method}_MissingIfMatch");
                var values = new[] { "", " ", "AAAAAAAAAAA=", "W/\"AAAAAAAAAAA=\"", "*",
                    "\"AAAAAAAAAAA=\", \"AAAAAAAAAAE=\"", "\"!!!!!!!!!!!=\"", "\"AAAAAAAAAA==\"",
                    "\"AAAAAAAAAAAA\"", "\"AAAAAAAAAAB=\"", new string('a', 129) };
                for (var i = 0; i < values.Length; i++)
                {
                    yield return new TestCaseData(method, new[] { values[i] }, 400).SetName($"{method}_InvalidIfMatch_{i}");
                }

                yield return new TestCaseData(method, new[] { "\"AAAAAAAAAAA=\"", "\"AAAAAAAAAAE=\"" }, 400)
                    .SetName($"{method}_MultipleIfMatchHeaders");
            }
        }

        [TestCaseSource(nameof(InvalidHeaders))]
        public async Task InvalidPreconditionsNeverReachPersistenceOrRates(string method, string[]? headers, int status)
        {
            // Arrange
            var dependencies = new ForbiddenDependencies();
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IExpenseStore>(dependencies);
                    services.AddSingleton<IRateResolver>(dependencies);
                }));
            using var client = factory.CreateClient();

            // Act
            var context = await factory.Server.SendAsync(context =>
            {
                context.Request.Method = method;
                context.Request.Path = "/api/expenses/1";
                if (headers is not null)
                {
                    context.Request.Headers["If-Match"] = headers;
                }

                if (method == "PUT")
                {
                    var body = Encoding.UTF8.GetBytes("""{"amount":10,"currency":"USD","categoryId":1,"date":"2030-01-02"}""");
                    context.Request.ContentType = "application/json";
                    context.Request.ContentLength = body.Length;
                    context.Request.Body = new MemoryStream(body);
                }
            }, TestContext.CurrentContext.CancellationToken);

            // Assert
            context.Response.StatusCode.Should().Be(status);
            context.Response.ContentType.Should().StartWith("application/problem+json");
            using var problem = await JsonDocument.ParseAsync(context.Response.Body,
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(status);
            context.Response.Headers.ContainsKey("ETag").Should().BeFalse();
            dependencies.Calls.Should().Be(0);
        }

        private sealed class ForbiddenDependencies : IExpenseStore, IRateResolver
        {
            internal int Calls { get; private set; }
            private Task<T> Reject<T>()
            {
                Calls++;
                throw new InvalidOperationException("Preconditions must be checked before I/O.");
            }

            public Task<bool> HasConflictingDatasetCurrencyAsync(string baseCurrency, CancellationToken cancellationToken) => Reject<bool>();
            public Task<ExpenseSnapshot> CreateAsync(Expense expense, CancellationToken cancellationToken) => Reject<ExpenseSnapshot>();
            public Task<ExpenseSnapshot?> FindAsync(int id, CancellationToken cancellationToken) => Reject<ExpenseSnapshot?>();
            public Task<IReadOnlyList<Expense>> ListAsync(ExpenseQuery query, CancellationToken cancellationToken) => Reject<IReadOnlyList<Expense>>();
            public Task<byte[]> UpdateAsync(ExpenseSnapshot previous, Expense replacement, CancellationToken cancellationToken) => Reject<byte[]>();
            public Task<bool> DeleteAsync(int id, byte[] expectedVersion, CancellationToken cancellationToken) => Reject<bool>();
            public Task<ResolvedRates> ResolveAsync(CancellationToken cancellationToken) => Reject<ResolvedRates>();
        }
    }
}
