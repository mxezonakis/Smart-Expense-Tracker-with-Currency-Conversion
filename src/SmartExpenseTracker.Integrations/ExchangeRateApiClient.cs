using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

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
                var feed = await ReadFeedAsync(response, cancellationToken);
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

        private static async Task<Feed?> ReadFeedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
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
            return feed;
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            AllowDuplicateProperties = false
        };

        private ExchangeRateTable Normalize(Feed? feed, string baseCurrency)
        {
            if (feed?.Result != "success" || feed.BaseCode != baseCurrency)
            {
                throw new InvalidDataException("Incomplete or inconsistent exchange-rate response.");
            }

            if (feed.Rates is not { Count: >= 2 } rates || rates.GetValueOrDefault(baseCurrency) != 1m)
            {
                throw new InvalidDataException("Incomplete or inconsistent exchange-rate response.");
            }

            var publishedAt = ReadTimestamp(feed.PublishedUnix)
                ?? throw new InvalidDataException("Incomplete or inconsistent exchange-rate response.");
            if (publishedAt - _clock.GetUtcNow() > _maximumClockSkew)
            {
                throw new InvalidDataException("Exchange-rate publication time is in the future.");
            }

            return new ExchangeRateTable("ExchangeRate-API", baseCurrency, publishedAt,
                NormalizeRates(rates), ReadTimestamp(feed.NextUpdateUnix));
        }

        private static FrozenDictionary<string, decimal> NormalizeRates(Dictionary<string, decimal> rates)
        {
            var normalized = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var (code, rate) in rates)
            {
                if (!IsCurrencyCode(code) || rate <= 0)
                {
                    throw new InvalidDataException("Invalid exchange-rate entry.");
                }

                var baseUnitsPerCurrencyUnit = 1m / rate;
                if (baseUnitsPerCurrencyUnit == 0)
                {
                    throw new InvalidDataException("Invalid exchange-rate entry.");
                }

                normalized.Add(code, baseUnitsPerCurrencyUnit);
            }

            return normalized.ToFrozenDictionary(StringComparer.Ordinal);
        }

        private static DateTimeOffset? ReadTimestamp(long? unixSeconds)
        {
            const long lastSupportedSecond = 253402300799;
            return unixSeconds is > 0 and <= lastSupportedSecond ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) : null;
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
            [property: JsonPropertyName("time_next_update_unix")] long? NextUpdateUnix,
            [property: JsonPropertyName("rates")] Dictionary<string, decimal>? Rates);
    }
}
