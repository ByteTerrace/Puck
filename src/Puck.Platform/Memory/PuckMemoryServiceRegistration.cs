using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.Memory;

/// <summary>Dependency-injection registration for the engine's default unmanaged allocator.</summary>
public static class PuckMemoryServiceRegistration {
    /// <summary>
    /// Registers the process-wide default <see cref="IAllocator"/> — <see cref="Allocator.Current"/>, selected by
    /// the <c>Puck_ALLOCATOR</c> environment variable — unless one is already registered. Components that need
    /// unmanaged allocation (e.g. the Vulkan backend) depend only on the <see cref="IAllocator"/> abstraction and
    /// resolve it from the container, so the composition root is the single place that binds the concrete.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddPuckAllocator(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAllocator>(implementationFactory: static _ => Allocator.Current);

        return services;
    }
}
