using Microsoft.Extensions.DependencyInjection;

using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The one registration of each GPU counter source, shared by every backend: a Direct3D 12 or Vulkan device resolves
/// its keyed instance to count into, and a counters collector discovers it through <see cref="IWorkCounterSource"/>.
/// Every adder is idempotent across repeated calls and across a container composing both backends.
/// </summary>
public static class GpuWorkRegistration {
    /// <summary>Registers <paramref name="backend"/>'s <see cref="GpuDeviceMemoryWork"/>, unless a keyed instance for
    /// the same backend is already registered.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="backend">The backend's name (<c>vulkan</c>, <c>directx</c>): the keyed service's key and the
    /// counter source's name suffix (<c>memory.&lt;backend&gt;</c>).</param>
    /// <param name="owners">The observers each of the backend's segments and families reports its held bytes to
    /// (<see cref="GpuDeviceMemoryWork"/>), the host's account; <see langword="null"/> reports to none. The first
    /// registration of a backend decides it.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is empty.</exception>
    public static IServiceCollection AddGpuDeviceMemoryWork(this IServiceCollection services, string backend, IGpuMemoryOwners? owners = null) => AddKeyed(
        backend: backend,
        create: name => new GpuDeviceMemoryWork(backend: name, owners: owners),
        services: services
    );
    /// <summary>Registers <paramref name="backend"/>'s <see cref="GpuPipelineCacheWork"/>, shared by every backend that
    /// composes a <see cref="GpuPipelineCacheFile"/>, unless a keyed instance for the same backend is already
    /// registered.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="backend">The backend's name (<c>vulkan</c>, <c>directx</c>): the keyed service's key and the
    /// counter source's name suffix (<c>pipeline-cache.&lt;backend&gt;</c>).</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is empty.</exception>
    public static IServiceCollection AddGpuPipelineCacheWork(this IServiceCollection services, string backend) => AddKeyed(
        backend: backend,
        create: static name => new GpuPipelineCacheWork(backend: name),
        services: services
    );
    /// <summary>Registers <see cref="GpuQueueWork.Process"/> as an <see cref="IWorkCounterSource"/>, unless it is
    /// already registered.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddGpuQueueWork(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        if (!services.Any(predicate: static descriptor => ReferenceEquals(objA: descriptor.ImplementationInstance, objB: GpuQueueWork.Process))) {
            services.AddSingleton<IWorkCounterSource>(implementationInstance: GpuQueueWork.Process);
        }

        return services;
    }

    private static IServiceCollection AddKeyed<TWork>(IServiceCollection services, string backend, Func<string, TWork> create) where TWork : class, IWorkCounterSource {
        ArgumentNullException.ThrowIfNull(argument: services);

        if (services.Any(predicate: descriptor => (descriptor.IsKeyedService && (descriptor.ServiceType == typeof(TWork)) && Equals(
            objA: descriptor.ServiceKey,
            objB: backend
        )))) {
            return services;
        }

        var work = create(backend);

        services.AddKeyedSingleton(
            implementationInstance: work,
            serviceKey: backend
        );
        services.AddSingleton<IWorkCounterSource>(implementationInstance: work);

        return services;
    }
}
