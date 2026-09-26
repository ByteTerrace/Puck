using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Vulkan.Apis;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Presentation;

/// <summary>
/// Composes the Vulkan presentation backend: the native Vulkan APIs and factories, the swapchain renderer and
/// its surface-blit compositor, and the <see cref="ISurfacePresenter"/> the host loop drives. It contributes
/// the Vulkan device as an inherited root capability (a <see cref="HostCapabilityContribution"/>) so a host
/// can resolve the device without referencing this backend. Pair it with a host that assembles the root
/// <see cref="IHostContext"/> from the contributions and drives the run loop.
/// </summary>
public static class VulkanPresenterServiceRegistration {
    // The key the backend's GpuPipelineCacheWork and GpuDeviceMemoryWork are registered under, and the name their counts report.
    private const string PipelineCacheBackend = "vulkan";

    // The host's one procedure resolver over the loader, and its resolutions as a counter source, registered once
    // however often the backend is added. Creating the resolver never loads the loader.
    private static void AddProcedures(IServiceCollection services) {
        if (services.Any(predicate: static descriptor => (descriptor.ServiceType == typeof(VulkanProcResolver)))) {
            return;
        }

        var procedures = new VulkanProcResolver();

        services.AddSingleton(implementationInstance: procedures);
        services.AddSingleton<IWorkCounterSource>(implementationInstance: procedures.Work);
    }

