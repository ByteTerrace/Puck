using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Shaders;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Presentation;

/// <summary>
/// The Vulkan surface compositor: it blits the one <see cref="Surface"/> a producer hands it, fullscreen,
/// onto the swapchain through a triangle that samples the surface texture 1:1 via the
/// <see cref="VulkanRenderer.Present"/> path. The blit pipeline and descriptor set rebuild whenever
/// presentation resources are recreated (resize), and the device-level resources (shaders, vertex buffer,
/// sampler) rebuild too if that recreation ever comes with a changed device; each resource is freed on the
/// device that created it.
/// </summary>
public sealed class SurfaceCompositor : IDisposable {
    private const string BlitFragmentShaderFileName = "blit.frag.spv";
    // The blit descriptor-set ring depth — matches the renderer's presentation frame ring: a set is rewritten only
    // when the ROOT surface's image view changes, and cycling to the other set on each change means the set being
    // written was last referenced by a blit at least two presents back, which the renderer's frame-slot fence wait
    // (WaitForFrameSlot) has already proven retired. A single set was updated while a pending blit still referenced
    // it (VUID-vkUpdateDescriptorSets-None-03047, caught by the validation layer once the per-frame drain left).
    private const int DescriptorSetRingSize = 2;
    private const uint SamplerBindingIndex = 0;
    private const string VertexShaderFileName = "fullscreen.vert.spv";

    private static readonly byte[] FullscreenTriangleVertexData = FullscreenTriangle.CreateVertexData();

    private readonly IVulkanBufferApi m_bufferApi;
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly VulkanDescriptorAllocator m_descriptorAllocator;
    private readonly nint[] m_descriptorSets = new nint[DescriptorSetRingSize];
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly IVulkanGraphicsPipelineFactory m_graphicsPipelineFactory;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private readonly VulkanRenderer m_renderer;
    private readonly string m_shaderDirectory;
    private readonly IVulkanShaderModuleFactory m_shaderModuleFactory;
    private readonly IShaderModuleLoader m_shaderModuleLoader;

    private VulkanShaderModule? m_blitFragmentShader;
    private VulkanGraphicsPipeline? m_blitPipeline;
    private AssetContentHash m_blitPipelineId;
    private nint m_descriptorPool;
    private int m_descriptorSetIndex;
    private VulkanDrawCommand[][]? m_drawCommandsPerSet;
    private Dictionary<AssetContentHash, VulkanGraphicsPipeline>? m_graphicsPipelines;
    private bool m_initialized;
    private nint m_lastWrittenImageView;
    private VulkanLogicalDevice? m_resourceDevice;
    private VulkanSurfaceUpload? m_rootUpload;
    private nint m_sampler;
    private VulkanSurfaceImport? m_sharedImport;
    private VulkanBuffer? m_vertexBuffer;
    private VulkanShaderModule? m_vertexShader;

