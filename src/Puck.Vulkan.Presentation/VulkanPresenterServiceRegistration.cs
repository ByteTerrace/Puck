using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Vulkan.Apis;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Presentation;

/// <summary>
/// Composes the Vulkan presentation backend: the native Vulkan APIs and factories, the swapchain renderer and
/// its surface-blit compositor, and the <see cref="ISurfacePresenter"/> the host loop drives. It contributes
/// the Vulkan device as an inherited root capability (a <see cref="HostCapabilityContribution"/>) so a host
/// can resolve the device without referencing this backend. Pair it with a host that assembles the root
/// <see cref="IHostContext"/> from the contributions and drives the run loop.
/// </summary>
public static class VulkanPresenterServiceRegistration {
    /// <summary>Registers the Vulkan backend: native APIs, factories, the renderer/compositor, the
    /// <see cref="ISurfacePresenter"/>, and the Vulkan device capability contribution.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddVulkanPresenter(this IServiceCollection services) {
        var blitShaderDirectory = Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Shaders"
        );

        services
            .AddVulkanNativeApis()
            .AddVulkanFactories()
            .AddVulkanComputeApis();

        // The swapchain renderer, its surface-blit compositor, and the seam the host drives. The application
        // name is a cosmetic Vulkan instance label; it defaults to the running app, and a consumer may
        // register its own VulkanRendererOptions before calling this to override it.
        services.TryAddSingleton(instance: new VulkanRendererOptions {
            ApplicationName = AppDomain.CurrentDomain.FriendlyName,
        });
        // Neutral presentation preferences (present mode + surface format); a consumer may register its own
        // before calling this to override the defaults (Vsync + R8G8B8A8).
        services.TryAddSingleton(instance: new PresentationOptions());
        services.TryAddSingleton<VulkanRenderer>();
        // Publish the device context as a public interface so a consumer can compose its own root host
        // context (e.g. adding a DirectX device) without referencing the renderer type.
        services.TryAddSingleton<IVulkanDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<VulkanRenderer>());
        services.TryAddSingleton<VulkanQueueSubmitter>();
        services.TryAddSingleton(implementationFactory: sp => new SurfaceCompositor(
            commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>(),
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            descriptorApi: sp.GetRequiredService<IVulkanDescriptorApi>(),
            externalMemoryApi: sp.GetRequiredService<IVulkanExternalMemoryApi>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            graphicsPipelineFactory: sp.GetRequiredService<IVulkanGraphicsPipelineFactory>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>(),
            queueSubmitter: sp.GetRequiredService<VulkanQueueSubmitter>(),
            renderer: sp.GetRequiredService<VulkanRenderer>(),
            shaderDirectory: blitShaderDirectory,
            shaderModuleFactory: sp.GetRequiredService<IVulkanShaderModuleFactory>(),
            shaderModuleLoader: sp.GetRequiredService<IShaderModuleLoader>(),
            storageBufferFactory: sp.GetRequiredService<IVulkanStorageBufferFactory>(),
            vertexBufferFactory: sp.GetRequiredService<IVulkanVertexBufferFactory>()
        ));
        services.TryAddSingleton(implementationFactory: static sp => new VulkanSurfacePresenter(
            compositor: sp.GetRequiredService<SurfaceCompositor>(),
            renderer: sp.GetRequiredService<VulkanRenderer>()
        ));
        services.TryAddSingleton<ISurfacePresenter>(implementationFactory: static sp => sp.GetRequiredService<VulkanSurfacePresenter>());

        // Backend-neutral GPU abstractions: adapters that wrap the Vulkan-specific services above and
        // implement the IGpu* interfaces the render nodes (the compute world producer, and future nodes) drive.
        services.TryAddSingleton<IGpuCommandRecorder>(implementationFactory: static sp => new VulkanGpuCommandRecorder(
            commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>()
        ));
        services.TryAddSingleton<IGpuDescriptorAllocator>(implementationFactory: static sp => new VulkanGpuDescriptorAllocator(
            allocator: sp.GetRequiredService<VulkanDescriptorAllocator>()
        ));
        services.TryAddSingleton<IGpuPipelineFactory>(implementationFactory: static sp => new VulkanGpuPipelineFactory(
            pipelineFactory: sp.GetRequiredService<IVulkanGraphicsPipelineFactory>()
        ));
        services.TryAddSingleton<IGpuQueueSubmitter>(static sp => new VulkanGpuQueueSubmitter(
            frameSynchronizationApi: sp.GetRequiredService<IVulkanFrameSynchronizationApi>(),
            queueSubmitter: sp.GetRequiredService<VulkanQueueSubmitter>()
        ));
        services.TryAddSingleton<IGpuRenderTargetFactory>(implementationFactory: static sp => new VulkanGpuRenderTargetFactory(
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>(),
            renderPassApi: sp.GetRequiredService<IVulkanRenderPassApi>()
        ));
        services.TryAddSingleton<IGpuShaderModuleFactory>(implementationFactory: static sp => new VulkanGpuShaderModuleFactory(
            shaderModuleFactory: sp.GetRequiredService<IVulkanShaderModuleFactory>()
        ));
        // Optional capability: a Vulkan host can export a render target in shared device memory (an opaque Win32 NT
        // handle) for ANOTHER Vulkan instance to import zero-copy. A host resolves this when present and falls back
        // to the CPU-pixel transport otherwise. Unlike Direct3D 12's export, an opaque-Vulkan handle is not
        // importable by D3D12 — this is a Vulkan-to-Vulkan capability.
        services.TryAddSingleton<IGpuSurfaceExportFactory>(implementationFactory: static sp => new VulkanGpuSurfaceExportFactory(
            commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>(),
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            externalMemoryApi: sp.GetRequiredService<IVulkanExternalMemoryApi>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            queueSubmitter: sp.GetRequiredService<VulkanQueueSubmitter>(),
            renderPassApi: sp.GetRequiredService<IVulkanRenderPassApi>()
        ));
        services.TryAddSingleton<IGpuStorageBufferFactory>(implementationFactory: static sp => new VulkanGpuStorageBufferFactory(
            storageBufferFactory: sp.GetRequiredService<IVulkanStorageBufferFactory>()
        ));
        services.TryAddSingleton<IGpuSurfaceTransferFactory>(implementationFactory: static sp => new VulkanGpuSurfaceTransferFactory(
            commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>(),
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            externalMemoryApi: sp.GetRequiredService<IVulkanExternalMemoryApi>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            frameReadbackApi: sp.GetRequiredService<IVulkanFrameReadbackApi>(),
            frameSynchronizationApi: sp.GetRequiredService<IVulkanFrameSynchronizationApi>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>(),
            queueSubmitter: sp.GetRequiredService<VulkanQueueSubmitter>(),
            storageBufferFactory: sp.GetRequiredService<IVulkanStorageBufferFactory>()
        ));
        services.TryAddSingleton<IGpuVertexBufferFactory>(implementationFactory: static sp => new VulkanGpuVertexBufferFactory(
            vertexBufferFactory: sp.GetRequiredService<IVulkanVertexBufferFactory>()
        ));

        // Contribute the Vulkan device as an inherited root capability that flows to every node. The host
        // aggregates this with any other contributions into the root host context, so this backend stays free
        // of host- and application-specific concerns.
        services.AddSingleton(implementationFactory: static sp => new HostCapabilityContribution(
            CapabilityType: typeof(IVulkanDeviceContext),
            Instance: sp.GetRequiredService<VulkanRenderer>(),
            IsHeld: false
        ));
        services.AddSingleton(implementationFactory: static sp => new HostCapabilityContribution(
            CapabilityType: typeof(IGpuDeviceContext),
            Instance: sp.GetRequiredService<VulkanRenderer>(),
            IsHeld: false
        ));

        return services;
    }

    /// <summary>Registers one native API per Vulkan capability the renderer, compositor, and engine use.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddVulkanNativeApis(this IServiceCollection services) {
        services.TryAddSingleton<IVulkanCommandBufferRecordingApi>(implementationFactory: static sp => new VulkanNativeCommandBufferRecordingApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanCommandResourcesApi>(implementationFactory: static _ => new VulkanNativeCommandResourcesApi());
        services.TryAddSingleton<IVulkanComputePipelineApi>(implementationFactory: static sp => new VulkanNativeComputePipelineApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanDescriptorApi>(implementationFactory: static _ => new VulkanNativeDescriptorApi());
        services.TryAddSingleton<IVulkanExternalMemoryApi>(implementationFactory: static _ => new VulkanNativeExternalMemoryApi());
        services.TryAddSingleton<IVulkanFramebufferSetApi>(implementationFactory: static sp => new VulkanNativeFramebufferSetApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanFrameReadbackApi>(implementationFactory: static _ => new VulkanNativeFrameReadbackApi());
        services.TryAddSingleton<IVulkanFramePresentationApi>(implementationFactory: static sp => new VulkanNativeFramePresentationApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanFrameSynchronizationApi>(implementationFactory: static _ => new VulkanNativeFrameSynchronizationApi());
        services.TryAddSingleton<IVulkanGraphicsPipelineApi>(implementationFactory: static sp => new VulkanNativeGraphicsPipelineApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanInstanceApi>(implementationFactory: static sp => new VulkanNativeInstanceApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanQueryPoolApi>(implementationFactory: static _ => new VulkanNativeQueryPoolApi());
        services.TryAddSingleton<IVulkanLogicalDeviceApi>(implementationFactory: static sp => new VulkanNativeLogicalDeviceApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanOffscreenImageApi>(implementationFactory: static _ => new VulkanNativeOffscreenImageApi());
        services.TryAddSingleton<IVulkanPhysicalDeviceApi>(implementationFactory: static sp => new VulkanNativePhysicalDeviceApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanRenderPassApi>(implementationFactory: static sp => new VulkanNativeRenderPassApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanShaderModuleApi>(implementationFactory: static _ => new VulkanNativeShaderModuleApi());
        services.TryAddSingleton<IVulkanStorageBufferApi>(implementationFactory: static _ => new VulkanNativeStorageBufferApi());
        services.TryAddSingleton<IVulkanSurfaceApi>(implementationFactory: static _ => new VulkanNativeSurfaceApi());
        services.TryAddSingleton<IVulkanSwapchainApi>(implementationFactory: static sp => new VulkanNativeSwapchainApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanVertexBufferApi>(implementationFactory: static _ => new VulkanNativeVertexBufferApi());

        return services;
    }

    /// <summary>Registers the factories, command-buffer recorder, asset source, and shader loader the
    /// renderer and compositor compose over the native APIs.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddVulkanFactories(this IServiceCollection services) {
        services.TryAddSingleton<IVulkanInstanceFactory>(implementationFactory: static sp => new VulkanInstanceFactory(instanceApi: sp.GetRequiredService<IVulkanInstanceApi>()));
        services.TryAddSingleton<IVulkanSurfaceFactory>(implementationFactory: static sp => new VulkanSurfaceFactory(surfaceApi: sp.GetRequiredService<IVulkanSurfaceApi>()));
        services.TryAddSingleton<IVulkanPhysicalDeviceSelector>(implementationFactory: static sp => new VulkanPhysicalDeviceSelector(physicalDeviceApi: sp.GetRequiredService<IVulkanPhysicalDeviceApi>()));
        services.TryAddSingleton<IVulkanLogicalDeviceFactory>(implementationFactory: static sp =>
            new VulkanLogicalDeviceFactory(
                logicalDeviceApi: sp.GetRequiredService<IVulkanLogicalDeviceApi>(),
                physicalDeviceApi: sp.GetRequiredService<IVulkanPhysicalDeviceApi>()
            ));
        services.TryAddSingleton<IVulkanSwapchainSupportApi>(implementationFactory: static sp => new VulkanSwapchainSupportApi(physicalDeviceApi: sp.GetRequiredService<IVulkanPhysicalDeviceApi>()));
        services.TryAddSingleton<IVulkanSwapchainFactory>(implementationFactory: static sp => new VulkanSwapchainFactory(swapchainApi: sp.GetRequiredService<IVulkanSwapchainApi>()));
        services.TryAddSingleton<IVulkanRenderPassFactory>(implementationFactory: static sp => new VulkanRenderPassFactory(renderPassApi: sp.GetRequiredService<IVulkanRenderPassApi>()));
        services.TryAddSingleton<IVulkanFramebufferSetFactory>(implementationFactory: static sp => new VulkanFramebufferSetFactory(framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>()));
        services.TryAddSingleton<IVulkanCommandResourcesFactory>(implementationFactory: static sp => new VulkanCommandResourcesFactory(sp.GetRequiredService<IVulkanCommandResourcesApi>(), sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanFrameSynchronizationFactory>(implementationFactory: static sp => new VulkanFrameSynchronizationFactory(frameSynchronizationApi: sp.GetRequiredService<IVulkanFrameSynchronizationApi>()));
        services.TryAddSingleton<IVulkanFramePresenter>(implementationFactory: static sp =>
            new VulkanFramePresenter(
                framePresentationApi: sp.GetRequiredService<IVulkanFramePresentationApi>(),
                frameSynchronizationApi: sp.GetRequiredService<IVulkanFrameSynchronizationApi>()
            ));
        services.TryAddSingleton<IVulkanGraphicsPipelineFactory>(implementationFactory: static sp => new VulkanGraphicsPipelineFactory(graphicsPipelineApi: sp.GetRequiredService<IVulkanGraphicsPipelineApi>()));
        services.TryAddSingleton<IVulkanShaderModuleFactory>(implementationFactory: static sp => new VulkanShaderModuleFactory(shaderModuleApi: sp.GetRequiredService<IVulkanShaderModuleApi>()));
        services.TryAddSingleton<IVulkanStorageBufferFactory>(implementationFactory: static sp => new VulkanStorageBufferFactory(storageBufferApi: sp.GetRequiredService<IVulkanStorageBufferApi>()));
        services.TryAddSingleton<IVulkanVertexBufferFactory>(implementationFactory: static sp => new VulkanVertexBufferFactory(vertexBufferApi: sp.GetRequiredService<IVulkanVertexBufferApi>()));

        // The renderer's command-buffer recorder, the content-addressed asset source, and the shader loader.
        services.TryAddSingleton<IVulkanCommandBufferRecorder>(implementationFactory: static sp => new VulkanCommandBufferRecorder(commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>()));
        services.TryAddSingleton<IAssetSource, FileSystemAssetSource>();
        services.TryAddSingleton<IShaderModuleLoader, ShaderModuleLoader>();
        services.TryAddSingleton(implementationFactory: static sp => new VulkanDescriptorAllocator(descriptorApi: sp.GetRequiredService<IVulkanDescriptorApi>()));

        return services;
    }
}