    /// <summary>Returns how a Vulkan device context creates its neutral services
    /// (<see cref="IGpuDeviceContext.Services"/>), each bound to the context it is handed and passing through the host's
    /// <see cref="GpuCreationFaults"/> when one is registered. The native APIs are resolved here, once, so the provider
    /// never escapes into the context: the renderer creates its services through it, and so does a headless device
    /// context over a logical device of its own.</summary>
    /// <param name="serviceProvider">The provider holding the native APIs and factories
    /// (<see cref="AddVulkanNativeApis"/>, <see cref="AddVulkanFactories"/>), an <see cref="IAllocator"/>, the
    /// <see cref="VulkanRendererOptions"/> and the <see cref="VulkanQueueSubmitter"/>.</param>
    /// <returns>The function creating a context's services.</returns>
    /// <exception cref="InvalidOperationException">A required service is not registered.</exception>
    public static Func<IVulkanDeviceContext, GpuDeviceServices> DeviceServices(IServiceProvider serviceProvider) {
        var allocator = serviceProvider.GetRequiredService<IAllocator>();
        var bufferApi = serviceProvider.GetRequiredService<IVulkanBufferApi>();
        var commandBufferRecordingApi = serviceProvider.GetRequiredService<IVulkanCommandBufferRecordingApi>();
        var commandResourcesFactory = serviceProvider.GetRequiredService<IVulkanCommandResourcesFactory>();
        var computePipelineApi = serviceProvider.GetRequiredService<IVulkanComputePipelineApi>();
        var creationFaults = serviceProvider.GetService<GpuCreationFaults>();
        var descriptorAllocator = serviceProvider.GetRequiredService<VulkanDescriptorAllocator>();
        var externalMemoryApi = serviceProvider.GetRequiredService<IVulkanExternalMemoryApi>();
        var framebufferSetApi = serviceProvider.GetRequiredService<IVulkanFramebufferSetApi>();
        var frameSynchronizationApi = serviceProvider.GetRequiredService<IVulkanFrameSynchronizationApi>();
        var graphicsPipelineApi = serviceProvider.GetRequiredService<IVulkanGraphicsPipelineApi>();
        var offscreenImageApi = serviceProvider.GetRequiredService<IVulkanOffscreenImageApi>();
        var queueSubmitter = serviceProvider.GetRequiredService<VulkanQueueSubmitter>();
        var renderPassApi = serviceProvider.GetRequiredService<IVulkanRenderPassApi>();
        var shaderModuleFactory = serviceProvider.GetRequiredService<IVulkanShaderModuleFactory>();
        var validation = serviceProvider.GetRequiredService<VulkanRendererOptions>().EnableValidation;

        return deviceContext => {
            var naming = new VulkanGpuObjectNaming(
                deviceContext: deviceContext,
                isEnabled: validation
            );

            return GpuCreationFaults.Wrap(faults: creationFaults, services: new GpuDeviceServices {
                Bindings = new VulkanGpuBindings(
                allocator: descriptorAllocator,
                deviceContext: deviceContext,
                naming: naming
            ),
                BufferFactory = new VulkanGpuBufferFactory(
                bufferApi: bufferApi,
                deviceContext: deviceContext,
                naming: naming
            ),
                CommandPoolFactory = new VulkanGpuCommandPoolFactory(
                commandResourcesFactory: commandResourcesFactory,
                deviceContext: deviceContext,
                naming: naming
            ),
                ImageFactory = new VulkanGpuImageFactory(
                deviceContext: deviceContext,
                framebufferSetApi: framebufferSetApi,
                naming: naming,
                offscreenImageApi: offscreenImageApi
            ),
                Naming = naming,
                PipelineFactory = new VulkanGpuPipelineFactory(
                allocator: allocator,
                computePipelineApi: computePipelineApi,
                deviceContext: deviceContext,
                graphicsPipelineApi: graphicsPipelineApi,
                naming: naming
            ),
                QueueSubmitter = new VulkanGpuQueueSubmitter(
                deviceContext: deviceContext,
                frameSynchronizationApi: frameSynchronizationApi,
                queueSubmitter: queueSubmitter
            ),
                Recorder = new VulkanGpuRecorder(
                deviceContext: deviceContext,
                recordingApi: commandBufferRecordingApi
            ),
                RenderPassFactory = new VulkanGpuRenderPassFactory(
                deviceContext: deviceContext,
                framebufferSetApi: framebufferSetApi,
                naming: naming,
                renderPassApi: renderPassApi
            ),
                ShaderModuleFactory = new VulkanGpuShaderModuleFactory(
                deviceContext: deviceContext,
                shaderModuleFactory: shaderModuleFactory
            ),
                SurfaceTransferFactory = new VulkanGpuSurfaceTransferFactory(
                bufferApi: bufferApi,
                commandBufferRecordingApi: commandBufferRecordingApi,
                commandResourcesFactory: commandResourcesFactory,
                deviceContext: deviceContext,
                externalMemoryApi: externalMemoryApi,
                frameSynchronizationApi: frameSynchronizationApi,
                framebufferSetApi: framebufferSetApi,
                offscreenImageApi: offscreenImageApi,
                queueSubmitter: queueSubmitter
            ),
            });
        };
    }
    /// <summary>Registers the factories, command-buffer recorder, asset source, and shader loader the
    /// renderer and compositor compose over the native APIs, and the backend's <c>pipeline-cache.vulkan</c> and
    /// <c>procedures.vulkan</c> and <c>memory.vulkan</c> <see cref="IWorkCounterSource"/>s.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddVulkanFactories(this IServiceCollection services) {
        services.AddGpuPipelineCacheWork(backend: PipelineCacheBackend);
        services.AddGpuDeviceMemoryWork(backend: PipelineCacheBackend);
        AddProcedures(services: services);
        services.TryAddSingleton<IVulkanInstanceFactory>(implementationFactory: static sp => new VulkanInstanceFactory(instanceApi: sp.GetRequiredService<IVulkanInstanceApi>()));
        services.TryAddSingleton<IVulkanSurfaceFactory>(implementationFactory: static sp => new VulkanSurfaceFactory(surfaceApi: sp.GetRequiredService<IVulkanSurfaceApi>()));
        services.TryAddSingleton<IVulkanPhysicalDeviceSelector>(implementationFactory: static sp => new VulkanPhysicalDeviceSelector(physicalDeviceApi: sp.GetRequiredService<IVulkanPhysicalDeviceApi>()));
        services.TryAddSingleton<IVulkanLogicalDeviceFactory>(implementationFactory: static sp =>
            new VulkanLogicalDeviceFactory(
            logicalDeviceApi: sp.GetRequiredService<IVulkanLogicalDeviceApi>(),
            physicalDeviceApi: sp.GetRequiredService<IVulkanPhysicalDeviceApi>(),
            pipelineCacheStore: sp.GetService<GpuPipelineCacheStore>(),
            pipelineCacheWork: sp.GetRequiredKeyedService<GpuPipelineCacheWork>(serviceKey: PipelineCacheBackend)
        ));
        services.TryAddSingleton<IVulkanSwapchainSupportApi>(implementationFactory: static sp => new VulkanSwapchainSupportApi(physicalDeviceApi: sp.GetRequiredService<IVulkanPhysicalDeviceApi>()));
        services.TryAddSingleton<IVulkanSwapchainFactory>(implementationFactory: static sp => new VulkanSwapchainFactory(swapchainApi: sp.GetRequiredService<IVulkanSwapchainApi>()));
        services.TryAddSingleton<IVulkanRenderPassFactory>(implementationFactory: static sp => new VulkanRenderPassFactory(renderPassApi: sp.GetRequiredService<IVulkanRenderPassApi>()));
        services.TryAddSingleton<IVulkanFramebufferSetFactory>(implementationFactory: static sp => new VulkanFramebufferSetFactory(framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>()));
        services.TryAddSingleton<IVulkanCommandResourcesFactory>(implementationFactory: static sp => new VulkanCommandResourcesFactory(
            sp.GetRequiredService<IVulkanCommandResourcesApi>(),
            sp.GetRequiredService<IAllocator>()
        ));
        services.TryAddSingleton<IVulkanFrameSynchronizationFactory>(implementationFactory: static sp => new VulkanFrameSynchronizationFactory(frameSynchronizationApi: sp.GetRequiredService<IVulkanFrameSynchronizationApi>()));
        services.TryAddSingleton<IVulkanFramePresenter>(implementationFactory: static sp =>
            new VulkanFramePresenter(
            framePresentationApi: sp.GetRequiredService<IVulkanFramePresentationApi>(),
            frameSynchronizationApi: sp.GetRequiredService<IVulkanFrameSynchronizationApi>()
        ));
        services.TryAddSingleton<IVulkanShaderModuleFactory>(implementationFactory: static sp => new VulkanShaderModuleFactory(shaderModuleApi: sp.GetRequiredService<IVulkanShaderModuleApi>()));

