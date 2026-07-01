using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Memory;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Presentation;

namespace Puck.Demo;

/// <summary>
/// Registers the Vulkan neutral compute services a bespoke-device (off-host) compute world producer drives — the
/// Vulkan peer of <see cref="DirectXComputeWorldServices.AddDirectXComputeWorld"/>. Unlike the live Vulkan presenter,
/// this collection is PRESENTER-LESS (the reverse cross-backend producer renders compute only — the Direct3D 12 host
/// owns the window), so it must contribute everything the presenter normally would: the unmanaged allocator, the
/// native Vulkan APIs and factories, the neutral compute seam, and the five neutral GPU factories
/// (<see cref="IGpuComputeServices"/> composes all nine) the presenter otherwise registers.
/// </summary>
internal static class VulkanComputeWorldServices {
    /// <summary>Registers the standalone Vulkan compute service bundle for a bespoke off-host device.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddVulkanComputeWorld(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        // The native Vulkan APIs + factories the compute seam wraps (the live presenter registers these; an off-host
        // compute collection has no presenter, so it registers them itself), the unmanaged allocator they depend on,
        // and the neutral compute adapters + IGpuComputeServices bundle.
        services.AddPuckAllocator();
        services
            .AddVulkanNativeApis()
            .AddVulkanFactories()
            .AddVulkanComputeApis();

        // The queue submitter plus the five neutral factories the presenter would otherwise contribute — the same set
        // the presenter-less Direct3D 12 compute collection re-adds, so IGpuComputeServices resolves all nine members.
        services.TryAddSingleton<VulkanQueueSubmitter>();
        services.TryAddSingleton<IGpuDescriptorAllocator>(static sp => new VulkanGpuDescriptorAllocator(
            allocator: sp.GetRequiredService<VulkanDescriptorAllocator>()
        ));
        services.TryAddSingleton<IGpuQueueSubmitter>(static sp => new VulkanGpuQueueSubmitter(
            queueSubmitter: sp.GetRequiredService<VulkanQueueSubmitter>()
        ));
        services.TryAddSingleton<IGpuShaderModuleFactory>(static sp => new VulkanGpuShaderModuleFactory(
            shaderModuleFactory: sp.GetRequiredService<IVulkanShaderModuleFactory>()
        ));
        services.TryAddSingleton<IGpuStorageBufferFactory>(static sp => new VulkanGpuStorageBufferFactory(
            storageBufferFactory: sp.GetRequiredService<IVulkanStorageBufferFactory>()
        ));
        services.TryAddSingleton<IGpuSurfaceTransferFactory>(static sp => new VulkanGpuSurfaceTransferFactory(
            commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>(),
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            externalMemoryApi: sp.GetRequiredService<IVulkanExternalMemoryApi>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            frameReadbackApi: sp.GetRequiredService<IVulkanFrameReadbackApi>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>(),
            queueSubmitter: sp.GetRequiredService<VulkanQueueSubmitter>(),
            storageBufferFactory: sp.GetRequiredService<IVulkanStorageBufferFactory>()
        ));

        return services;
    }
}
