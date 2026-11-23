using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SmartExpenseTracker.Caching
{
    public sealed class RateCacheOptions
    {
        internal static readonly TimeSpan MaximumAllowedAge = TimeSpan.FromHours(48);

        public string BaseCurrency { get; set; } = "EUR";
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(6);
        public TimeSpan MaximumAge { get; set; } = MaximumAllowedAge;
        public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(2);
        public TimeSpan RateLimitRetryInterval { get; set; } = TimeSpan.FromMinutes(20);
        public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    public static class RateCacheServiceCollectionExtensions
    {
        public static IServiceCollection AddRateCaching(this IServiceCollection services, IConfiguration configuration, string provider)
        {
            services.AddMemoryCache();
            services.TryAddSingleton(TimeProvider.System);
            services.AddOptions<RateCacheOptions>().Bind(configuration.GetSection("RateCache"))
                .Validate(o => o.BaseCurrency is { Length: 3 } && o.BaseCurrency.All(char.IsAsciiLetterUpper), "Invalid dataset base currency.")
                .Validate(o => o.MaximumAge <= RateCacheOptions.MaximumAllowedAge, "RateCache maximum age cannot exceed 48 hours.")
                .Validate(o => o.RefreshInterval > TimeSpan.Zero && o.MaximumAge >= o.RefreshInterval &&
                    o.RetryInterval > TimeSpan.Zero && o.RetryInterval < o.MaximumAge &&
                    o.RateLimitRetryInterval > TimeSpan.Zero && o.RateLimitRetryInterval < o.MaximumAge &&
                    o.RefreshTimeout > TimeSpan.Zero && o.RefreshTimeout <= TimeSpan.FromMinutes(5), "Invalid rate cache durations.")
                .ValidateOnStart();
            services.AddSingleton<IRateResolver>(sp => ActivatorUtilities.CreateInstance<RateResolver>(sp, provider));
            return services;
        }
    }
}