        // The renderer's command-buffer recorder, the content-addressed asset source, and the shader loader.
        services.TryAddSingleton<IVulkanCommandBufferRecorder>(implementationFactory: static sp => new VulkanCommandBufferRecorder(commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>()));
        services.TryAddSingleton<IAssetSource, FileSystemAssetSource>();
        services.TryAddSingleton<IShaderModuleLoader, ShaderModuleLoader>();
        services.TryAddSingleton(implementationFactory: static sp => new VulkanDescriptorAllocator(descriptorApi: sp.GetRequiredService<IVulkanDescriptorApi>()));

        return services;
    }
    /// <summary>Registers the full Vulkan host block a launcher selects: the backend
    /// (<see cref="AddVulkanPresenter"/>), the neutral <see cref="IGpuDeviceContext"/> alias (the backend
    /// publishes its device in DI as <see cref="IVulkanDeviceContext"/> only, with the neutral interface riding
    /// a <see cref="HostCapabilityContribution"/>, so backend-neutral consumers need this alias to resolve the
    /// same device), the renderer's <c>presentation.vulkan</c> <see cref="IWorkCounterSource"/>, and the
    /// <c>"vulkan"</c> <see cref="SurfacePresenterDescriptor"/>.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddVulkanHostedPresentation(this IServiceCollection services) {
        services.AddVulkanPresenter();
        services.TryAddSingleton<IGpuDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<VulkanRenderer>());
        // The renderer's presentation counters (presentation.skipped), discovered by any counter readout.
        services.AddSingleton<IWorkCounterSource>(implementationFactory: static sp => sp.GetRequiredService<VulkanRenderer>().Presentation);
        services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
            Name: "vulkan",
            Presenter: sp.GetRequiredService<VulkanSurfacePresenter>()
        ));

        return services;
    }
    /// <summary>Registers one native API per Vulkan capability the renderer, compositor, and engine use.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddVulkanNativeApis(this IServiceCollection services) {
        AddProcedures(services: services);
        services.AddGpuDeviceMemoryWork(backend: PipelineCacheBackend);
        services.TryAddSingleton<IVulkanBufferApi>(implementationFactory: static _ => new VulkanNativeBufferApi());
        services.TryAddSingleton<IVulkanCommandBufferRecordingApi>(implementationFactory: static sp => new VulkanNativeCommandBufferRecordingApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanCommandResourcesApi>(implementationFactory: static _ => new VulkanNativeCommandResourcesApi());
        services.TryAddSingleton<IVulkanComputePipelineApi>(implementationFactory: static sp => new VulkanNativeComputePipelineApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanDescriptorApi>(implementationFactory: static _ => new VulkanNativeDescriptorApi());
        services.TryAddSingleton<IVulkanExternalMemoryApi>(implementationFactory: static _ => new VulkanNativeExternalMemoryApi());
        services.TryAddSingleton<IVulkanFramebufferSetApi>(implementationFactory: static sp => new VulkanNativeFramebufferSetApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanFramePresentationApi>(implementationFactory: static sp => new VulkanNativeFramePresentationApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanFrameSynchronizationApi>(implementationFactory: static _ => new VulkanNativeFrameSynchronizationApi());
        services.TryAddSingleton<IVulkanGraphicsPipelineApi>(implementationFactory: static sp => new VulkanNativeGraphicsPipelineApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanInstanceApi>(implementationFactory: static sp => new VulkanNativeInstanceApi(
            allocator: sp.GetRequiredService<IAllocator>(),
            procedures: sp.GetRequiredService<VulkanProcResolver>()
        ));
        services.TryAddSingleton<IVulkanLogicalDeviceApi>(implementationFactory: static sp => new VulkanNativeLogicalDeviceApi(
            allocator: sp.GetRequiredService<IAllocator>(),
            memory: sp.GetRequiredKeyedService<GpuDeviceMemoryWork>(serviceKey: PipelineCacheBackend),
            procedures: sp.GetRequiredService<VulkanProcResolver>()
        ));
        services.TryAddSingleton<IVulkanOffscreenImageApi>(implementationFactory: static _ => new VulkanNativeOffscreenImageApi());
        services.TryAddSingleton<IVulkanPhysicalDeviceApi>(implementationFactory: static sp => new VulkanNativePhysicalDeviceApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanRenderPassApi>(implementationFactory: static sp => new VulkanNativeRenderPassApi(allocator: sp.GetRequiredService<IAllocator>()));
        services.TryAddSingleton<IVulkanShaderModuleApi>(implementationFactory: static _ => new VulkanNativeShaderModuleApi());
        services.TryAddSingleton<IVulkanSurfaceApi>(implementationFactory: static _ => new VulkanNativeSurfaceApi());
        services.TryAddSingleton<IVulkanSwapchainApi>(implementationFactory: static sp => new VulkanNativeSwapchainApi(allocator: sp.GetRequiredService<IAllocator>()));

        return services;
    }
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
            .AddVulkanFactories();

        // The swapchain renderer, its surface-blit compositor, and the seam the host drives. The application
        // name is a cosmetic Vulkan instance label; it defaults to the running app, validation follows the host's
        // GpuDeviceOptions (off when none is registered), and a consumer may register its own VulkanRendererOptions
        // before calling this to override both.
        services.TryAddSingleton(implementationFactory: static sp => new VulkanRendererOptions {
            ApplicationName = AppDomain.CurrentDomain.FriendlyName,
            EnableValidation = (sp.GetService<GpuDeviceOptions>()?.DebugLayers ?? false),
        });
        // Neutral presentation preferences (present mode + surface format); a consumer may register its own
        // before calling this to override the defaults (Vsync + R8G8B8A8).
        services.TryAddSingleton(instance: new PresentationOptions());
        services.TryAddSingleton(implementationFactory: static sp => new VulkanRenderer(
            commandBufferRecorder: sp.GetRequiredService<IVulkanCommandBufferRecorder>(),
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            createServices: DeviceServices(serviceProvider: sp),
            framebufferSetFactory: sp.GetRequiredService<IVulkanFramebufferSetFactory>(),
            framePresenter: sp.GetRequiredService<IVulkanFramePresenter>(),
            frameSynchronizationFactory: sp.GetRequiredService<IVulkanFrameSynchronizationFactory>(),
            instanceFactory: sp.GetRequiredService<IVulkanInstanceFactory>(),
            logicalDeviceFactory: sp.GetRequiredService<IVulkanLogicalDeviceFactory>(),
            options: sp.GetRequiredService<VulkanRendererOptions>(),
            physicalDeviceApi: sp.GetRequiredService<IVulkanPhysicalDeviceApi>(),
            physicalDeviceSelector: sp.GetRequiredService<IVulkanPhysicalDeviceSelector>(),
            presentationOptions: sp.GetRequiredService<PresentationOptions>(),
            renderPassFactory: sp.GetRequiredService<IVulkanRenderPassFactory>(),
            surfaceFactory: sp.GetRequiredService<IVulkanSurfaceFactory>(),
            swapchainFactory: sp.GetRequiredService<IVulkanSwapchainFactory>(),
            swapchainSupportApi: sp.GetRequiredService<IVulkanSwapchainSupportApi>()
        ));
        // Publish the device context as a public interface so a consumer can compose its own root host
        // context (e.g. adding a DirectX device) without referencing the renderer type.
        services.TryAddSingleton<IVulkanDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<VulkanRenderer>());
        services.TryAddSingleton<VulkanQueueSubmitter>();
        // The composition's pass pipelines, which the blit is an entry of; a host that registers its own shares it.
        services.TryAddSingleton<GpuPassPipelineCache>();
        services.TryAddSingleton(implementationFactory: sp => new SurfaceCompositor(
            bufferApi: sp.GetRequiredService<IVulkanBufferApi>(),
            commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>(),
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            descriptorApi: sp.GetRequiredService<IVulkanDescriptorApi>(),
            externalMemoryApi: sp.GetRequiredService<IVulkanExternalMemoryApi>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>(),
            pipelines: sp.GetRequiredService<GpuPassPipelineCache>(),
            queueSubmitter: sp.GetRequiredService<VulkanQueueSubmitter>(),
            renderer: sp.GetRequiredService<VulkanRenderer>(),
            shaderDirectory: blitShaderDirectory,
            shaderModuleLoader: sp.GetRequiredService<IShaderModuleLoader>()
        ));
        services.TryAddSingleton(implementationFactory: static sp => new VulkanSurfacePresenter(
            compositor: sp.GetRequiredService<SurfaceCompositor>(),
            renderer: sp.GetRequiredService<VulkanRenderer>()
        ));
        services.TryAddSingleton<ISurfacePresenter>(implementationFactory: static sp => sp.GetRequiredService<VulkanSurfacePresenter>());

        // Optional capability: a Vulkan host can export an image in shared device memory (an opaque Win32 NT
        // handle) for ANOTHER Vulkan instance to import zero-copy. A host resolves this when present and falls back
        // to the CPU-pixel transport otherwise. Unlike Direct3D 12's export, an opaque-Vulkan handle is not
        // importable by D3D12 — this is a Vulkan-to-Vulkan capability.
        services.TryAddSingleton<IGpuSurfaceExportFactory>(implementationFactory: static sp => new VulkanGpuSurfaceExportFactory(
            deviceContext: sp.GetRequiredService<IVulkanDeviceContext>(),
            externalMemoryApi: sp.GetRequiredService<IVulkanExternalMemoryApi>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>()
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
}
