using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartExpenseTracker.Application.ExchangeRates;

namespace SmartExpenseTracker.Integrations
{
    public static class IntegrationServiceCollectionExtensions
    {
        public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration configuration)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddOptions<ExchangeRateApiOptions>()
                .Bind(configuration.GetSection("ExchangeRateApi"))
                .Validate(options => options.TimeoutSeconds is >= 1 and <= 120, "ExchangeRateApi timeout must be 1 to 120 seconds.")
                .ValidateOnStart();
            services.AddHttpClient<IExchangeRateProvider, ExchangeRateApiClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<ExchangeRateApiOptions>>().Value;
                client.BaseAddress = new Uri("https://open.er-api.com/v6/latest/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                client.MaxResponseContentBufferSize = 256 * 1024;
            });
            return services;
        }
    }

    internal sealed class ExchangeRateApiOptions
    {
        public int TimeoutSeconds { get; set; } = 10;
    }
}
