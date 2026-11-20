using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartExpenseTracker.Application.Categories;

namespace SmartExpenseTracker.Persistence
{
    public static class PersistenceServiceCollectionExtensions
    {
        public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddDbContext<SmartExpenseTrackerDbContext>(options => options.UseSqlServer(configuration.GetConnectionString("SmartExpenseTracker")));
            return services.AddScoped<ICategoryStore, CategoryStore>();
        }
    }
}
