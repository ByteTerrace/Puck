using Puck.Assets;
using Puck.Compositing;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Launcher.Vulkan;

/// <summary>
/// The terminal's compositor: it blits the one surface the root node produces, fullscreen, onto the
/// swapchain — the dumb terminal's whole job once the engine owns world rendering. A fullscreen triangle
/// samples the surface texture 1:1 through the existing <see cref="VulkanRenderer.Present"/> path. The
/// blit pipeline and descriptor set rebuild whenever presentation resources are recreated (resize), and the
/// device-level resources (shaders, vertex buffer, sampler) rebuild too if that recreation ever comes with a
/// changed device; each resource is freed on the device that created it.
/// </summary>
internal sealed class SurfaceCompositor : IDisposable {
    private const string BlitFragmentShaderFileName = "blit.frag.spv";
    private const uint SamplerBindingIndex = 0;
    private const string VertexShaderFileName = "fullscreen.vert.spv";

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly VulkanDescriptorAllocator m_descriptorAllocator;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly IVulkanGraphicsPipelineFactory m_graphicsPipelineFactory;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private readonly VulkanRenderer m_renderer;
    private readonly string m_shaderDirectory;
    private readonly IShaderModuleLoader m_shaderModuleLoader;
    private readonly IVulkanShaderModuleFactory m_shaderModuleFactory;
    private readonly IVulkanStorageBufferFactory m_storageBufferFactory;
    private readonly IVulkanVertexBufferFactory m_vertexBufferFactory;
    private VulkanSurfaceUpload? m_rootUpload;
    private VulkanShaderModule? m_blitFragmentShader;
    private VulkanGraphicsPipeline? m_blitPipeline;
    private AssetContentHash m_blitPipelineId;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private bool m_initialized;
    private nint m_lastWrittenImageView;
    private VulkanLogicalDevice? m_resourceDevice;
    private nint m_sampler;
    private VulkanShaderModule? m_vertexShader;
    private VulkanVertexBuffer? m_vertexBuffer;

    public SurfaceCompositor(
        VulkanRenderer renderer,
        string shaderDirectory,
        IShaderModuleLoader shaderModuleLoader,
        IVulkanShaderModuleFactory shaderModuleFactory,
        IVulkanGraphicsPipelineFactory graphicsPipelineFactory,
        IVulkanVertexBufferFactory vertexBufferFactory,
        IVulkanDescriptorApi descriptorApi,
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanStorageBufferFactory storageBufferFactory,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(descriptorApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(graphicsPipelineFactory);
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(shaderDirectory);
        ArgumentNullException.ThrowIfNull(shaderModuleFactory);
        ArgumentNullException.ThrowIfNull(shaderModuleLoader);
        ArgumentNullException.ThrowIfNull(storageBufferFactory);
        ArgumentNullException.ThrowIfNull(vertexBufferFactory);

        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_descriptorAllocator = new VulkanDescriptorAllocator(descriptorApi: descriptorApi);
        m_framebufferSetApi = framebufferSetApi;
        m_graphicsPipelineFactory = graphicsPipelineFactory;
        m_offscreenImageApi = offscreenImageApi;
        m_queueSubmitter = queueSubmitter;
        m_renderer = renderer;
        m_shaderDirectory = shaderDirectory;
        m_shaderModuleFactory = shaderModuleFactory;
        m_shaderModuleLoader = shaderModuleLoader;
        m_storageBufferFactory = storageBufferFactory;
        m_vertexBufferFactory = vertexBufferFactory;
    }

    /// <summary>Builds the device-level resources (shaders, vertex buffer, sampler) and subscribes to the
    /// renderer's presentation-recreated signal. The renderer must already be initialized.</summary>
    public void Initialize() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var device = m_renderer.Device;

        CreateDeviceResources(device: device);
        m_resourceDevice = device;

        m_renderer.PresentationResourcesRecreated += OnPresentationResourcesRecreated;
        m_initialized = true;
    }

    /// <summary>Blits the surface fullscreen onto the swapchain. A no-op until presentation resources
    /// exist or when the surface is empty (a skipped frame).</summary>
    public void Blit(Surface surface) {
        if (
            m_disposed ||
            (m_blitPipeline is null) ||
            (0 == m_descriptorSet) ||
            surface.IsEmpty
        ) {
            return;
        }

        // A same-device root hands over its image-view directly; a root whose output crossed a device boundary
        // (a DirectX host root) arrives as CPU pixels, so upload it onto this device first, then blit.
        var imageViewHandle = surface.ImageViewHandle;

        if (surface.IsCpuPixels) {
            m_rootUpload ??= new VulkanSurfaceUpload(
                commandBufferRecordingApi: m_commandBufferRecordingApi,
                commandResourcesFactory: m_commandResourcesFactory,
                framebufferSetApi: m_framebufferSetApi,
                offscreenImageApi: m_offscreenImageApi,
                queueSubmitter: m_queueSubmitter,
                storageBufferFactory: m_storageBufferFactory
            );
            imageViewHandle = m_rootUpload.Upload(
                deviceContext: m_renderer,
                surface: surface
            );
        }

        if (imageViewHandle != m_lastWrittenImageView) {
            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: SamplerBindingIndex,
                descriptorSetHandle: m_descriptorSet,
                deviceHandle: m_renderer.Device.Handle,
                imageViewHandle: imageViewHandle,
                samplerHandle: m_sampler
            );

            m_lastWrittenImageView = imageViewHandle;
        }

