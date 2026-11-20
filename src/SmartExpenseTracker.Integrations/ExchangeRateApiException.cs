using System.Net;

namespace SmartExpenseTracker.Integrations
{
    public sealed class ExchangeRateApiException : HttpRequestException
    {
        internal ExchangeRateApiException(string errorCode, HttpStatusCode status)
            : base("The exchange-rate provider reported an error.", null, status)
        {
            ErrorCode = errorCode switch
            {
                "unsupported-code" or "base-code-only" or "quota-reached" or "malformed-request"
                    or "invalid-key" or "inactive-account" or "rate-limit-reached" => errorCode,
                _ => "unknown"
            };
        }

        public string ErrorCode { get; }
    }
}
