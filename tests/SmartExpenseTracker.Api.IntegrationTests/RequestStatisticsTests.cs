using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using SmartExpenseTracker.Api.Contracts;

namespace SmartExpenseTracker.Api.IntegrationTests
{
    public sealed class RequestStatisticsTests
    {
        [Test]
        public async Task EscapingExceptionPropagatesWithoutRecording()
        {
            var statistics = new RequestStatistics();
            statistics.Record(400, TimeSpan.FromMilliseconds(2.5));
            var before = statistics.Snapshot();
            var expected = new InvalidOperationException("Escaping downstream failure");
            RequestDelegate next = _ => Task.FromException(expected);
            var invoke = CreateMiddleware(next, statistics);
            var context = new DefaultHttpContext();

            Func<Task> action = () => invoke(context);
            var thrown = await action.Should().ThrowAsync<InvalidOperationException>();

            thrown.Which.Should().BeSameAs(expected);
            statistics.Snapshot().Should().Be(before);
        }

        [Test]
        public async Task CancelledRequestAbortedReturnsNormallyWithoutRecording()
        {
            var statistics = new RequestStatistics();
            statistics.Record(400, TimeSpan.FromMilliseconds(2.5));
            var before = statistics.Snapshot();
            using var cancellation = new CancellationTokenSource();
            var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
            RequestDelegate next = current =>
            {
                current.Response.StatusCode = StatusCodes.Status202Accepted;
                cancellation.Cancel();
                return Task.CompletedTask;
            };
            var invoke = CreateMiddleware(next, statistics);

            await invoke(context);

            context.RequestAborted.IsCancellationRequested.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
            statistics.Snapshot().Should().Be(before);
        }

        private static RequestDelegate CreateMiddleware(RequestDelegate next, RequestStatistics statistics)
        {
            // Bind the internal middleware's public contract without adding a production testing hook.
            var type = typeof(Program).Assembly.GetType("SmartExpenseTracker.Api.RequestStatisticsMiddleware", throwOnError: true)!;
            var middleware = Activator.CreateInstance(type, next, statistics)!;
            return type.GetMethod("InvokeAsync")!.CreateDelegate<RequestDelegate>(middleware);
        }

        [Test]
        public void SnapshotAccumulatesFractionalMillisecondsAndOnlyCountsCompletedErrors()
        {
            var statistics = new RequestStatistics();
            statistics.Snapshot().Should().Be(new RequestStatisticsSnapshot(0, 0, 0, 0));

            statistics.Record(200, TimeSpan.FromMilliseconds(1.5));
            statistics.Snapshot().Should().Be(new RequestStatisticsSnapshot(1, 0, 1.5, 1.5));
            statistics.Record(400, TimeSpan.FromMilliseconds(0.5));
            statistics.Record(599, TimeSpan.Zero);
            statistics.Record(600, TimeSpan.FromMilliseconds(2));
            statistics.Record(399, TimeSpan.FromMilliseconds(1));

            var expected = new RequestStatisticsSnapshot(5, 2, 5, 1);
            statistics.Snapshot().Should().Be(expected);
            statistics.Snapshot().Should().Be(expected);
        }

        [Test]
        public void ConcurrentRecordAndSnapshotPreserveAllCompoundUpdates()
        {
            var statistics = new RequestStatistics();
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    statistics.Record(500, TimeSpan.FromMilliseconds(2.5));
                    var snapshot = statistics.Snapshot();
                    snapshot.FailedRequests.Should().Be(snapshot.CompletedRequests);
                    snapshot.TotalDurationMs.Should().Be(2.5 * snapshot.CompletedRequests);
                    snapshot.AverageDurationMs.Should().Be(2.5);
                }
            });

            statistics.Snapshot().Should().Be(new RequestStatisticsSnapshot(8000, 8000, 20000, 2.5));
        }
    }
}
