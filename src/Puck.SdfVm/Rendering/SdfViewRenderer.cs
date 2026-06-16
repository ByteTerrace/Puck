using Puck.Cameras;
using Puck.Compositing;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.SdfVm.Rendering;

internal sealed class SdfViewRenderer : IDisposable {
    internal const int ViewCount = 4;

    private const int CompositePushConstantByteLength = ((sizeof(float) * 4) * 6);
    private const uint CompositeSamplerBindingIndex = 0;
    private const uint OutputFormat = VulkanFormat.B8G8R8A8Unorm;
    private const uint ProgramBindingIndex = 1;
    private const int SdfPushConstantByteLength = ((sizeof(float) * 4) * 5);
    private const ulong StorageBufferByteLength = (256UL * 1024UL);

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();
    private readonly VulkanSurfaceImport?[] m_childImports = new VulkanSurfaceImport?[ViewCount];
    private readonly VulkanSurfaceUpload?[] m_childUploads = new VulkanSurfaceUpload?[ViewCount];
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly nint[] m_commandBufferScratch = new nint[(ViewCount + 1)];
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly VulkanDescriptorAllocator m_descriptorAllocator;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly IVulkanGraphicsPipelineFactory m_graphicsPipelineFactory;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private readonly SdfViewRendererOptions m_options;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private readonly IVulkanRenderPassApi m_renderPassApi;
    private readonly IVulkanShaderModuleFactory m_shaderModuleFactory;
    private readonly IShaderModuleLoader m_shaderModuleLoader;
    private readonly IVulkanStorageBufferFactory m_storageBufferFactory;
    private readonly IVulkanVertexBufferFactory m_vertexBufferFactory;
    private readonly VulkanViewTarget?[] m_viewTargets = new VulkanViewTarget?[ViewCount];
    private VulkanShaderModule? m_compositeFragmentShader;
    private nint m_compositeDescriptorPool;
    private nint m_compositeDescriptorSet;
    private VulkanGraphicsPipeline? m_compositePipeline;
    private VulkanLogicalDevice? m_device;
    private bool m_disposed;
    private uint m_height;
    private VulkanInstance? m_instance;
    private VulkanViewTarget? m_outputTarget;
    private VulkanStorageBuffer? m_programBuffer;
    private nint m_sampler;
    private VulkanShaderModule? m_sdfFragmentShader;
    private nint m_sdfDescriptorPool;
    private nint m_sdfDescriptorSet;
    private VulkanGraphicsPipeline? m_sdfPipeline;
    private VulkanShaderModule? m_vertexShader;
    private VulkanVertexBuffer? m_vertexBuffer;
    private readonly VulkanSurfaceReadback? m_readback;
    private uint m_width;

