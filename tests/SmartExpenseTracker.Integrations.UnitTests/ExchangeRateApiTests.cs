using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SmartExpenseTracker.Application.ExchangeRates;
using SmartExpenseTracker.Integrations;

namespace SmartExpenseTracker.Integrations.UnitTests
{
    [CancelAfter(10000)]
    public sealed class ExchangeRateApiTests
    {
        private const string _validFeed = """
            {"result":"success","base_code":"EUR","time_last_update_unix":1700000000,"rates":{"EUR":1,"USD":1.25,"GBP":0.8}}
            """;

        [Test]
        public async Task PreservesAdvertisedNextUpdateAsUtcMetadata()
        {
            var feed = _validFeed.Replace("\"rates\"", "\"time_next_update_unix\":1700086400,\"rates\"");
            using var services = CreateServices((_, _) => Reply(feed));
            var table = await FetchAsync(services);
            Assert.That(table.NextUpdateAt!.Value.EqualsExact(DateTimeOffset.FromUnixTimeSeconds(1700086400)), Is.True);
        }

        [Test]
        public async Task LatestTableMapsTheRequestedBaseDirectionAndPublicationTime()
        {
            using var services = CreateServices((request, _) =>
            {
                Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
                Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://open.er-api.com/v6/latest/EUR"));
                return Reply(_validFeed);
            });

            var table = await FetchAsync(services);

            Assert.That(table.Provider, Is.EqualTo("ExchangeRate-API"));
            Assert.That(table.BaseCurrency, Is.EqualTo("EUR"));
            Assert.That(table.PublishedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1700000000)));
            Assert.That(table.PublishedAt.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(table.BaseUnitsPerCurrencyUnit["EUR"], Is.EqualTo(1m));
            Assert.That(table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(0.8m));
            Assert.That(table.BaseUnitsPerCurrencyUnit["GBP"], Is.EqualTo(1.25m));
        }

