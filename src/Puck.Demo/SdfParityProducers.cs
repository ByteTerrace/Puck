using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Vulkan.Interfaces;

namespace Puck.Demo;

/// <summary>
/// Builds the neutral <see cref="SdfProducerServices"/> GPU-service bundle for a same-device Vulkan producer, and
/// loads validated shader bytecode. Shared by the demo nodes that drive an <see cref="SdfProducerServices"/>-shaped
/// producer on the Vulkan host — currently the overworld's binding-bar overlay.
/// </summary>
internal static class SdfParityProducers {
    // Internal so other Vulkan-hosted demo nodes (the overworld's binding-bar overlay) can reuse the same neutral
    // service bundle at their own render size.
    internal static SdfProducerServices BuildVulkanServices(IServiceProvider serviceProvider, uint width, uint height) {
        T Resolve<T>() => (T)serviceProvider.GetService(serviceType: typeof(T))!;

        // The Vulkan device context is published as IVulkanDeviceContext (the VulkanRenderer, which also
        // implements IGpuDeviceContext); IGpuDeviceContext itself resolves to the DirectX device context.
        var deviceContext = (IGpuDeviceContext)Resolve<IVulkanDeviceContext>();
        var renderTargetFactory = Resolve<IGpuRenderTargetFactory>();

        return new SdfProducerServices {
            CommandRecorder = Resolve<IGpuCommandRecorder>(),
            CreateRenderTarget = () => renderTargetFactory.Create(deviceContext: deviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: height, width: width),
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
    // Internal so other demo nodes load their overlay bytecode through the same validated path.
    internal static ReadOnlyMemory<byte> LoadShader(IShaderModuleLoader loader, string directory, string fileName, ShaderStage stage) {
        return loader
            .ValidateShader(stage: stage, path: Path.Combine(path1: directory, path2: fileName))
            .Content;
    }
}
