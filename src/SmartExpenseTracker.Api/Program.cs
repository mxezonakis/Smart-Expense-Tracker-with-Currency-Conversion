using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Caching;
using SmartExpenseTracker.Integrations;
using SmartExpenseTracker.Persistence;

namespace SmartExpenseTracker.Api
{
    public sealed class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddPersistence(builder.Configuration);
            builder.Services.AddIntegrations(builder.Configuration);
            builder.Services.AddRateCaching(builder.Configuration, "ExchangeRate-API");
            builder.Services.AddScoped<CategoryService>();
            builder.Services.AddExceptionHandler<CategoryExceptionHandler>();
            builder.Services.AddApi(builder.Configuration);

            var app = builder.Build();
            app.UseExceptionHandler();
            app.UseStatusCodePages();
            app.MapControllers();
            app.MapOpenApi();
            app.Run();
        }
    }
}