        [TestCase("null")]
        [TestCase("{")]
        [TestCase("""{"result":"success"}""")]
        [TestCase("""{"result":"error"}""")]
        public void RejectsInvalidPayload(string body)
        {
            using var services = CreateServices((_, _) => Reply(body));
            Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(services));
        }

        [TestCase("1.25", "0")]
        [TestCase("1.25", "-1")]
        [TestCase("1.25", "\"invalid\"")]
        [TestCase("USD", "US1")]
        [TestCase("GBP", "USD")]
        [TestCase("1700000000", "253402300800")]
        [TestCase("\"EUR\":1", "\"EUR\":2")]
        [TestCase("\"base_code\":\"EUR\"", "\"base_code\":\"GBP\"")]
        public void RejectsInvalidTableWithoutReturningPartialRates(string original, string replacement)
        {
            using var services = CreateServices((_, _) => Reply(_validFeed.Replace(original, replacement)));
            Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(services));
        }

        [TestCase(HttpStatusCode.OK, "unsupported-code", "unsupported-code")]
        [TestCase(HttpStatusCode.BadRequest, "unsupported-code", "unsupported-code")]
        [TestCase(HttpStatusCode.TooManyRequests, "quota-reached", "quota-reached")]
        [TestCase(HttpStatusCode.ServiceUnavailable, "untrusted upstream text", "unknown")]
        [TestCase(HttpStatusCode.OK, "untrusted upstream text", "unknown")]
        public void DeclaredProviderFailurePreservesStatusAndSafeCode(HttpStatusCode status, string upstream, string expected)
        {
            using var services = CreateServices((_, _) => Reply($$"""{"result":"error","error-type":"{{upstream}}"}""", status));
            var error = Assert.ThrowsAsync<ExchangeRateApiException>(() => services.GetRequiredService<IExchangeRateProvider>()
                .GetLatestAsync("ZZZ", TestContext.CurrentContext.CancellationToken));
            Assert.That(error!.ErrorCode, Is.EqualTo(expected));
            Assert.That(error.StatusCode, Is.EqualTo(status));
            Assert.That(error.Message, Does.Not.Contain(upstream));
        }

        [Test]
        public async Task NonEuroBaseIsUsedInUriAndMappedTable()
        {
            using var services = CreateServices((request, _) =>
            {
                Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://open.er-api.com/v6/latest/JPY"));
                return Reply(_validFeed.Replace("EUR", "JPY"));
            });
            var table = await services.GetRequiredService<IExchangeRateProvider>()
                .GetLatestAsync("JPY", TestContext.CurrentContext.CancellationToken);
            Assert.That(table.BaseCurrency, Is.EqualTo("JPY"));
            Assert.That(table.BaseUnitsPerCurrencyUnit["JPY"], Is.EqualTo(1m));
        }

        [TestCase("eur")]
        [TestCase("../USD")]
        [TestCase("")]
        public void InvalidCurrencyMakesZeroRequests(string input)
        {
            var calls = 0;
            using var services = CreateServices((_, _) =>
            {
                calls++;
                return Reply(_validFeed);
            });
            Assert.ThrowsAsync<ArgumentException>(() => services.GetRequiredService<IExchangeRateProvider>()
                .GetLatestAsync(input, TestContext.CurrentContext.CancellationToken));
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void PreservesTransportFailureWithoutResponse()
        {
            var failure = new HttpRequestException("Connection failed.");
            using var services = CreateServices((_, _) => throw failure);
            Assert.That(Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(services)), Is.SameAs(failure));
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(120)]
        public async Task PreservesPublicationThroughInclusiveClockSkewBoundary(int offsetSeconds)
        {
            var published = DateTimeOffset.FromUnixTimeSeconds(1700000000 + offsetSeconds);
            using var services = CreateServices((_, _) => Reply(_validFeed.Replace("1700000000", published.ToUnixTimeSeconds().ToString())));
            var table = await FetchAsync(services);
            Assert.That(table.PublishedAt.EqualsExact(published), Is.True);
        }

        [TestCase("1700000121")]
        [TestCase("253402300799")]
        public void RejectsFuturePublicationBeyondTolerance(string unix)
        {
            using var services = CreateServices((_, _) => Reply(_validFeed.Replace("1700000000", unix)));
            Assert.ThrowsAsync<InvalidDataException>(() => FetchAsync(services));
        }

        [Test]
        public void AppliesConfiguredTimeoutToProviderClient()
        {
            using var services = CreateServices((_, _) => Reply(_validFeed));
            using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IExchangeRateProvider));
            Assert.That(client.Timeout, Is.EqualTo(TimeSpan.FromSeconds(1)));
        }

        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void PreservesUnsuccessfulHttpStatus(HttpStatusCode status)
        {
            using var services = CreateServices((_, _) => Task.FromResult(new HttpResponseMessage(status)));
            var error = Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(services));
            Assert.That(error!.StatusCode, Is.EqualTo(status));
        }

        [Test]
        public async Task CallerCancellationReachesThePendingRequest()
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var services = CreateServices((_, token) =>
            {
                started.SetResult();
                return WaitForCancellationAsync(token);
            });
            using var caller = new CancellationTokenSource();
            var pending = services.GetRequiredService<IExchangeRateProvider>().GetLatestAsync("EUR", caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await caller.CancelAsync();

            Assert.CatchAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void DistinguishesClientTimeoutFromCallerCancellation()
        {
            using var services = CreateServices((_, token) => WaitForCancellationAsync(token));
            Assert.ThrowsAsync<TimeoutException>(() => FetchAsync(services));
        }

        private static async Task<HttpResponseMessage> WaitForCancellationAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        }

        private static Task<HttpResponseMessage> Reply(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        private static Task<ExchangeRateTable> FetchAsync(IServiceProvider services)
        {
            return services.GetRequiredService<IExchangeRateProvider>()
                .GetLatestAsync("EUR", TestContext.CurrentContext.CancellationToken);
        }

        private static ServiceProvider CreateServices(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(new FixedClock());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeRateApi:TimeoutSeconds"] = "1"
            }).Build();
            services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => new Handler(send)));
            services.AddIntegrations(configuration);
            return services.BuildServiceProvider();
        }

        private sealed class FixedClock : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000);
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

            public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
            {
                _send = send;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _send(request, cancellationToken);
            }
        }
    }
}
