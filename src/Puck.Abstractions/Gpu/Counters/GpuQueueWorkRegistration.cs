using Microsoft.Extensions.DependencyInjection;

using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>The one registration of the process's <see cref="GpuQueueWork"/> as a counter source, shared by every
/// backend.</summary>
public static class GpuQueueWorkRegistration {
    /// <summary>Registers <see cref="GpuQueueWork.Process"/> as an <see cref="IWorkCounterSource"/>, unless it is
    /// already registered: idempotent across repeated calls and across a container composing both backends.</summary>
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
}
