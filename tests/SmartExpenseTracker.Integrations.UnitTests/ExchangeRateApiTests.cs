using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
            // Arrange
            var feed = _validFeed.Replace("\"rates\"", "\"time_next_update_unix\":1700086400,\"rates\"");
            using var services = CreateServices((_, _) => Reply(feed));

            // Act
            var table = await FetchAsync(services);

            // Assert
            Assert.That(table.NextUpdateAt!.Value.EqualsExact(DateTimeOffset.FromUnixTimeSeconds(1700086400)), Is.True);
        }

        [Test]
        public async Task LatestTableMapsTheRequestedBaseDirectionAndPublicationTime()
        {
            // Arrange
            using var services = CreateServices((request, _) =>
            {
                Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
                Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://open.er-api.com/v6/latest/EUR"));
                return Reply(_validFeed);
            });

            // Act
            var table = await FetchAsync(services);

            // Assert
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
            // Arrange
            using var services = CreateServices((_, _) => Reply(body));

            // Act
            AsyncTestDelegate act = () => FetchAsync(services);

            // Assert
            Assert.ThrowsAsync<InvalidDataException>(act);
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
            // Arrange
            using var services = CreateServices((_, _) => Reply(_validFeed.Replace(original, replacement)));

            // Act
            AsyncTestDelegate act = () => FetchAsync(services);

            // Assert
            Assert.ThrowsAsync<InvalidDataException>(act);
        }

        [TestCase(HttpStatusCode.OK, "unsupported-code", "unsupported-code")]
        [TestCase(HttpStatusCode.BadRequest, "unsupported-code", "unsupported-code")]
        [TestCase(HttpStatusCode.TooManyRequests, "quota-reached", "quota-reached")]
        [TestCase(HttpStatusCode.ServiceUnavailable, "untrusted upstream text", "unknown")]
        [TestCase(HttpStatusCode.OK, "untrusted upstream text", "unknown")]
        public void DeclaredProviderFailurePreservesStatusAndSafeCode(HttpStatusCode status, string upstream, string expected)
        {
            // Arrange
            using var services = CreateServices((_, _) => Reply($$"""{"result":"error","error-type":"{{upstream}}"}""", status));

            // Act
            var error = Assert.ThrowsAsync<ExchangeRateApiException>(() => services.GetRequiredService<IExchangeRateProvider>()
                .GetLatestAsync("ZZZ", TestContext.CurrentContext.CancellationToken));

            // Assert
            Assert.That(error!.ErrorCode, Is.EqualTo(expected));
            Assert.That(error.StatusCode, Is.EqualTo(status));
            Assert.That(error.Message, Does.Not.Contain(upstream));
        }

        [Test]
        public async Task NonEuroBaseIsUsedInUriAndMappedTable()
        {
            // Arrange
            using var services = CreateServices((request, _) =>
            {
                Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://open.er-api.com/v6/latest/JPY"));
                return Reply(_validFeed.Replace("EUR", "JPY"));
            });

            // Act
            var table = await services.GetRequiredService<IExchangeRateProvider>()
                .GetLatestAsync("JPY", TestContext.CurrentContext.CancellationToken);

            // Assert
            Assert.That(table.BaseCurrency, Is.EqualTo("JPY"));
            Assert.That(table.BaseUnitsPerCurrencyUnit["JPY"], Is.EqualTo(1m));
        }

        [TestCase("eur")]
        [TestCase("../USD")]
        [TestCase("")]
        public void InvalidCurrencyMakesZeroRequests(string input)
        {
            // Arrange
            var calls = 0;
            using var services = CreateServices((_, _) =>
            {
                calls++;
                return Reply(_validFeed);
            });

            // Act
            AsyncTestDelegate act = () => services.GetRequiredService<IExchangeRateProvider>()
                .GetLatestAsync(input, TestContext.CurrentContext.CancellationToken);

            // Assert
            Assert.ThrowsAsync<ArgumentException>(act);
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void PreservesTransportFailureWithoutResponse()
        {
            // Arrange
            var failure = new HttpRequestException("Connection failed.");
            using var services = CreateServices((_, _) => throw failure);

            // Act
            var actual = Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(services));

            // Assert
            Assert.That(actual, Is.SameAs(failure));
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(120)]
        public async Task PreservesPublicationThroughInclusiveClockSkewBoundary(int offsetSeconds)
        {
            // Arrange
            var published = DateTimeOffset.FromUnixTimeSeconds(1700000000 + offsetSeconds);
            using var services = CreateServices((_, _) => Reply(_validFeed.Replace("1700000000", published.ToUnixTimeSeconds().ToString())));

            // Act
            var table = await FetchAsync(services);

            // Assert
            Assert.That(table.PublishedAt.EqualsExact(published), Is.True);
        }

        [TestCase("1700000121")]
        [TestCase("253402300799")]
        public void RejectsFuturePublicationBeyondTolerance(string unix)
        {
            // Arrange
            using var services = CreateServices((_, _) => Reply(_validFeed.Replace("1700000000", unix)));

            // Act
            AsyncTestDelegate act = () => FetchAsync(services);

            // Assert
            Assert.ThrowsAsync<InvalidDataException>(act);
        }

        [Test]
        public void AppliesConfiguredTimeoutToProviderClient()
        {
            // Arrange
            using var services = CreateServices((_, _) => Reply(_validFeed));

            // Act
            using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IExchangeRateProvider));

            // Assert
            Assert.That(client.Timeout, Is.EqualTo(TimeSpan.FromSeconds(1)));
        }

        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void PreservesUnsuccessfulHttpStatus(HttpStatusCode status)
        {
            // Arrange
            using var services = CreateServices((_, _) => Task.FromResult(new HttpResponseMessage(status)));

            // Act
            var error = Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(services));

            // Assert
            Assert.That(error!.StatusCode, Is.EqualTo(status));
        }

        [Test]
        public async Task CallerCancellationReachesThePendingRequest()
        {
            // Arrange
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var services = CreateServices((_, token) =>
            {
                started.SetResult();
                return WaitForCancellationAsync(token);
            });
            using var caller = new CancellationTokenSource();

            // Act
            var pending = services.GetRequiredService<IExchangeRateProvider>().GetLatestAsync("EUR", caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await caller.CancelAsync();

            AsyncTestDelegate act = async () => await pending.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.CatchAsync<OperationCanceledException>(act);
        }

        [Test]
        public void DistinguishesClientTimeoutFromCallerCancellation()
        {
            // Arrange
            using var services = CreateServices((_, token) => WaitForCancellationAsync(token));

            // Act
            AsyncTestDelegate act = () => FetchAsync(services);

            // Assert
            Assert.ThrowsAsync<TimeoutException>(act);
        }

        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("{\"EUR\":1}")]
        [TestCase("{\"USD\":1.25,\"GBP\":0.8}")]
        [TestCase("{\"EUR\":1,\"USD\":79228162514264337593543950335}")]
        public void IncompleteOrUnrepresentableRatesNeverProduceATable(string rates)
        {
            // Arrange
            var feed = $$"""{"result":"success","base_code":"EUR","time_last_update_unix":1700000000,"rates":{{rates}}}""";
            using var services = CreateServices((_, _) => Reply(feed));

            // Act
            AsyncTestDelegate act = () => FetchAsync(services);

            // Assert
            Assert.ThrowsAsync<InvalidDataException>(act);
        }

        [TestCase("0")]
        [TestCase("-1")]
        public void PublicationMustBeAfterTheUnixEpoch(string publication)
        {
            // Arrange
            using var services = CreateServices((_, _) => Reply(_validFeed.Replace("1700000000", publication)));

            // Act
            AsyncTestDelegate act = () => FetchAsync(services);

            // Assert
            Assert.ThrowsAsync<InvalidDataException>(act);
        }

        [Test]
        public void DuplicateTopLevelFieldsAreRejectedRatherThanSelectingOneValue()
        {
            // Arrange
            var feed = _validFeed.Replace("\"result\":\"success\"", "\"result\":\"error\",\"result\":\"success\"");
            using var services = CreateServices((_, _) => Reply(feed));

            // Act
            AsyncTestDelegate act = () => FetchAsync(services);

            // Assert
            Assert.ThrowsAsync<InvalidDataException>(act);
        }

        [TestCase("null", null)]
        [TestCase("0", null)]
        [TestCase("-1", null)]
        [TestCase("253402300800", null)]
        [TestCase("1", 1L)]
        [TestCase("253402300799", 253402300799L)]
        public async Task NextUpdateTimestampHasIndependentOptionalBounds(string nextUpdate, long? expected)
        {
            // Arrange
            var feed = _validFeed.Replace("\"rates\"", $"\"time_next_update_unix\":{nextUpdate},\"rates\"");
            using var services = CreateServices((_, _) => Reply(feed));

            // Act
            var table = await FetchAsync(services);

            // Assert
            Assert.That(table.NextUpdateAt, Is.EqualTo(expected is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : (DateTimeOffset?)null));
            Assert.That(table.BaseUnitsPerCurrencyUnit["USD"], Is.EqualTo(0.8m));
        }

        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void ValidJsonCannotTurnAnUnsuccessfulHttpStatusIntoSuccess(HttpStatusCode status)
        {
            // Arrange
            using var services = CreateServices((_, _) => Reply(_validFeed, status));

            // Act
            var error = Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(services));

            // Assert
            Assert.That(error!.StatusCode, Is.EqualTo(status));
        }

        [TestCase("base-code-only")]
        [TestCase("malformed-request")]
        [TestCase("invalid-key")]
        [TestCase("inactive-account")]
        [TestCase("rate-limit-reached")]
        public void RecognizedProviderCodesRemainAvailableWithoutLeakingPayload(string code)
        {
            // Arrange
            using var services = CreateServices((_, _) => Reply($$"""{"result":"error","error-type":"{{code}}"}""", HttpStatusCode.BadRequest));

            // Act
            var error = Assert.ThrowsAsync<ExchangeRateApiException>(() => FetchAsync(services));

            // Assert
            Assert.That(error!.ErrorCode, Is.EqualTo(code));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error.Message, Is.EqualTo("The exchange-rate provider reported an error."));
        }

        [TestCase(0)]
        [TestCase(121)]
        public void InvalidTimeoutConfigurationFailsBeforeAnHttpRequest(int seconds)
        {
            // Arrange
            var calls = 0;
            using var services = CreateServices((_, _) =>
            {
                calls++;
                return Reply(_validFeed);
            }, seconds);

            // Act
            TestDelegate act = () => services.GetRequiredService<IExchangeRateProvider>();

            // Assert
            Assert.Throws<OptionsValidationException>(act);
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void MaximumConfiguredTimeoutIsAccepted()
        {
            // Arrange
            using var services = CreateServices((_, _) => Reply(_validFeed), 120);

            // Act
            using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IExchangeRateProvider));

            // Assert
            Assert.That(client.Timeout, Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(client.MaxResponseContentBufferSize, Is.EqualTo(256 * 1024));
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

        private static ServiceProvider CreateServices(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, int timeoutSeconds = 1)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(new FixedClock());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeRateApi:TimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
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