    public SurfaceCompositor(
        VulkanRenderer renderer,
        string shaderDirectory,
        IShaderModuleLoader shaderModuleLoader,
        IVulkanShaderModuleFactory shaderModuleFactory,
        IVulkanGraphicsPipelineFactory graphicsPipelineFactory,
        IVulkanBufferApi bufferApi,
        IVulkanDescriptorApi descriptorApi,
        IVulkanExternalMemoryApi externalMemoryApi,
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(bufferApi);
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(descriptorApi);
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(graphicsPipelineFactory);
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(shaderDirectory);
        ArgumentNullException.ThrowIfNull(shaderModuleFactory);
        ArgumentNullException.ThrowIfNull(shaderModuleLoader);

        m_bufferApi = bufferApi;
        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_descriptorAllocator = new VulkanDescriptorAllocator(descriptorApi: descriptorApi);
        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_graphicsPipelineFactory = graphicsPipelineFactory;
        m_offscreenImageApi = offscreenImageApi;
        m_queueSubmitter = queueSubmitter;
        m_renderer = renderer;
        m_shaderDirectory = shaderDirectory;
        m_shaderModuleFactory = shaderModuleFactory;
        m_shaderModuleLoader = shaderModuleLoader;
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
        m_vertexBuffer = VulkanBuffer.Create(
            bufferApi: m_bufferApi,
            device: m_renderer,
            memory: VulkanBufferMemory.HostCoherent,
            sizeBytes: ((ulong)FullscreenTriangleVertexData.Length),
            usage: VulkanBufferUsageFlags.VertexBuffer
        );
        m_vertexBuffer.Write<byte>(data: FullscreenTriangleVertexData);
        m_sampler = m_descriptorAllocator.CreateSampler(request: new VulkanSamplerCreateRequest(
            AddressModeU: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeV: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeW: VulkanSamplerAddressMode.ClampToEdge,
            AnisotropyEnable: 0,
            BorderColor: 0,
            CompareEnable: 0,
            CompareOp: 0,
            Device: device.Commands,
            Flags: 0,
            MagFilter: VulkanFilter.Linear,
            MaxAnisotropy: 0f,
            MaxLod: 0f,
            MinFilter: VulkanFilter.Linear,
            MinLod: 0f,
            MipLodBias: 0f,
            MipmapMode: VulkanSamplerMipmapMode.Linear,
            UnnormalizedCoordinates: 0
        ));
    }
    private void DisposeDeviceResources(VulkanLogicalDevice device) {
        m_descriptorAllocator.DestroySampler(
            device: device.Commands,
            samplerHandle: m_sampler
        );
        m_sampler = 0;
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_blitFragmentShader?.Dispose();
        m_blitFragmentShader = null;
        m_vertexShader?.Dispose();
        m_vertexShader = null;
    }
    private void DisposeFrameResources(VulkanLogicalDevice device) {
        m_descriptorAllocator.DestroyPool(
            device: device.Commands,
            poolHandle: m_descriptorPool
        );
        m_descriptorPool = 0;
        Array.Clear(array: m_descriptorSets);
        m_drawCommandsPerSet = null;
        m_graphicsPipelines = null;
        m_blitPipeline?.Dispose();
        m_blitPipeline = null;
    }
    private void OnPresentationResourcesRecreated() {
        var device = m_renderer.Device;

        if (m_resourceDevice is null) {
            // Device resources were pre-released during device-loss recovery (ReleaseForDeviceLoss); the frame resources
            // went with them, so there is nothing to dispose — just rebuild the device resources on the new device.
            CreateDeviceResources(device: device);
        } else {
            DisposeFrameResources(device: m_resourceDevice);

            if (m_resourceDevice.Commands != device.Commands) {
                DisposeDeviceResources(device: m_resourceDevice);
                CreateDeviceResources(device: device);
            }
        }

        m_resourceDevice = device;

        // The blit binds one sampled source texture per ring set. This single count drives BOTH the pipeline's
        // descriptor-set layout and the pool's capacity, so they cannot drift out of sync (a pool undersized for
        // the layout would fail vkAllocateDescriptorSets).
        const uint TextureSamplerCount = 1;

        m_blitPipeline = m_graphicsPipelineFactory.Create(
            enableStorageBuffer: false,
            fragmentShaderModule: m_blitFragmentShader!,
            logicalDevice: device,
            pushConstantBinding: null,
            renderPass: m_renderer.RenderPass,
            swapchain: m_renderer.Swapchain,
            textureSamplerCount: TextureSamplerCount,
            vertexShaderModule: m_vertexShader!
        );
        m_descriptorPool = m_descriptorAllocator.CreatePool(
            device: device.Commands,
            maxSets: DescriptorSetRingSize,
            poolSizes: new VulkanDescriptorPoolSize[]
            {
                new(
                DescriptorCount: (TextureSamplerCount * DescriptorSetRingSize),
                DescriptorType: VulkanDescriptorType.CombinedImageSampler
            ),
            }
        );

        // One set + one prebuilt draw-command list per ring slot (the draw command bakes the set handle in, so the
        // Blit path just indexes — see DescriptorSetRingSize).
        var drawCommandsPerSet = new VulkanDrawCommand[DescriptorSetRingSize][];

        for (var setIndex = 0; (setIndex < DescriptorSetRingSize); setIndex++) {
            m_descriptorSets[setIndex] = m_descriptorAllocator.AllocateSet(
                device: device.Commands,
                descriptorSetLayoutHandle: m_blitPipeline.DescriptorSetLayoutHandle,
                poolHandle: m_descriptorPool
            );
            drawCommandsPerSet[setIndex] = [
                new VulkanDrawCommand(
                    DescriptorSetHandle: m_descriptorSets[setIndex],
                    DrawParameters: new VulkanDrawParameters(
                        firstInstance: 0,
                        firstVertex: 0,
                        instanceCount: 1,
                        vertexCount: 3
                    ),
                    PipelineId: m_blitPipelineId,
                    VertexBufferBinding: new VulkanVertexBufferBinding(bufferHandle: m_vertexBuffer!.BufferHandle)
                ),
            ];
        }

        m_lastWrittenImageView = 0;
        m_descriptorSetIndex = 0;
        m_drawCommandsPerSet = drawCommandsPerSet;
        m_graphicsPipelines = new Dictionary<AssetContentHash, VulkanGraphicsPipeline> {
            [m_blitPipelineId] = m_blitPipeline,
        };
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

    /// <summary>Blits the surface fullscreen onto the swapchain. A no-op until presentation resources
    /// exist or when the surface is empty (a skipped frame).</summary>
    public void Blit(Surface surface) {
        if (
            !m_initialized ||
            (m_blitPipeline is null) ||
            (0 == m_descriptorSets[0]) ||
            surface.IsEmpty
        ) {
            return;
        }

        // A same-device root hands over its image-view directly. A root whose output crossed a device boundary
        // arrives one of two ways: zero-copy as a shared NT handle to a texture another backend rendered into
        // shared GPU memory (imported here without a host round-trip), or — when sharing is unavailable — as
        // CPU pixels uploaded onto this device first. Either path yields an image view this device samples.
        var imageViewHandle = surface.ImageViewHandle;

        if (surface.IsSharedHandle) {
            var vulkanFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: GpuPixelFormats.FromSurfaceFormat(format: surface.Format));

            m_sharedImport ??= new VulkanSurfaceImport(
                commandBufferRecordingApi: m_commandBufferRecordingApi,
                commandResourcesFactory: m_commandResourcesFactory,
                externalMemoryApi: m_externalMemoryApi,
                framebufferSetApi: m_framebufferSetApi,
                queueSubmitter: m_queueSubmitter
            );
            imageViewHandle = m_sharedImport.Import(
                deviceContext: m_renderer,
                height: surface.Height,
                sharedHandle: surface.SharedHandle,
                vulkanFormat: vulkanFormat,
                width: surface.Width
            );
        } else if (surface.IsCpuPixels) {
            var vulkanFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: GpuPixelFormats.FromSurfaceFormat(format: surface.Format));

            m_rootUpload ??= new VulkanSurfaceUpload(
                commandBufferRecordingApi: m_commandBufferRecordingApi,
                commandResourcesFactory: m_commandResourcesFactory,
                framebufferSetApi: m_framebufferSetApi,
                offscreenImageApi: m_offscreenImageApi,
                queueSubmitter: m_queueSubmitter,
                bufferApi: m_bufferApi
            );
            imageViewHandle = m_rootUpload.Upload(
                deviceContext: m_renderer,
                height: surface.Height,
                pixels: surface.Pixels,
                vulkanFormat: vulkanFormat,
                width: surface.Width
            );
        }

