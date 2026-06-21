using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Presentation;

/// <summary>
/// Registers the backend-neutral compute adapters (<see cref="IGpuComputePipelineFactory"/>,
/// <see cref="IGpuStorageImageFactory"/>, <see cref="IGpuComputeCommandPoolFactory"/>, and
/// <see cref="IGpuComputeRecorder"/>) that wrap the Vulkan compute APIs. Kept apart from
/// <see cref="VulkanPresenterServiceRegistration"/> so that composition root's type coupling stays bounded.
/// </summary>
public static class VulkanComputeServiceRegistration {
    /// <summary>Registers the neutral compute pipeline, storage image, command pool, and recorder adapters.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddVulkanComputeApis(this IServiceCollection services) {
        // The Vulkan-only ray-query / acceleration-structure API (no neutral seam — D3D12 has no DXR). Previously
        // dead code; registered here so the world-acceleration builder and the ray-query (--world-rt) path can
        // resolve it. Harmless when unused — it self-resolves device entry points lazily on first call.
        services.TryAddSingleton<IVulkanAccelerationStructureApi>(static _ => new VulkanNativeAccelerationStructureApi());
        services.TryAddSingleton<IVulkanWorldAccelerationApi>(static sp => new VulkanWorldAccelerationApi(
            accelerationStructureApi: sp.GetRequiredService<IVulkanAccelerationStructureApi>()
        ));
        // The neutral acceleration-structure factory the ray-query render node resolves (the Vulkan peer of the
        // Direct3D 12 one). Wraps the world-acceleration builder; harmless when unused.
        services.TryAddSingleton<IGpuAccelerationStructureFactory>(static sp => new VulkanGpuAccelerationStructureFactory(
            worldAccelerationApi: sp.GetRequiredService<IVulkanWorldAccelerationApi>()
        ));
        services.TryAddSingleton<IGpuComputeCommandPoolFactory>(static sp => new VulkanGpuComputeCommandPoolFactory(
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>()
        ));
        services.TryAddSingleton<IGpuComputePipelineFactory>(static sp => new VulkanGpuComputePipelineFactory(
            computePipelineApi: sp.GetRequiredService<IVulkanComputePipelineApi>()
        ));
        services.TryAddSingleton<IGpuComputeRecorder>(static sp => new VulkanGpuComputeRecorder(
            recordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>()
        ));
        services.TryAddSingleton<IGpuStorageImageFactory>(static sp => new VulkanGpuStorageImageFactory(
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>()
        ));
        // GPU performance counters: the timestamp query-pool factory + recorder (the neutral timing seam). The
        // query-pool native API is registered with the other native APIs; the physical-device API supplies the period.
        services.TryAddSingleton<IGpuTimingPoolFactory>(static sp => new VulkanGpuTimingPoolFactory(
            physicalDeviceApi: sp.GetRequiredService<IVulkanPhysicalDeviceApi>(),
            queryPoolApi: sp.GetRequiredService<IVulkanQueryPoolApi>()
        ));
        services.TryAddSingleton<IGpuTimingRecorder>(static sp => new VulkanGpuTimingRecorder(
            queryPoolApi: sp.GetRequiredService<IVulkanQueryPoolApi>()
        ));
        // The compute-services bundle composes the nine granular compute factories/services a compute node drives
        // (the four above plus the descriptor allocator, queue submitter, shader-module, storage-buffer, and
        // surface-transfer factories the presenter registers). Resolved lazily, so order with those is immaterial.
        services.TryAddSingleton<IGpuComputeServices, GpuComputeServices>();

        return services;
    }
}
