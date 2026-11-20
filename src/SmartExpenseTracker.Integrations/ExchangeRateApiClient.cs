using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartExpenseTracker.Application.ExchangeRates;

namespace SmartExpenseTracker.Integrations
{
    internal sealed class ExchangeRateApiClient : IExchangeRateProvider
    {
        private readonly HttpClient _client;
        private readonly TimeProvider _clock;
        private static readonly TimeSpan _maximumClockSkew = TimeSpan.FromMinutes(2);

        public ExchangeRateApiClient(HttpClient client, TimeProvider clock)
        {
            _client = client;
            _clock = clock;
        }

        public async Task<ExchangeRateTable> GetLatestAsync(string baseCurrency, CancellationToken cancellationToken)
        {
            if (!IsCurrencyCode(baseCurrency))
            {
                throw new ArgumentException("Expected a three-letter uppercase currency code.", nameof(baseCurrency));
            }

            try
            {
                using var response = await _client.GetAsync(baseCurrency, cancellationToken);
                Feed? feed;
                try
                {
                    using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                    feed = await JsonSerializer.DeserializeAsync<Feed>(body, _jsonOptions, cancellationToken);
                }
                catch (JsonException) when (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException("Unsuccessful exchange-rate HTTP response.", null, response.StatusCode);
                }

                if (feed is { Result: "error" } && !string.IsNullOrWhiteSpace(feed.ErrorType))
                {
                    throw new ExchangeRateApiException(feed.ErrorType, response.StatusCode);
                }

                response.EnsureSuccessStatusCode();
                return Normalize(feed, baseCurrency);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && exception.InnerException is TimeoutException)
            {
                throw new TimeoutException("The exchange-rate provider timed out.", exception);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Invalid exchange-rate response.", exception);
            }
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            AllowDuplicateProperties = false
        };

        private ExchangeRateTable Normalize(Feed? feed, string baseCurrency)
        {
            if (feed?.Result != "success" || feed.BaseCode != baseCurrency ||
                feed.PublishedUnix is <= 0 or > 253402300799 ||
                feed.Rates == null || !feed.Rates.TryGetValue(baseCurrency, out var identity) || identity != 1m ||
                feed.Rates.Count < 2)
            {
                throw new InvalidDataException("Incomplete or inconsistent exchange-rate response.");
            }

            var publishedAt = DateTimeOffset.FromUnixTimeSeconds(feed.PublishedUnix);
            if (publishedAt - _clock.GetUtcNow() > _maximumClockSkew)
            {
                throw new InvalidDataException("Exchange-rate publication time is in the future.");
            }

            var normalized = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var (code, rate) in feed.Rates)
            {
                if (!IsCurrencyCode(code) || rate <= 0 || 1m / rate == 0)
                {
                    throw new InvalidDataException("Invalid exchange-rate entry.");
                }

                normalized.Add(code, 1m / rate);
            }

            return new ExchangeRateTable("ExchangeRate-API", baseCurrency,
                publishedAt, normalized.ToFrozenDictionary(StringComparer.Ordinal));
        }

        private static bool IsCurrencyCode(string? value)
        {
            return value is { Length: 3 } && value.All(char.IsAsciiLetterUpper);
        }

        private sealed record Feed(
            [property: JsonPropertyName("result")] string? Result,
            [property: JsonPropertyName("error-type")] string? ErrorType,
            [property: JsonPropertyName("base_code")] string? BaseCode,
            [property: JsonPropertyName("time_last_update_unix")] long PublishedUnix,
            [property: JsonPropertyName("rates")] Dictionary<string, decimal>? Rates);
    }
}
