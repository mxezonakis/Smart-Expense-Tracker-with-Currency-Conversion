using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Api.Contracts;
using SmartExpenseTracker.Application.Reporting;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    public sealed class StatisticsApiTests
    {
        private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

        [Test]
        public async Task SnapshotReadsAreExcludedAndHostsHaveIndependentCounters()
        {
            using var first = new WebApplicationFactory<Program>();
            using var second = new WebApplicationFactory<Program>();
            using var client = first.CreateClient();
            using var otherClient = second.CreateClient();
            var empty = new RequestStatisticsSnapshot(0, 0, 0, 0);
            (await ReadAsync(client)).Should().Be(empty);
            (await ReadAsync(client, "/API/Statistics/?sample=1")).Should().Be(empty);

            using var openApi = await client.GetAsync("/openapi/v1.json", Token);
            openApi.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync(Token));
            var operation = document.RootElement.GetProperty("paths").GetProperty("/api/statistics").GetProperty("get");
            operation.GetProperty("responses").TryGetProperty("200", out _).Should().BeTrue();
            var fields = document.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty("RequestStatisticsSnapshot").GetProperty("properties");
            fields.EnumerateObject().Select(field => field.Name).Should()
                .BeEquivalentTo("completedRequests", "failedRequests", "totalDurationMs", "averageDurationMs");

            var snapshot = await ReadAsync(client);
            snapshot.CompletedRequests.Should().Be(1);
            snapshot.FailedRequests.Should().Be(0);
            snapshot.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
            snapshot.AverageDurationMs.Should().Be(snapshot.TotalDurationMs);
            (await ReadAsync(client)).Should().Be(snapshot);
            (await ReadAsync(otherClient)).Should().Be(empty);
        }

        [Test]
        public async Task OverlappingReportsAndErrorsProduceExactCompletedCounts()
        {
            const int reports = 8;
            var store = new GatedReportStore(reports);
            using var factory = new WebApplicationFactory<Program>();
            using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddSingleton<IReportStore>(store)));
            using var client = host.CreateClient();
            var successes = Enumerable.Range(0, reports).Select(_ =>
                CheckResponseAsync(client, "/api/reports/by-category", HttpStatusCode.OK)).ToArray();
            double gatedDurationMs = 0;
            try
            {
                await store.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                long start = Stopwatch.GetTimestamp();
                var errors = Enumerable.Range(0, reports).SelectMany(_ => new[]
                {
                    CheckResponseAsync(client, "/api/reports/by-period?period=invalid", HttpStatusCode.BadRequest),
                    CheckResponseAsync(client, "/missing-route", HttpStatusCode.NotFound)
                });
                await Task.WhenAll(errors);
                var inFlight = await ReadAsync(client);
                inFlight.CompletedRequests.Should().Be(2 * reports);
                inFlight.FailedRequests.Should().Be(2 * reports);
                gatedDurationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            finally
            {
                store.Release.TrySetResult();
                await Task.WhenAll(successes);
            }

            var snapshot = await ReadAsync(client);
            snapshot.CompletedRequests.Should().Be(3 * reports);
            snapshot.FailedRequests.Should().Be(2 * reports);
            gatedDurationMs.Should().BeGreaterThan(0);
            // Allow only floating-point conversion noise, not an unmeasured duration.
            (snapshot.TotalDurationMs + 1e-9).Should().BeGreaterThanOrEqualTo(gatedDurationMs);
            snapshot.TotalDurationMs.Should().BeGreaterThan(0);
            snapshot.AverageDurationMs.Should().BeApproximately(snapshot.TotalDurationMs / snapshot.CompletedRequests, 1e-9);
        }

        [Test]
        public async Task HandledServerExceptionCountsOnceWithItsProblemStatus()
        {
            using var factory = new WebApplicationFactory<Program>();
            using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddScoped<IReportStore>(_ => throw new InvalidOperationException("Test failure"))));
            using var client = host.CreateClient();

            await CheckResponseAsync(client, "/api/reports/by-category", HttpStatusCode.InternalServerError);
            var snapshot = await ReadAsync(client);
            snapshot.CompletedRequests.Should().Be(1);
            snapshot.FailedRequests.Should().Be(1);
        }

        private static async Task<RequestStatisticsSnapshot> ReadAsync(HttpClient client, string path = "/api/statistics")
            => (await client.GetFromJsonAsync<RequestStatisticsSnapshot>(path, Token))!;

        private static async Task CheckResponseAsync(HttpClient client, string path, HttpStatusCode status)
        {
            using var response = await client.GetAsync(path, Token);
            response.StatusCode.Should().Be(status);
            if ((int)status >= 400)
            {
                response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
                body.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
            }
        }

        private sealed class GatedReportStore : IReportStore
        {
            private int _remaining;

            public GatedReportStore(int expected)
            {
                _remaining = expected;
            }

            public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<IReadOnlyList<CategoryTotal>> ByCategoryAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Decrement(ref _remaining) == 0)
                {
                    AllStarted.TrySetResult();
                }

                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                return [];
            }

            public Task<IReadOnlyList<PeriodTotal>> ByPeriodAsync(ReportPeriod period, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }
    }
}
