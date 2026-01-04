using Microsoft.Extensions.DependencyInjection;
using PriceWatcher.Domain.Interfaces;
using PriceWatcher.Infrastructure.Sources;

namespace PriceWatcher.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProductSources(this IServiceCollection services)
    {
        services.AddSingleton<OzonProductSource>();
        services.AddSingleton<IProductSource>(sp => sp.GetRequiredService<OzonProductSource>());
        services.AddSingleton<IProductSourceResolver, ProductSourceResolver>();

        return services;
    }
}