    public SdfViewRenderer(
        SdfViewRendererOptions options,
        IShaderModuleLoader shaderModuleLoader,
        IVulkanShaderModuleFactory shaderModuleFactory,
        IVulkanGraphicsPipelineFactory graphicsPipelineFactory,
        IVulkanVertexBufferFactory vertexBufferFactory,
        IVulkanStorageBufferFactory storageBufferFactory,
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanRenderPassApi renderPassApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        IVulkanFrameReadbackApi frameReadbackApi,
        IVulkanExternalMemoryApi externalMemoryApi,
        VulkanDescriptorAllocator descriptorAllocator,
        VulkanQueueSubmitter queueSubmitter,
        bool produceCpuPixels
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(shaderModuleLoader);
        ArgumentNullException.ThrowIfNull(shaderModuleFactory);
        ArgumentNullException.ThrowIfNull(graphicsPipelineFactory);
        ArgumentNullException.ThrowIfNull(vertexBufferFactory);
        ArgumentNullException.ThrowIfNull(storageBufferFactory);
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(renderPassApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(frameReadbackApi);
        ArgumentNullException.ThrowIfNull(descriptorAllocator);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_descriptorAllocator = descriptorAllocator;
        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_graphicsPipelineFactory = graphicsPipelineFactory;
        m_offscreenImageApi = offscreenImageApi;
        m_options = options;
        m_queueSubmitter = queueSubmitter;
        m_renderPassApi = renderPassApi;
        m_shaderModuleFactory = shaderModuleFactory;
        m_shaderModuleLoader = shaderModuleLoader;
        m_storageBufferFactory = storageBufferFactory;
        m_vertexBufferFactory = vertexBufferFactory;
        // When this engine is a cross-backend child, its composited output must leave the device as host
        // memory rather than a device handle; the readback turns the output image into a CPU-pixel surface.
        m_readback = (produceCpuPixels
            ? new VulkanSurfaceReadback(
                commandBufferRecordingApi: commandBufferRecordingApi,
                commandResourcesFactory: commandResourcesFactory,
                frameReadbackApi: frameReadbackApi,
                queueSubmitter: queueSubmitter
            )
            : null);
    }

    public Surface Render(IVulkanDeviceContext deviceContext, SdfFrame frame, Surface?[] childSurfaces, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(childSurfaces);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        EnsureDeviceResources(deviceContext: deviceContext);
        EnsureExtentResources(
            height: height,
            width: width
        );

        if (frame.ProgramChanged) {
            UploadProgram(program: frame.Program);
        }

        var device = m_device!;
        var recorded = 0;

        for (var index = 0; (index < ViewCount); index++) {
            if (childSurfaces[index] is not null) {
                continue;
            }

            m_commandBufferScratch[recorded++] = RecordViewPass(
                camera: frame.Views[index].Camera,
                time: frame.Time,
                viewTarget: m_viewTargets[index]!
            );
        }

        for (var index = 0; (index < ViewCount); index++) {
            nint imageViewHandle;

            // A child surface that crossed a device boundary arrives either as a shared GPU handle (import it
            // zero-copy) or as CPU pixels (upload it). A same-device child hands over its image-view directly;
            // an empty slot samples the SDF view rendered above.
            if (childSurfaces[index] is { } childSurface) {
                if (childSurface.IsSharedHandle) {
                    imageViewHandle = ImportChildSurface(
                        deviceContext: deviceContext,
                        slot: index,
                        surface: childSurface
                    );
                } else if (childSurface.IsCpuPixels) {
                    imageViewHandle = UploadChildSurface(
                        deviceContext: deviceContext,
                        slot: index,
                        surface: childSurface
                    );
                } else {
                    imageViewHandle = childSurface.ImageViewHandle;
                }
            } else {
                imageViewHandle = m_viewTargets[index]!.ImageViewHandle;
            }

            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: (uint)index,
                binding: CompositeSamplerBindingIndex,
                descriptorSetHandle: m_compositeDescriptorSet,
                deviceHandle: device.Handle,
                imageViewHandle: imageViewHandle,
                samplerHandle: m_sampler
            );
        }

        m_commandBufferScratch[recorded++] = RecordCompositePass(
            frame: frame,
            height: height,
            width: width
        );

        m_queueSubmitter.SubmitAndWait(
            commandBufferHandles: m_commandBufferScratch.AsSpan(
                length: recorded,
                start: 0
            ),
            deviceHandle: device.Handle,
            graphicsQueue: device.GraphicsQueue
        );

        // A cross-backend parent can't sample our device image, so read it back to host memory; otherwise hand
        // over the shader-readable image-view directly (the same-device fast path).
        if (m_readback is not null) {
            return m_readback.Read(
                deviceContext: deviceContext,
                format: SurfaceFormat.B8G8R8A8Unorm,
                height: height,
                sourceImageHandle: m_outputTarget!.ImageHandle,
                width: width
            );
        }

        return new Surface(
            Format: SurfaceFormat.B8G8R8A8Unorm,
            Height: height,
            ImageViewHandle: m_outputTarget!.ImageViewHandle,
            Width: width
        );
    }

