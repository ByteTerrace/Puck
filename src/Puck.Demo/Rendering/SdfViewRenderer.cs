using Puck.Assets;
using Puck.Demo.Cameras;
using Puck.Demo.Compositing;
using Puck.Demo.Scene;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Demo.Rendering;

/// <summary>
/// Renders the scene's viewports through offscreen render targets, then composites them into their
/// split-screen regions — "viewport as data" at full size. Each frame: pass one raymarches the shared
/// SDF program once per viewport (a distinct camera each) into its <see cref="VulkanViewTarget"/>; a
/// single batched submit runs all of them; pass two composites the resulting textures into their current
/// regions with the layout's transition (including the shader-driven Warp ripple). All GPU resources are
/// (re)built on <see cref="VulkanRenderer.PresentationResourcesRecreated"/>.
/// </summary>
internal sealed class SdfViewRenderer(
    SdfViewRendererOptions options,
    VulkanRenderer renderer,
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
    VulkanDescriptorAllocator descriptorAllocator,
    VulkanQueueSubmitter queueSubmitter
) : IDisposable {
    private const int CompositePushConstantByteLength = ((sizeof(float) * 4) * 6);
    private const uint CompositeSamplerBindingIndex = 0;
    private const uint ProgramBindingIndex = 1;
    private const int SdfPushConstantByteLength = ((sizeof(float) * 4) * 5);
    private const ulong StorageBufferByteLength = (256UL * 1024UL);
    private const int ViewCount = SplitLayouts.ViewportCount;

    // A single oversized triangle whose interior covers the whole clip volume.
    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();

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

    private readonly nint[] m_commandBufferScratch = new nint[ViewCount];
    private readonly VulkanViewTarget?[] m_viewTargets = new VulkanViewTarget?[ViewCount];
    private VulkanShaderModule? m_compositeFragmentShader;
    private nint m_compositeDescriptorPool;
    private nint m_compositeDescriptorSet;
    private VulkanGraphicsPipeline? m_compositePipeline;
    private AssetContentHash m_compositePipelineId;
    private bool m_disposed;
    private bool m_initialized;
    private VulkanStorageBuffer? m_programBuffer;
    private nint m_sampler;
    private VulkanShaderModule? m_sdfFragmentShader;
    private nint m_sdfDescriptorPool;
    private nint m_sdfDescriptorSet;
    private VulkanGraphicsPipeline? m_sdfPipeline;
    private AssetContentHash m_sdfPipelineId;
    private VulkanShaderModule? m_vertexShader;
    private VulkanVertexBuffer? m_vertexBuffer;

    /// <summary>Builds the device-level resources (shaders, vertex buffer, program storage buffer, sampler)
    /// and subscribes to the renderer's resize signal. The renderer must already be initialized.</summary>
    public void Initialize() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var device = renderer.Device;
        var vertexShaderInfo = ValidateShader(
            fileName: options.VertexShaderFileName,
            stage: ShaderStage.Vertex
        );
        var sdfFragmentShaderInfo = ValidateShader(
            fileName: options.FragmentShaderFileName,
            stage: ShaderStage.Fragment
        );
        var compositeFragmentShaderInfo = ValidateShader(
            fileName: options.CompositeFragmentShaderFileName,
            stage: ShaderStage.Fragment
        );

        m_vertexShader = shaderModuleFactory.Create(
            logicalDevice: device,
            stageInfo: vertexShaderInfo
        );
        m_sdfFragmentShader = shaderModuleFactory.Create(
            logicalDevice: device,
            stageInfo: sdfFragmentShaderInfo
        );
        m_compositeFragmentShader = shaderModuleFactory.Create(
            logicalDevice: device,
            stageInfo: compositeFragmentShaderInfo
        );
        m_sdfPipelineId = sdfFragmentShaderInfo.ContentHash;
        m_compositePipelineId = compositeFragmentShaderInfo.ContentHash;
        m_vertexBuffer = vertexBufferFactory.Create(
            logicalDevice: device,
            vertexData: FullscreenTriangleVertexData,
            vulkanInstance: renderer.Instance
        );
        m_programBuffer = storageBufferFactory.Create(
            logicalDevice: device,
            sizeBytes: StorageBufferByteLength,
            vulkanInstance: renderer.Instance
        );
        m_sampler = descriptorAllocator.CreateSampler(deviceHandle: device.Handle);

        renderer.PresentationResourcesRecreated += OnPresentationResourcesRecreated;
        m_initialized = true;
    }

    /// <summary>Uploads the shared program to the VM's storage buffer (rare; scene switches). Waits for the
    /// device to go idle before overwriting the buffer the GPU may still be reading.</summary>
    public void UploadProgram(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_programBuffer is null) {
            return;
        }

        var byteLength = ((ulong)program.Words.Length * sizeof(uint));

        if (byteLength > StorageBufferByteLength) {
            throw new InvalidOperationException(message: $"The SDF program ({byteLength} bytes) exceeds the storage-buffer capacity ({StorageBufferByteLength} bytes).");
        }

        renderer.Device.WaitIdle();
        m_programBuffer.Write<uint>(data: program.Words);
    }

    /// <summary>Renders every viewport into its target, then composites them per the scene's layout.</summary>
    public void Render(DemoScene scene) {
        ArgumentNullException.ThrowIfNull(scene);

        if (m_disposed) {
            return;
        }

        // BeginFrame may (re)create presentation resources, which rebuilds both passes via the event.
        renderer.BeginFrame();

        if (
            (m_sdfPipeline is null) ||
            (m_compositePipeline is null) ||
            (m_sdfDescriptorSet == 0) ||
            (m_compositeDescriptorSet == 0) ||
            (m_viewTargets[0] is null)
        ) {
            return;
        }

        var device = renderer.Device;
        var viewportWidth = renderer.ViewportWidth;
        var viewportHeight = renderer.ViewportHeight;

        for (var index = 0; (index < ViewCount); index++) {
            var camera = scene.Viewports[index].Camera.Capture(
                viewportHeight: viewportHeight,
                viewportWidth: viewportWidth
            );

            m_commandBufferScratch[index] = RecordViewPass(
                camera: camera,
                time: scene.Time,
                viewTarget: m_viewTargets[index]!
            );
        }

        // One batched submit + wait runs all the offscreen passes before the composite samples them.
        queueSubmitter.SubmitAndWait(
            commandBufferHandles: m_commandBufferScratch,
            deviceHandle: device.Handle,
            graphicsQueue: device.GraphicsQueue
        );

        CompositeToSwapchain(
            scene: scene,
            viewportHeight: viewportHeight,
            viewportWidth: viewportWidth
        );
    }

    private nint RecordViewPass(VulkanViewTarget viewTarget, CameraSnapshot camera, float time) {
        var device = renderer.Device;
        var sdfPipeline = m_sdfPipeline!;
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
            GraphicsPipelineHandle: sdfPipeline.Handle,
            Height: viewTarget.Height,
            RenderPassHandle: viewTarget.RenderPass.Handle,
            Width: viewTarget.Width
        );

        commandBufferRecordingApi.BeginCommandBuffer(request: request).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        commandBufferRecordingApi.StartRenderPass(request: request);
        commandBufferRecordingApi.SetScissor(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            height: viewTarget.Height,
            width: viewTarget.Width,
            x: 0,
            y: 0
        );
        commandBufferRecordingApi.BindGraphicsPipeline(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            pipelineHandle: sdfPipeline.Handle
        );
        commandBufferRecordingApi.BindVertexBuffer(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            vertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: m_vertexBuffer!.BufferHandle)
        );
        commandBufferRecordingApi.PushConstants(
            commandBufferHandle: request.CommandBufferHandle,
            data: pushConstants,
            deviceHandle: device.Handle,
            offset: 0,
            pipelineLayoutHandle: sdfPipeline.LayoutHandle,
            stageFlags: VulkanShaderStageFlags.Fragment
        );
        commandBufferRecordingApi.BindDescriptorSet(
            commandBufferHandle: request.CommandBufferHandle,
            descriptorSetHandle: m_sdfDescriptorSet,
            deviceHandle: device.Handle,
            pipelineLayoutHandle: sdfPipeline.LayoutHandle
        );
        commandBufferRecordingApi.Draw(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle,
            firstInstance: 0,
            firstVertex: 0,
            instanceCount: 1,
            vertexCount: 3
        );
        commandBufferRecordingApi.EndRenderPass(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle
        );
        commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: request.CommandBufferHandle,
            deviceHandle: device.Handle
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");

        return request.CommandBufferHandle;
    }
    private void CompositeToSwapchain(DemoScene scene, uint viewportWidth, uint viewportHeight) {
        var pushConstants = BuildCompositePushConstants(
            scene: scene,
            viewportHeight: viewportHeight,
            viewportWidth: viewportWidth
        );
        var drawCommands = new VulkanDrawCommand[]
        {
            new(
                DescriptorSetHandle: m_compositeDescriptorSet,
                DrawParameters: new VulkanDrawParameters(
                    firstInstance: 0,
                    firstVertex: 0,
                    instanceCount: 1,
                    vertexCount: 3
                ),
                PipelineId: m_compositePipelineId,
                PushConstantBinding: new VulkanPushConstantBinding(
                    data: pushConstants,
                    offset: 0,
                    stageFlags: VulkanShaderStageFlags.Fragment
                ),
                VertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: m_vertexBuffer!.BufferHandle)
            ),
        };
        var graphicsPipelines = new Dictionary<AssetContentHash, VulkanGraphicsPipeline> {
            [m_compositePipelineId] = m_compositePipeline!,
        };

        renderer.Present(
            drawCommands: drawCommands,
            graphicsPipelines: graphicsPipelines
        );
    }
    private static byte[] BuildCompositePushConstants(DemoScene scene, uint viewportWidth, uint viewportHeight) {
        var data = new byte[CompositePushConstantByteLength];

        for (var index = 0; (index < ViewCount); index++) {
            var region = scene.CurrentRegionFor(viewportIndex: index);

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
            y: scene.WarpAmount,
            z: scene.Time
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
    private void OnPresentationResourcesRecreated() {
        var device = renderer.Device;
        var swapchain = renderer.Swapchain;

        DisposeFrameResources();

        for (var index = 0; (index < ViewCount); index++) {
            m_viewTargets[index] = new VulkanViewTarget(
                commandResourcesFactory: commandResourcesFactory,
                format: swapchain.ImageFormat,
                framebufferSetApi: framebufferSetApi,
                height: swapchain.ImageExtentHeight,
                instance: renderer.Instance,
                logicalDevice: device,
                offscreenImageApi: offscreenImageApi,
                renderPassApi: renderPassApi,
                width: swapchain.ImageExtentWidth
            );
        }

        // Pass one: the SDF raymarch. One pipeline serves every view target (their render passes are
        // compatible — identical format/layout), built against the first.
        m_sdfPipeline = graphicsPipelineFactory.Create(
            enableStorageBuffer: true,
            fragmentShaderModule: m_sdfFragmentShader!,
            logicalDevice: device,
            pushConstantBinding: new VulkanPushConstantBinding(
                data: new byte[SdfPushConstantByteLength],
                offset: 0,
                stageFlags: VulkanShaderStageFlags.Fragment
            ),
            renderPass: m_viewTargets[0]!.RenderPass,
            swapchain: swapchain,
            textureSamplerCount: 1,
            vertexShaderModule: m_vertexShader!
        );
        m_sdfDescriptorPool = descriptorAllocator.CreatePool(
            deviceHandle: device.Handle,
            maxCombinedImageSamplers: 1,
            maxStorageBuffers: 1
        );
        m_sdfDescriptorSet = descriptorAllocator.AllocateSet(
            deviceHandle: device.Handle,
            descriptorSetLayoutHandle: m_sdfPipeline.DescriptorSetLayoutHandle,
            poolHandle: m_sdfDescriptorPool
        );
        descriptorAllocator.WriteStorageBuffer(
            binding: ProgramBindingIndex,
            bufferHandle: m_programBuffer!.BufferHandle,
            bufferSize: m_programBuffer.SizeBytes,
            descriptorSetHandle: m_sdfDescriptorSet,
            deviceHandle: device.Handle
        );

        // Pass two: composite all view textures into their regions on the swapchain's render pass.
        m_compositePipeline = graphicsPipelineFactory.Create(
            enableStorageBuffer: false,
            fragmentShaderModule: m_compositeFragmentShader!,
            logicalDevice: device,
            pushConstantBinding: new VulkanPushConstantBinding(
                data: new byte[CompositePushConstantByteLength],
                offset: 0,
                stageFlags: VulkanShaderStageFlags.Fragment
            ),
            renderPass: renderer.RenderPass,
            swapchain: swapchain,
            textureSamplerCount: ViewCount,
            vertexShaderModule: m_vertexShader!
        );
        m_compositeDescriptorPool = descriptorAllocator.CreatePool(
            deviceHandle: device.Handle,
            maxCombinedImageSamplers: ViewCount,
            maxStorageBuffers: 0
        );
        m_compositeDescriptorSet = descriptorAllocator.AllocateSet(
            deviceHandle: device.Handle,
            descriptorSetLayoutHandle: m_compositePipeline.DescriptorSetLayoutHandle,
            poolHandle: m_compositeDescriptorPool
        );

        for (var index = 0; (index < ViewCount); index++) {
            descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: (uint)index,
                binding: CompositeSamplerBindingIndex,
                descriptorSetHandle: m_compositeDescriptorSet,
                deviceHandle: device.Handle,
                imageViewHandle: m_viewTargets[index]!.ImageViewHandle,
                samplerHandle: m_sampler
            );
        }
    }
    private void DisposeFrameResources() {
        var deviceHandle = renderer.Device.Handle;

        if (m_sdfDescriptorPool != 0) {
            descriptorAllocator.DestroyPool(
                deviceHandle: deviceHandle,
                poolHandle: m_sdfDescriptorPool
            );
            m_sdfDescriptorPool = 0;
            m_sdfDescriptorSet = 0;
        }

        if (m_compositeDescriptorPool != 0) {
            descriptorAllocator.DestroyPool(
                deviceHandle: deviceHandle,
                poolHandle: m_compositeDescriptorPool
            );
            m_compositeDescriptorPool = 0;
            m_compositeDescriptorSet = 0;
        }

        m_sdfPipeline?.Dispose();
        m_sdfPipeline = null;
        m_compositePipeline?.Dispose();
        m_compositePipeline = null;

        for (var index = 0; (index < ViewCount); index++) {
            m_viewTargets[index]?.Dispose();
            m_viewTargets[index] = null;
        }
    }
    private ShaderStageInfo ValidateShader(string fileName, ShaderStage stage) {
        return shaderModuleLoader.ValidateShader(
            path: Path.Combine(
                path1: options.ShaderDirectory,
                path2: fileName
            ),
            stage: stage
        );
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        renderer.PresentationResourcesRecreated -= OnPresentationResourcesRecreated;

        if (!m_initialized) {
            return;
        }

        renderer.Device.WaitIdle();
        DisposeFrameResources();
        descriptorAllocator.DestroySampler(
            deviceHandle: renderer.Device.Handle,
            samplerHandle: m_sampler
        );
        m_programBuffer?.Dispose();
        m_vertexBuffer?.Dispose();
        m_compositeFragmentShader?.Dispose();
        m_sdfFragmentShader?.Dispose();
        m_vertexShader?.Dispose();
    }
}
