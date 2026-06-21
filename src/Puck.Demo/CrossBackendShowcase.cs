using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Vulkan.Interfaces;

namespace Puck.Demo;

/// <summary>
/// Builds the root render node for the cross-backend showcase. The Vulkan-hosted window presents an
/// <see cref="SdfProducerNode"/> that runs the SDF engine on a chosen backend: a Direct3D 12 producer renders
/// into a texture in shared GPU memory on the same adapter (LUID-matched) and hands Vulkan only the shared NT
/// handle (zero-copy import), or a Vulkan producer renders same-device. Either way the producer is the identical
/// neutral node; only the injected backend services differ — including the GPU readback used for capture, which
/// is therefore the exact same capability on both backends. Isolated from <c>Program</c> for loose coupling.
/// </summary>
internal static class CrossBackendShowcase {
    private const uint RenderSize = 512;

    /// <summary>Gets the directory the compiled SDF shaders are deployed to next to the demo.</summary>
    internal static string ShaderDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Shaders",
        "Sdf"
    );

    /// <summary>Gets the directory the demo's compiled overlay shaders are deployed to next to the demo.</summary>
    internal static string OverlayShaderDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Shaders",
        "Overlay"
    );

    /// <summary>Creates the root node when Vulkan hosts on Windows; otherwise a blank node (the producer needs a
    /// live Vulkan device — for the LUID match or as the same-device renderer — which only exists once Vulkan is
    /// up).</summary>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <param name="startWithDirectX">Whether the host starts with the Direct3D 12 backend.</param>
    /// <param name="produceBackend">Which backend renders the SDF: <c>directx</c> (default, zero-copy cross-backend) or <c>vulkan</c> (same-device).</param>
    /// <param name="capturePath">An optional PNG path the producer writes its first rendered frame to (a GPU readback), or <see langword="null"/>.</param>
    /// <returns>The root <see cref="IRenderNode"/>.</returns>
    public static IRenderNode CreateRootNode(IServiceProvider serviceProvider, bool startWithDirectX, string? produceBackend = null, string? capturePath = null) {
        if (startWithDirectX) {
            // The showcase producer renders on a chosen backend and LUID-matches/imports into a LIVE Vulkan host; with
            // a Direct3D 12 host there is no such Vulkan device, so the node is blank. Log it rather than silently
            // presenting an empty window (e.g. a `showcase` graph under host.backend:"directx").
            Console.Error.WriteLine(value: "[showcase] the cross-backend SDF showcase requires a Vulkan host; a Direct3D 12 host yields a blank window. Use a Vulkan host, or the 'world' graph for a Direct3D 12-hosted run.");

            return new NullRenderNode();
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return new NullRenderNode();
        }

        // The Vulkan same-device producer is the proven texture-sampling path, so the per-controller cursor
        // overlay rides it. The DirectX producer stays the zero-copy SDF showcase (no cursor overlay).
        return string.Equals(produceBackend, "vulkan", StringComparison.OrdinalIgnoreCase)
            ? CreateVulkanCursorShowcase(serviceProvider: serviceProvider, shaderDirectory: ShaderDirectory, overlayShaderDirectory: OverlayShaderDirectory, capturePath: capturePath)
            : CreateDirectXProducer(serviceProvider: serviceProvider, shaderDirectory: ShaderDirectory, capturePath: capturePath);
    }

    // Direct3D 12 renders into an exportable shared texture on the Vulkan adapter; Vulkan imports it zero-copy.
    // Internal so the parity harness can build a headless DirectX producer directly.
    [SupportedOSPlatform("windows10.0.10240")]
    internal static SdfProducerNode CreateDirectXProducer(IServiceProvider serviceProvider, string shaderDirectory, string? capturePath) {
        var vulkanContext = (IVulkanDeviceContext)serviceProvider.GetService(serviceType: typeof(IVulkanDeviceContext))!;
        var physicalDeviceApi = (IVulkanPhysicalDeviceApi)serviceProvider.GetService(serviceType: typeof(IVulkanPhysicalDeviceApi))!;
        var shaderLoader = (IShaderModuleLoader)serviceProvider.GetService(serviceType: typeof(IShaderModuleLoader))!;
        var deviceContext = new DirectXDeviceContext(
            adapterLuidProvider: () => physicalDeviceApi.GetDeviceLuid(
                instanceHandle: vulkanContext.Instance.Handle,
                physicalDeviceHandle: vulkanContext.PhysicalDevice.Handle
            ),
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        );
        var services = new SdfProducerServices {
            CommandRecorder = new DirectXGpuCommandRecorder(),
            CreateRenderTarget = () => new DirectXGpuSurfaceExportFactory().CreateExportableTarget(deviceContext: deviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: RenderSize, width: RenderSize),
            DescriptorAllocator = new DirectXGpuDescriptorAllocator(),
            DeviceContext = deviceContext,
            OwnsDeviceContext = true,
            PipelineFactory = new DirectXGpuPipelineFactory(),
            QueueSubmitter = new DirectXGpuQueueSubmitter(),
            ShaderModuleFactory = new DirectXGpuShaderModuleFactory(),
            StorageBufferBinding = 0, // Direct3D 12 descriptor-table slot (storage SRV at t0, textureSamplerCount 0).
            StorageBufferFactory = new DirectXGpuStorageBufferFactory(),
            SurfaceTransferFactory = new DirectXGpuSurfaceTransferFactory(),
            VertexBufferFactory = new DirectXGpuVertexBufferFactory(vertexBufferFactory: new Puck.DirectX.Factories.DirectXVertexBufferFactory(vertexBufferApi: new DirectXNativeVertexBufferApi())),
        };

        return new SdfProducerNode(
            capturePath: capturePath,
            fragmentBytecode: LoadShader(directory: shaderDirectory, fileName: "sdf-view.frag.dxil", loader: shaderLoader, stage: ShaderStage.Fragment),
            height: RenderSize,
            services: services,
            vertexBytecode: LoadShader(directory: shaderDirectory, fileName: "fullscreen.vert.dxil", loader: shaderLoader, stage: ShaderStage.Vertex),
            width: RenderSize
        );
    }

    // Vulkan renders the SDF same-device (the host's device) and presents it directly — no sharing.
    // Internal so the parity harness can build a plain offscreen Vulkan producer directly.
    internal static SdfProducerNode CreateVulkanProducer(IServiceProvider serviceProvider, string shaderDirectory, string? capturePath) {
        return CreateVulkanProducer(
            capturePath: capturePath,
            services: BuildVulkanServices(serviceProvider: serviceProvider),
            shaderDirectory: shaderDirectory,
            shaderLoader: (IShaderModuleLoader)serviceProvider.GetService(serviceType: typeof(IShaderModuleLoader))!,
            submitAndWait: false
        );
    }

    // Vulkan SDF producer with each controller's cursor drawn over it: the producer renders into a sampleable
    // target (blocking on submit so it's complete), then CursorOverlayNode samples that and blends the cursors.
    private static IRenderNode CreateVulkanCursorShowcase(IServiceProvider serviceProvider, string shaderDirectory, string overlayShaderDirectory, string? capturePath) {
        var services = BuildVulkanServices(serviceProvider: serviceProvider);
        var shaderLoader = (IShaderModuleLoader)serviceProvider.GetService(serviceType: typeof(IShaderModuleLoader))!;
        var inner = CreateVulkanProducer(
            capturePath: capturePath,
            services: services,
            shaderDirectory: shaderDirectory,
            shaderLoader: shaderLoader,
            submitAndWait: true
        );
        var fullscreenVertex = LoadShader(directory: shaderDirectory, fileName: "fullscreen.vert.spv", loader: shaderLoader, stage: ShaderStage.Vertex);

        // The overlay reuses the producer's neutral services (same device): its output target is another plain
        // Vulkan target (both presentable and the inner one sampleable), and its color comes from the cursor store.
        return new CursorOverlayNode(
            commandRecorder: services.CommandRecorder,
            createRenderTarget: services.CreateRenderTarget,
            cursors: (CursorStore)serviceProvider.GetService(serviceType: typeof(CursorStore))!,
            descriptorAllocator: services.DescriptorAllocator,
            deviceContext: services.DeviceContext,
            fragmentBytecode: LoadShader(directory: overlayShaderDirectory, fileName: "cursor-overlay.frag.spv", loader: shaderLoader, stage: ShaderStage.Fragment),
            height: RenderSize,
            inner: inner,
            pipelineFactory: services.PipelineFactory,
            queueSubmitter: services.QueueSubmitter,
            shaderModuleFactory: services.ShaderModuleFactory,
            vertexBufferFactory: services.VertexBufferFactory,
            vertexBytecode: fullscreenVertex,
            width: RenderSize
        );
    }

    private static SdfProducerNode CreateVulkanProducer(SdfProducerServices services, string shaderDirectory, IShaderModuleLoader shaderLoader, string? capturePath, bool submitAndWait) {
        return new SdfProducerNode(
            capturePath: capturePath,
            fragmentBytecode: LoadShader(directory: shaderDirectory, fileName: "sdf-view.frag.spv", loader: shaderLoader, stage: ShaderStage.Fragment),
            height: RenderSize,
            services: services,
            submitAndWait: submitAndWait,
            vertexBytecode: LoadShader(directory: shaderDirectory, fileName: "fullscreen.vert.spv", loader: shaderLoader, stage: ShaderStage.Vertex),
            width: RenderSize
        );
    }

    private static SdfProducerServices BuildVulkanServices(IServiceProvider serviceProvider) {
        T Resolve<T>() => (T)serviceProvider.GetService(serviceType: typeof(T))!;

        // The Vulkan device context is published as IVulkanDeviceContext (the VulkanRenderer, which also
        // implements IGpuDeviceContext); IGpuDeviceContext itself resolves to the DirectX device context.
        var deviceContext = (IGpuDeviceContext)Resolve<IVulkanDeviceContext>();
        var renderTargetFactory = Resolve<IGpuRenderTargetFactory>();

        return new SdfProducerServices {
            CommandRecorder = Resolve<IGpuCommandRecorder>(),
            CreateRenderTarget = () => renderTargetFactory.Create(deviceContext: deviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: RenderSize, width: RenderSize),
            DescriptorAllocator = Resolve<IGpuDescriptorAllocator>(),
            DeviceContext = deviceContext,
            OwnsDeviceContext = false,
            PipelineFactory = Resolve<IGpuPipelineFactory>(),
            QueueSubmitter = Resolve<IGpuQueueSubmitter>(),
            ShaderModuleFactory = Resolve<IGpuShaderModuleFactory>(),
            StorageBufferBinding = 1, // Vulkan descriptor binding 1 (binding 0 is the always-present sampler slot).
            StorageBufferFactory = Resolve<IGpuStorageBufferFactory>(),
            SurfaceTransferFactory = Resolve<IGpuSurfaceTransferFactory>(),
            VertexBufferFactory = Resolve<IGpuVertexBufferFactory>(),
        };
    }

    // Routes the demo's compiled shader bytecode through IShaderModuleLoader so it is validated (SPIR-V or
    // DXBC/DXIL magic + declared size) and content-hash cached, instead of an unchecked File.ReadAllBytes.
    private static ReadOnlyMemory<byte> LoadShader(IShaderModuleLoader loader, string directory, string fileName, ShaderStage stage) {
        return loader
            .ValidateShader(stage: stage, path: Path.Combine(directory, fileName))
            .Content;
    }
}