        var drawCommands = new VulkanDrawCommand[]
        {
            new(
                DescriptorSetHandle: m_descriptorSet,
                DrawParameters: new VulkanDrawParameters(
                    firstInstance: 0,
                    firstVertex: 0,
                    instanceCount: 1,
                    vertexCount: 3
                ),
                PipelineId: m_blitPipelineId,
                VertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: m_vertexBuffer!.BufferHandle)
            ),
        };
        var graphicsPipelines = new Dictionary<AssetContentHash, VulkanGraphicsPipeline> {
            [m_blitPipelineId] = m_blitPipeline,
        };

        m_renderer.Present(
            drawCommands: drawCommands,
            graphicsPipelines: graphicsPipelines
        );
    }

    private void OnPresentationResourcesRecreated() {
        var device = m_renderer.Device;
        var resourceDevice = (m_resourceDevice ?? device);

        DisposeFrameResources(device: resourceDevice);

        if (resourceDevice.Handle != device.Handle) {
            DisposeDeviceResources(device: resourceDevice);
            CreateDeviceResources(device: device);
        }

        m_resourceDevice = device;
        m_blitPipeline = m_graphicsPipelineFactory.Create(
            enableStorageBuffer: false,
            fragmentShaderModule: m_blitFragmentShader!,
            logicalDevice: device,
            pushConstantBinding: null,
            renderPass: m_renderer.RenderPass,
            swapchain: m_renderer.Swapchain,
            textureSamplerCount: 1,
            vertexShaderModule: m_vertexShader!
        );
        m_descriptorPool = m_descriptorAllocator.CreatePool(
            deviceHandle: device.Handle,
            maxCombinedImageSamplers: 1,
            maxStorageBuffers: 0
        );
        m_descriptorSet = m_descriptorAllocator.AllocateSet(
            deviceHandle: device.Handle,
            descriptorSetLayoutHandle: m_blitPipeline.DescriptorSetLayoutHandle,
            poolHandle: m_descriptorPool
        );
        m_lastWrittenImageView = 0;
    }
    private void CreateDeviceResources(VulkanLogicalDevice device) {
        var vertexShaderInfo = ValidateShader(
            fileName: VertexShaderFileName,
            stage: ShaderStage.Vertex
        );
        var blitFragmentShaderInfo = ValidateShader(
            fileName: BlitFragmentShaderFileName,
            stage: ShaderStage.Fragment
        );

        m_blitPipelineId = blitFragmentShaderInfo.ContentHash;
        m_vertexShader = m_shaderModuleFactory.Create(
            logicalDevice: device,
            stageInfo: vertexShaderInfo
        );
        m_blitFragmentShader = m_shaderModuleFactory.Create(
            logicalDevice: device,
            stageInfo: blitFragmentShaderInfo
        );
        m_vertexBuffer = m_vertexBufferFactory.Create(
            logicalDevice: device,
            vertexData: FullscreenTriangleVertexData,
            vulkanInstance: m_renderer.Instance
        );
        m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: device.Handle);
    }
    private ShaderStageInfo ValidateShader(string fileName, ShaderStage stage) {
        return m_shaderModuleLoader.ValidateShader(
            path: Path.Combine(
                path1: m_shaderDirectory,
                path2: fileName
            ),
            stage: stage
        );
    }
    private void DisposeFrameResources(VulkanLogicalDevice device) {
        if (0 != m_descriptorPool) {
            m_descriptorAllocator.DestroyPool(
                deviceHandle: device.Handle,
                poolHandle: m_descriptorPool
            );
            m_descriptorPool = 0;
            m_descriptorSet = 0;
        }

        m_blitPipeline?.Dispose();
        m_blitPipeline = null;
    }
    private void DisposeDeviceResources(VulkanLogicalDevice device) {
        if (0 != m_sampler) {
            m_descriptorAllocator.DestroySampler(
                deviceHandle: device.Handle,
                samplerHandle: m_sampler
            );
            m_sampler = 0;
        }

        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_blitFragmentShader?.Dispose();
        m_blitFragmentShader = null;
        m_vertexShader?.Dispose();
        m_vertexShader = null;
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

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_renderer.PresentationResourcesRecreated -= OnPresentationResourcesRecreated;
        m_rootUpload?.Dispose();

        if (!m_initialized) {
            return;
        }

        var device = (m_resourceDevice ?? m_renderer.Device);

        device.WaitIdle();
        DisposeFrameResources(device: device);
        DisposeDeviceResources(device: device);
    }
}