        if (imageViewHandle != m_lastWrittenImageView) {
            // Cycle to the NEXT ring set before writing (see DescriptorSetRingSize): the set being written was last
            // referenced by a blit the renderer's frame-slot wait already proved retired; the pending blit rides the
            // other set untouched.
            m_descriptorSetIndex = ((m_descriptorSetIndex + 1) % DescriptorSetRingSize);
            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: SamplerBindingIndex,
                descriptorSetHandle: m_descriptorSets[m_descriptorSetIndex],
                device: m_renderer.Device.Commands,
                imageViewHandle: imageViewHandle,
                samplerHandle: m_sampler
            );

            m_lastWrittenImageView = imageViewHandle;
        }

        m_renderer.Present(
            drawCommands: m_drawCommandsPerSet![m_descriptorSetIndex],
            graphicsPipelines: m_graphicsPipelines!
        );
    }
    public void Dispose() {
        m_renderer.PresentationResourcesRecreated -= OnPresentationResourcesRecreated;
        m_rootUpload?.Dispose();
        m_rootUpload = null;
        m_sharedImport?.Dispose();
        m_sharedImport = null;

        if (!m_initialized) {
            return;
        }

        m_initialized = false;

        // If the device resources were already released for device loss (ReleaseForDeviceLoss nulled m_resourceDevice),
        // there is nothing left to drain or dispose — and after an UNRECOVERABLE loss the renderer has no device at all,
        // so falling back to m_renderer.Device would throw. Early-out in that case.
        if (m_resourceDevice is null) {
            return;
        }

        var device = m_resourceDevice;

        device.TryWaitIdle();
        DisposeFrameResources(device: device);
        DisposeDeviceResources(device: device);
    }
    /// <summary>Builds the device-level resources (shaders, vertex buffer, sampler) and subscribes to the
    /// renderer's presentation-recreated signal. The renderer must already be initialized.</summary>
    public void Initialize() {
        var device = m_renderer.Device;

        CreateDeviceResources(device: device);
        m_resourceDevice = device;

        m_renderer.PresentationResourcesRecreated += OnPresentationResourcesRecreated;
        m_initialized = true;
    }
    /// <summary>Releases the compositor's device-derived blit resources on the CURRENT resource device during device-loss
    /// recovery — BEFORE that device is destroyed, so they don't leak (they are not swapchain resources, so the renderer's
    /// device-recreate does not free them, and destroying a device with live children is a validation error and can
    /// crash). The compositor STAYS subscribed to <c>PresentationResourcesRecreated</c>, which rebuilds the resources on
    /// the new device at the next BeginFrame (the null <see cref="m_resourceDevice"/> signals a from-scratch rebuild).
    /// Tolerant of an already-lost device: a faulting drain is swallowed (the host pump drained earlier when it could).</summary>
    public void ReleaseForDeviceLoss() {
        if (
            !m_initialized ||
            (m_resourceDevice is null)
        ) {
            return;
        }

        var device = m_resourceDevice;

        try {
            device.WaitIdle();
        } catch (DeviceLostException) {
            // Device already lost; nothing in flight to drain.
        }

        m_rootUpload?.Dispose();
        m_rootUpload = null;
        m_sharedImport?.Dispose();
        m_sharedImport = null;
        DisposeFrameResources(device: device);
        DisposeDeviceResources(device: device);
        m_resourceDevice = null;
    }
}