    private void EnsureDeviceResources(IVulkanDeviceContext deviceContext) {
        var device = deviceContext.LogicalDevice;

        if (
            (m_device is not null) &&
            (m_device.Handle == device.Handle)
        ) {
            return;
        }

        if (m_device is not null) {
            DisposeDeviceResources();
        }

        var vertexShaderInfo = ValidateShader(
            fileName: m_options.VertexShaderFileName,
            stage: ShaderStage.Vertex
        );
        var sdfFragmentShaderInfo = ValidateShader(
            fileName: m_options.FragmentShaderFileName,
            stage: ShaderStage.Fragment
        );
        var compositeFragmentShaderInfo = ValidateShader(
            fileName: m_options.CompositeFragmentShaderFileName,
            stage: ShaderStage.Fragment
        );

        m_device = device;
        m_instance = deviceContext.Instance;
        m_vertexShader = m_shaderModuleFactory.Create(
            logicalDevice: device,
            stageInfo: vertexShaderInfo
        );
        m_sdfFragmentShader = m_shaderModuleFactory.Create(
            logicalDevice: device,
            stageInfo: sdfFragmentShaderInfo
        );
        m_compositeFragmentShader = m_shaderModuleFactory.Create(
            logicalDevice: device,
            stageInfo: compositeFragmentShaderInfo
        );
        m_vertexBuffer = m_vertexBufferFactory.Create(
            logicalDevice: device,
            vertexData: FullscreenTriangleVertexData,
            vulkanInstance: m_instance
        );
        m_programBuffer = m_storageBufferFactory.Create(
            logicalDevice: device,
            sizeBytes: StorageBufferByteLength,
            vulkanInstance: m_instance
        );
        m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: device.Handle);
    }
    private void EnsureExtentResources(uint width, uint height) {
        if (
            (m_outputTarget is not null) &&
            (m_width == width) &&
            (m_height == height)
        ) {
            return;
        }

        var device = m_device!;

        DisposeExtentResources();

        for (var index = 0; (index < ViewCount); index++) {
            m_viewTargets[index] = CreateTarget(
                height: height,
                width: width
            );
        }

        m_outputTarget = CreateTarget(
            height: height,
            width: width
        );

        m_sdfPipeline = m_graphicsPipelineFactory.Create(
            enableStorageBuffer: true,
            fragmentShaderModule: m_sdfFragmentShader!,
            height: height,
            logicalDevice: device,
            pushConstantBinding: new VulkanPushConstantBinding(
                data: new byte[SdfPushConstantByteLength],
                offset: 0,
                stageFlags: VulkanShaderStageFlags.Fragment
            ),
            renderPass: m_viewTargets[0]!.RenderPass,
            textureSamplerCount: 1,
            vertexShaderModule: m_vertexShader!,
            width: width
        );
        m_sdfDescriptorPool = m_descriptorAllocator.CreatePool(
            deviceHandle: device.Handle,
            maxCombinedImageSamplers: 1,
            maxStorageBuffers: 1
        );
        m_sdfDescriptorSet = m_descriptorAllocator.AllocateSet(
            deviceHandle: device.Handle,
            descriptorSetLayoutHandle: m_sdfPipeline.DescriptorSetLayoutHandle,
            poolHandle: m_sdfDescriptorPool
        );
        m_descriptorAllocator.WriteStorageBuffer(
            binding: ProgramBindingIndex,
            bufferHandle: m_programBuffer!.BufferHandle,
            bufferSize: m_programBuffer.SizeBytes,
            descriptorSetHandle: m_sdfDescriptorSet,
            deviceHandle: device.Handle
        );

        m_compositePipeline = m_graphicsPipelineFactory.Create(
            enableStorageBuffer: false,
            fragmentShaderModule: m_compositeFragmentShader!,
            height: height,
            logicalDevice: device,
            pushConstantBinding: new VulkanPushConstantBinding(
                data: new byte[CompositePushConstantByteLength],
                offset: 0,
                stageFlags: VulkanShaderStageFlags.Fragment
            ),
            renderPass: m_outputTarget.RenderPass,
            textureSamplerCount: ViewCount,
            vertexShaderModule: m_vertexShader!,
            width: width
        );
        m_compositeDescriptorPool = m_descriptorAllocator.CreatePool(
            deviceHandle: device.Handle,
            maxCombinedImageSamplers: ViewCount,
            maxStorageBuffers: 0
        );
        m_compositeDescriptorSet = m_descriptorAllocator.AllocateSet(
            deviceHandle: device.Handle,
            descriptorSetLayoutHandle: m_compositePipeline.DescriptorSetLayoutHandle,
            poolHandle: m_compositeDescriptorPool
        );

        m_height = height;
        m_width = width;
    }
    private VulkanViewTarget CreateTarget(uint width, uint height) {
        return new VulkanViewTarget(
            commandResourcesFactory: m_commandResourcesFactory,
            format: OutputFormat,
            framebufferSetApi: m_framebufferSetApi,
            height: height,
            instance: m_instance!,
            logicalDevice: m_device!,
            offscreenImageApi: m_offscreenImageApi,
            renderPassApi: m_renderPassApi,
            width: width
        );
    }
    private void UploadProgram(SdfProgram program) {
        if (m_programBuffer is null) {
            return;
        }

        var byteLength = ((ulong)program.Words.Length * sizeof(uint));

        if (byteLength > StorageBufferByteLength) {
            throw new InvalidOperationException(message: $"The SDF program ({byteLength} bytes) exceeds the storage-buffer capacity ({StorageBufferByteLength} bytes).");
        }

        m_device!.WaitIdle();
        m_programBuffer.Write<uint>(data: program.Words);
    }
    private nint RecordViewPass(VulkanViewTarget viewTarget, CameraSnapshot camera, float time) {
        var device = m_device!;
        var pipeline = m_sdfPipeline!;
        var pushConstants = BuildCameraPushConstants(
            camera: camera,
            time: time,
            viewportHeight: viewTarget.Height,
            viewportWidth: viewTarget.Width
        );
        var request = new VulkanCommandBufferRecordRequest(
            CommandBufferHandle: viewTarget.CommandBufferHandle,
            DeviceHandle: device.Handle,
            FramebufferHandle: viewTarget.FramebufferHandle,
            GraphicsPipelineHandle: pipeline.Handle,
            Height: viewTarget.Height,
            RenderPassHandle: viewTarget.RenderPass.Handle,
            Width: viewTarget.Width
        );

        m_commandBufferRecordingApi.BeginCommandBuffer(request: request).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.StartRenderPass(request: request);
        m_commandBufferRecordingApi.SetScissor(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            height: viewTarget.Height,
            width: viewTarget.Width,
            x: 0,
            y: 0
        );
        m_commandBufferRecordingApi.BindGraphicsPipeline(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            pipelineHandle: pipeline.Handle
        );
        m_commandBufferRecordingApi.BindVertexBuffer(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            vertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: m_vertexBuffer!.BufferHandle)
        );
        m_commandBufferRecordingApi.PushConstants(
            commandBufferHandle: request.CommandBufferHandle,
            data: pushConstants,
            deviceHandle: device.Handle,
            offset: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle,
            stageFlags: VulkanShaderStageFlags.Fragment
        );
        m_commandBufferRecordingApi.BindDescriptorSet(
            commandBufferHandle: request.CommandBufferHandle,
            descriptorSetHandle: m_sdfDescriptorSet,
            deviceHandle: device.Handle,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        m_commandBufferRecordingApi.Draw(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            firstInstance: 0,
            firstVertex: 0,
            instanceCount: 1,
            vertexCount: 3
        );
        m_commandBufferRecordingApi.EndRenderPass(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");

        return request.CommandBufferHandle;
    }
    private nint RecordCompositePass(SdfFrame frame, uint width, uint height) {
        var device = m_device!;
        var pipeline = m_compositePipeline!;
        var outputTarget = m_outputTarget!;
        var pushConstants = BuildCompositePushConstants(
            frame: frame,
            viewportHeight: height,
            viewportWidth: width
        );
        var request = new VulkanCommandBufferRecordRequest(
            CommandBufferHandle: outputTarget.CommandBufferHandle,
            DeviceHandle: device.Handle,
            FramebufferHandle: outputTarget.FramebufferHandle,
            GraphicsPipelineHandle: pipeline.Handle,
            Height: outputTarget.Height,
            RenderPassHandle: outputTarget.RenderPass.Handle,
            Width: outputTarget.Width
        );

        m_commandBufferRecordingApi.BeginCommandBuffer(request: request).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.StartRenderPass(request: request);
        m_commandBufferRecordingApi.SetScissor(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            height: outputTarget.Height,
            width: outputTarget.Width,
            x: 0,
            y: 0
        );
        m_commandBufferRecordingApi.BindGraphicsPipeline(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            pipelineHandle: pipeline.Handle
        );
        m_commandBufferRecordingApi.BindVertexBuffer(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            vertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: m_vertexBuffer!.BufferHandle)
        );
        m_commandBufferRecordingApi.PushConstants(
            commandBufferHandle: request.CommandBufferHandle,
            data: pushConstants,
            deviceHandle: device.Handle,
            offset: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle,
            stageFlags: VulkanShaderStageFlags.Fragment
        );
        m_commandBufferRecordingApi.BindDescriptorSet(
            commandBufferHandle: request.CommandBufferHandle,
            descriptorSetHandle: m_compositeDescriptorSet,
            deviceHandle: device.Handle,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        m_commandBufferRecordingApi.Draw(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            firstInstance: 0,
            firstVertex: 0,
            instanceCount: 1,
            vertexCount: 3
        );
        m_commandBufferRecordingApi.EndRenderPass(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");

        return request.CommandBufferHandle;
    }
    private ShaderStageInfo ValidateShader(string fileName, ShaderStage stage) {
        return m_shaderModuleLoader.ValidateShader(
            path: Path.Combine(
                path1: m_options.ShaderDirectory,
                path2: fileName
            ),
            stage: stage
        );
    }
    private void DisposeExtentResources() {
        var device = m_device;

        if (
            (device is not null) &&
            (m_sdfDescriptorPool != 0)
        ) {
            m_descriptorAllocator.DestroyPool(
                deviceHandle: device.Handle,
                poolHandle: m_sdfDescriptorPool
            );
        }

        m_sdfDescriptorPool = 0;
        m_sdfDescriptorSet = 0;

        if (
            (device is not null) &&
            (m_compositeDescriptorPool != 0)
        ) {
            m_descriptorAllocator.DestroyPool(
                deviceHandle: device.Handle,
                poolHandle: m_compositeDescriptorPool
            );
        }

        m_compositeDescriptorPool = 0;
        m_compositeDescriptorSet = 0;

        m_sdfPipeline?.Dispose();
        m_sdfPipeline = null;
        m_compositePipeline?.Dispose();
        m_compositePipeline = null;

        for (var index = 0; (index < ViewCount); index++) {
            m_viewTargets[index]?.Dispose();
            m_viewTargets[index] = null;
        }

        m_outputTarget?.Dispose();
        m_outputTarget = null;
    }
    private static byte[] BuildCameraPushConstants(CameraSnapshot camera, float time, uint viewportWidth, uint viewportHeight) {
        var data = new byte[SdfPushConstantByteLength];

        WriteVector4(
            data: data,
            offset: 0,
            w: time,
            x: camera.Position.X,
            y: camera.Position.Y,
            z: camera.Position.Z
        );
        WriteVector4(
            data: data,
            offset: 16,
            w: camera.TanHalfFieldOfView,
            x: camera.Right.X,
            y: camera.Right.Y,
            z: camera.Right.Z
        );
        WriteVector4(
            data: data,
            offset: 32,
            w: camera.AspectRatio,
            x: camera.Up.X,
            y: camera.Up.Y,
            z: camera.Up.Z
        );
        WriteVector4(
            data: data,
            offset: 48,
            w: 0f,
            x: camera.Forward.X,
            y: camera.Forward.Y,
            z: camera.Forward.Z
        );
        WriteVector4(
            data: data,
            offset: 64,
            w: 0f,
            x: viewportWidth,
            y: viewportHeight,
            z: 0f
        );

        return data;
    }
    private static byte[] BuildCompositePushConstants(SdfFrame frame, uint viewportWidth, uint viewportHeight) {
        var data = new byte[CompositePushConstantByteLength];

        for (var index = 0; (index < ViewCount); index++) {
            var region = frame.Views[index].Region;

            WriteVector4(
                data: data,
                offset: (index * 16),
                w: region.Height,
                x: region.X,
                y: region.Y,
                z: region.Width
            );
        }

        WriteVector4(
            data: data,
            offset: 64,
            w: 0f,
            x: ViewCount,
            y: frame.WarpAmount,
            z: frame.Time
        );
        WriteVector4(
            data: data,
            offset: 80,
            w: 0f,
            x: viewportWidth,
            y: viewportHeight,
            z: 0f
        );

        return data;
    }
    private static byte[] CreateFullscreenTriangleVertexData() {
        var vertices = new (float X, float Y)[]
        {
            (-1f, -1f),
            (3f, -1f),
            (-1f, 3f),
        };
        var vertexData = new byte[((sizeof(float) * 2) * vertices.Length)];

        for (var index = 0; (index < vertices.Length); index++) {
            var offset = ((index * sizeof(float)) * 2);

            _ = BitConverter.TryWriteBytes(
                destination: vertexData.AsSpan(
                    length: sizeof(float),
                    start: offset
                ),
                value: vertices[index].X
            );
            _ = BitConverter.TryWriteBytes(
                destination: vertexData.AsSpan(
                    length: sizeof(float),
                    start: (offset + sizeof(float))
                ),
                value: vertices[index].Y
            );
        }

        return vertexData;
    }
    private static void WriteVector4(byte[] data, int offset, float x, float y, float z, float w) {
        _ = BitConverter.TryWriteBytes(
            destination: data.AsSpan(
            start: offset,
            length: sizeof(float)
        ),
            value: x
        );
        _ = BitConverter.TryWriteBytes(
            destination: data.AsSpan(
            start: (offset + 4),
            length: sizeof(float)
        ),
            value: y
        );
        _ = BitConverter.TryWriteBytes(
            destination: data.AsSpan(
            start: (offset + 8),
            length: sizeof(float)
        ),
            value: z
        );
        _ = BitConverter.TryWriteBytes(
            destination: data.AsSpan(
            start: (offset + 12),
            length: sizeof(float)
        ),
            value: w
        );
    }
    private nint UploadChildSurface(int slot, IVulkanDeviceContext deviceContext, Surface surface) {
        var upload = (m_childUploads[slot] ??= new VulkanSurfaceUpload(
            commandBufferRecordingApi: m_commandBufferRecordingApi,
            commandResourcesFactory: m_commandResourcesFactory,
            framebufferSetApi: m_framebufferSetApi,
            offscreenImageApi: m_offscreenImageApi,
            queueSubmitter: m_queueSubmitter,
            storageBufferFactory: m_storageBufferFactory
        ));

        return upload.Upload(
            deviceContext: deviceContext,
            surface: surface
        );
    }
    private nint ImportChildSurface(int slot, IVulkanDeviceContext deviceContext, Surface surface) {
        var import = (m_childImports[slot] ??= new VulkanSurfaceImport(
            commandBufferRecordingApi: m_commandBufferRecordingApi,
            commandResourcesFactory: m_commandResourcesFactory,
            externalMemoryApi: m_externalMemoryApi,
            framebufferSetApi: m_framebufferSetApi,
            queueSubmitter: m_queueSubmitter
        ));

        return import.Import(
            deviceContext: deviceContext,
            surface: surface
        );
    }
    private void DisposeUploads() {
        for (var index = 0; (index < ViewCount); index++) {
            m_childUploads[index]?.Dispose();
            m_childUploads[index] = null;
            m_childImports[index]?.Dispose();
            m_childImports[index] = null;
        }
    }
    private void DisposeDeviceResources() {
        m_device?.WaitIdle();
        DisposeExtentResources();
        DisposeUploads();

        if (
            (m_device is not null) &&
            (m_sampler != 0)
        ) {
            m_descriptorAllocator.DestroySampler(
                deviceHandle: m_device.Handle,
                samplerHandle: m_sampler
            );
        }

        m_sampler = 0;
        m_programBuffer?.Dispose();
        m_programBuffer = null;
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_compositeFragmentShader?.Dispose();
        m_compositeFragmentShader = null;
        m_sdfFragmentShader?.Dispose();
        m_sdfFragmentShader = null;
        m_vertexShader?.Dispose();
        m_vertexShader = null;
        m_device = null;
        m_instance = null;
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        DisposeDeviceResources();
        m_readback?.Dispose();
    }
}
