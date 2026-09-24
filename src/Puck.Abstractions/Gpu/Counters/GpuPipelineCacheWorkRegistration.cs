using Microsoft.Extensions.DependencyInjection;

using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The one registration of a backend's <see cref="GpuPipelineCacheWork"/>, shared by every backend that composes a
/// <see cref="GpuPipelineCacheFile"/>: a Direct3D 12 or Vulkan device factory resolves the keyed instance to hand its
/// device context, and a counters collector discovers it through <see cref="IWorkCounterSource"/>.
/// </summary>
public static class GpuPipelineCacheWorkRegistration {
    /// <summary>Registers <paramref name="backend"/>'s <see cref="GpuPipelineCacheWork"/>, unless a keyed instance for
    /// the same backend is already registered — idempotent across repeated calls and across a container composing
    /// both backends.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="backend">The backend's name (<c>vulkan</c>, <c>directx</c>): the keyed service's key and the
    /// counter source's name suffix (<c>pipeline-cache.&lt;backend&gt;</c>).</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is empty.</exception>
    public static IServiceCollection AddGpuPipelineCacheWork(this IServiceCollection services, string backend) {
        ArgumentNullException.ThrowIfNull(argument: services);

        if (services.Any(predicate: descriptor => (descriptor.IsKeyedService && (descriptor.ServiceType == typeof(GpuPipelineCacheWork)) && Equals(
            objA: descriptor.ServiceKey,
            objB: backend
        )))) {
            return services;
        }

        var work = new GpuPipelineCacheWork(backend: backend);

        services.AddKeyedSingleton(
            implementationInstance: work,
            serviceKey: backend
        );
        services.AddSingleton<IWorkCounterSource>(implementationInstance: work);

        return services;
    }
}
