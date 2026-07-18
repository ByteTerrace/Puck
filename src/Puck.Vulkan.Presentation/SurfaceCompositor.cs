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

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly VulkanDescriptorAllocator m_descriptorAllocator;
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
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
    private VulkanSurfaceImport? m_sharedImport;
    private VulkanShaderModule? m_blitFragmentShader;
    private VulkanGraphicsPipeline? m_blitPipeline;
    private AssetContentHash m_blitPipelineId;
    private nint m_descriptorPool;
    private readonly nint[] m_descriptorSets = new nint[DescriptorSetRingSize];
    private int m_descriptorSetIndex;
    private VulkanDrawCommand[][]? m_drawCommandsPerSet;
    private Dictionary<AssetContentHash, VulkanGraphicsPipeline>? m_graphicsPipelines;
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
        IVulkanExternalMemoryApi externalMemoryApi,
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
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
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
        m_externalMemoryApi = externalMemoryApi;
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
            var vulkanFormat = surface.Format switch {
                SurfaceFormat.B8G8R8A8Unorm => VulkanFormat.B8G8R8A8Unorm,
                SurfaceFormat.R8G8B8A8Unorm => VulkanFormat.R8G8B8A8Unorm,
                _ => throw new InvalidOperationException(message: "The surface format has no Vulkan mapping."),
            };

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
            var vulkanFormat = surface.Format switch {
                SurfaceFormat.B8G8R8A8Unorm => VulkanFormat.B8G8R8A8Unorm,
                SurfaceFormat.R8G8B8A8Unorm => VulkanFormat.R8G8B8A8Unorm,
                _ => throw new InvalidOperationException(message: "The surface format has no Vulkan mapping."),
            };

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
                deviceHandle: m_renderer.Device.Handle,
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

    private void OnPresentationResourcesRecreated() {
        var device = m_renderer.Device;

        if (m_resourceDevice is null) {
            // Device resources were pre-released during device-loss recovery (ReleaseForDeviceLoss); the frame resources
            // went with them, so there is nothing to dispose — just rebuild the device resources on the new device.
            CreateDeviceResources(device: device);
        } else {
            DisposeFrameResources(device: m_resourceDevice);

            if (m_resourceDevice.Handle != device.Handle) {
                DisposeDeviceResources(device: m_resourceDevice);
                CreateDeviceResources(device: device);
            }
        }

        m_resourceDevice = device;

        // The blit binds one sampled source texture per ring set. This single count drives BOTH the pipeline's
        // descriptor-set layout and the pool's capacity, so they cannot drift out of sync (a pool undersized for
        // the layout would fail vkAllocateDescriptorSets).
        const uint textureSamplerCount = 1;

        m_blitPipeline = m_graphicsPipelineFactory.Create(
            enableStorageBuffer: false,
            fragmentShaderModule: m_blitFragmentShader!,
            logicalDevice: device,
            pushConstantBinding: null,
            renderPass: m_renderer.RenderPass,
            swapchain: m_renderer.Swapchain,
            textureSamplerCount: textureSamplerCount,
            vertexShaderModule: m_vertexShader!
        );
        m_descriptorPool = m_descriptorAllocator.CreatePool(
            deviceHandle: device.Handle,
            maxSets: DescriptorSetRingSize,
            poolSizes: new VulkanDescriptorPoolSize[]
            {
                new(
                    DescriptorCount: (textureSamplerCount * DescriptorSetRingSize),
                    DescriptorType: VulkanDescriptorType.CombinedImageSampler
                ),
            }
        );

        // One set + one prebuilt draw-command list per ring slot (the draw command bakes the set handle in, so the
        // Blit path just indexes — see DescriptorSetRingSize).
        var drawCommandsPerSet = new VulkanDrawCommand[DescriptorSetRingSize][];

        for (var setIndex = 0; (setIndex < DescriptorSetRingSize); setIndex++) {
            m_descriptorSets[setIndex] = m_descriptorAllocator.AllocateSet(
                deviceHandle: device.Handle,
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
        m_sampler = m_descriptorAllocator.CreateSampler(request: new VulkanSamplerCreateRequest(
            AddressModeU: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeV: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeW: VulkanSamplerAddressMode.ClampToEdge,
            AnisotropyEnable: 0,
            BorderColor: 0,
            CompareEnable: 0,
            CompareOp: 0,
            DeviceHandle: device.Handle,
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
            Array.Clear(array: m_descriptorSets);
        }

        m_drawCommandsPerSet = null;
        m_graphicsPipelines = null;
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

    /// <summary>Releases the compositor's device-derived blit resources on the CURRENT resource device during device-loss
    /// recovery — BEFORE that device is destroyed, so they don't leak (they are not swapchain resources, so the renderer's
    /// device-recreate does not free them, and destroying a device with live children is a validation error and can
    /// crash). The compositor STAYS subscribed to <c>PresentationResourcesRecreated</c>, which rebuilds the resources on
    /// the new device at the next BeginFrame (the null <see cref="m_resourceDevice"/> signals a from-scratch rebuild).
    /// Tolerant of an already-lost device: a faulting drain is swallowed (the host pump drained earlier when it could).</summary>
    public void ReleaseForDeviceLoss() {
        if (!m_initialized || (m_resourceDevice is null)) {
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
}
