using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Presentation;

/// <summary>
/// Registers the backend-neutral compute adapters (<see cref="IGpuComputePipelineFactory"/>,
/// <see cref="IGpuImageFactory"/>, and <see cref="IGpuComputeCommandPoolFactory"/>) that wrap the Vulkan compute APIs. Kept apart from
/// <see cref="VulkanPresenterServiceRegistration"/> so that composition root's type coupling stays bounded.
/// </summary>
public static class VulkanComputeServiceRegistration {
    /// <summary>Registers the neutral compute pipeline, image and command pool adapters.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddVulkanComputeApis(this IServiceCollection services) {
        services.TryAddSingleton<IGpuComputeCommandPoolFactory>(implementationFactory: static sp => new VulkanGpuComputeCommandPoolFactory(commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>()));
        services.TryAddSingleton<IGpuComputePipelineFactory>(implementationFactory: static sp => new VulkanGpuComputePipelineFactory(computePipelineApi: sp.GetRequiredService<IVulkanComputePipelineApi>()));
        services.TryAddSingleton<IGpuImageFactory>(implementationFactory: static sp => new VulkanGpuImageFactory(
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>()
        ));
        // The compute-services bundle composes the nine granular compute factories/services a compute node drives
        // (the three above plus the recorder, the bindings, queue submitter, shader-module, storage-buffer, and
        // surface-transfer factories the presenter registers). Resolved lazily, so order with those is immaterial.
        services.TryAddSingleton<IGpuComputeServices, GpuComputeServices>();

        return services;
    }
}
