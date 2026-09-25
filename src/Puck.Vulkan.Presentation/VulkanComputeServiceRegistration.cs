using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Presentation;

/// <summary>
/// Registers the backend-neutral image and command-pool adapters (<see cref="IGpuImageFactory"/> and
/// <see cref="IGpuCommandPoolFactory"/>) that wrap the Vulkan compute APIs. Kept apart from
/// <see cref="VulkanPresenterServiceRegistration"/> so that composition root's type coupling stays bounded.
/// </summary>
public static class VulkanComputeServiceRegistration {
    /// <summary>Registers the neutral image and command pool adapters, each bound to the backend's device context.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddVulkanComputeApis(this IServiceCollection services) {
        services.TryAddSingleton<IGpuCommandPoolFactory>(implementationFactory: static sp => new VulkanGpuCommandPoolFactory(
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            deviceContext: sp.GetRequiredService<IVulkanDeviceContext>()
        ));
        services.TryAddSingleton<IGpuImageFactory>(implementationFactory: static sp => new VulkanGpuImageFactory(
            deviceContext: sp.GetRequiredService<IVulkanDeviceContext>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>()
        ));
        // The compute-services bundle composes the nine granular services a compute node drives (the two above plus the
        // recorder, bindings, pipeline, queue submitter, shader-module, buffer and surface-transfer factories the
        // presenter registers). Resolved lazily, so order with those is immaterial.
        services.TryAddSingleton<IGpuComputeServices, GpuComputeServices>();

        return services;
    }
}
