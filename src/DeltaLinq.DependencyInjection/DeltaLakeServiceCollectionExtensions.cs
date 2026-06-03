using DeltaLinq;

// Placed in the DI namespace so a single `using Microsoft.Extensions.DependencyInjection;` exposes it.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers DeltaLinq services in the dependency injection container.</summary>
public static class DeltaLakeServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IDeltaTableFactory"/> (and the shared <see cref="DeltaOptions"/>) as singletons.
    /// </summary>
    public static IServiceCollection AddDeltaLake(this IServiceCollection services, Action<DeltaOptions>? configure = null)
    {
        var options = new DeltaOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IDeltaTableFactory>(_ => new DeltaTableFactory(options));
        return services;
    }
}
