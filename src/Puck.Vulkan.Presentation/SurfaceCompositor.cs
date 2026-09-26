using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Hosting;
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
    private const string VertexShaderFileName = "fullscreen.vert.spv";

    private static readonly byte[] FullscreenTriangleVertexData = FullscreenTriangle.CreateVertexData();
    // The blit's pipeline description: the shared group, and the fullscreen triangle's one float2 position.
    private static readonly GpuGraphicsPipelineDescription BlitDescription = new(
        Layout: SurfaceBlitLayout.Layout,
        Name: "surface-blit",
        VertexInput: new GpuVertexInputLayout(
            Attributes: [new GpuVertexAttribute(
                Format: GpuVertexFormat.R32G32Float,
                Location: 0,
                OffsetBytes: 0
            )],
            StrideBytes: FullscreenTriangle.StrideBytes
        )
    );

    private readonly IVulkanBufferApi m_bufferApi;
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly VulkanDescriptorAllocator m_descriptorAllocator;
    private readonly nint[] m_descriptorSets = new nint[DescriptorSetRingSize];
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private readonly GpuPassPipelineCache m_pipelines;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private readonly VulkanRenderer m_renderer;
    private readonly string m_shaderDirectory;
    private readonly IShaderModuleLoader m_shaderModuleLoader;

    private ReadOnlyMemory<byte> m_blitFragmentBytecode;
    // The blit pipeline's lease on the device's pass pipelines, held from the first presentation resources on a device
    // until that device's resources are released.
    private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_blitLease;
    private AssetContentHash m_blitPipelineId;
    private nint m_descriptorPool;
    private int m_descriptorSetIndex;
    private VulkanDrawCommand[][]? m_drawCommandsPerSet;
    private Dictionary<AssetContentHash, IGpuPipeline>? m_graphicsPipelines;
    private bool m_initialized;
    private nint m_lastWrittenImageView;
    private VulkanLogicalDevice? m_resourceDevice;
    private VulkanSurfaceUpload? m_rootUpload;
    private nint m_sampler;
    private VulkanSurfaceImport? m_sharedImport;
    private VulkanBuffer? m_vertexBuffer;
    private ReadOnlyMemory<byte> m_vertexBytecode;

    public SurfaceCompositor(
        VulkanRenderer renderer,
        string shaderDirectory,
        IShaderModuleLoader shaderModuleLoader,
        GpuPassPipelineCache pipelines,
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
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(queueSubmitter);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(shaderDirectory);
        ArgumentNullException.ThrowIfNull(shaderModuleLoader);

        m_bufferApi = bufferApi;
        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_descriptorAllocator = new VulkanDescriptorAllocator(descriptorApi: descriptorApi);
        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_offscreenImageApi = offscreenImageApi;
        m_pipelines = pipelines;
        m_queueSubmitter = queueSubmitter;
        m_renderer = renderer;
        m_shaderDirectory = shaderDirectory;
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

        // A creation that throws releases the ones before it, newest first; the fields hold only a complete set.
        using var scope = new GpuCreationScope();
        var vertexBuffer = scope.Own(created: VulkanBuffer.Create(
            bufferApi: m_bufferApi,
            device: m_renderer,
            memory: VulkanBufferMemory.HostCoherent,
            sizeBytes: ((ulong)FullscreenTriangleVertexData.Length),
            usage: VulkanBufferUsageFlags.VertexBuffer
        ));

        vertexBuffer.Write<byte>(data: FullscreenTriangleVertexData);

        var sampler = scope.Own(
            handle: CreateSampler(device: device),
            release: handle => m_descriptorAllocator.DestroySampler(
                device: device.Commands,
                samplerHandle: handle
            )
        );

        scope.Complete();
        m_blitPipelineId = blitFragmentShaderInfo.ContentHash;
        m_blitFragmentBytecode = blitFragmentShaderInfo.Content;
        m_vertexBytecode = vertexShaderInfo.Content;
        m_vertexBuffer = vertexBuffer;
        m_sampler = sampler;
    }
    private nint CreateSampler(VulkanLogicalDevice device) =>
        m_descriptorAllocator.CreateSampler(request: new VulkanSamplerCreateRequest(
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
    private void DisposeDeviceResources(VulkanLogicalDevice device) {
        m_descriptorAllocator.DestroySampler(
            device: device.Commands,
            samplerHandle: m_sampler
        );
        m_sampler = 0;
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        // The device's pass pipelines dispose the blit once no other lease holds it.
        m_blitLease?.Release();
        m_blitLease = null;
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

        // The blit is the device's pass pipeline for SurfaceBlitLayout, created for the swapchain's render pass description in
        // its format, compatible with the swapchain's own render pass (VulkanGpuRenderPass.PresentRequestOf). A lease on the new key is
        // taken before the old one is released, so a recreation that keeps the format keeps the pipeline; the pool holds
        // exactly one source image and one sampler per ring set, the pass group's two bindings.
        var previousLease = m_blitLease;

        m_blitLease = m_pipelines.Acquire(
            device: m_renderer,
            key: GpuPassPipelineKey.OfGraphics(
                description: BlitDescription,
                fragment: m_blitFragmentBytecode,
                renderPass: VulkanGpuRenderPass.PresentDescription(format: m_renderer.Swapchain.Format),
                vertex: m_vertexBytecode
            )
        );
        previousLease?.Release();

        var blitPipeline = m_blitLease.Wait(cancellationToken: CancellationToken.None).Graphics!;

        m_descriptorPool = m_descriptorAllocator.CreatePool(
            device: device.Commands,
            maxSets: DescriptorSetRingSize,
            poolSizes: new VulkanDescriptorPoolSize[]
            {
                new(
                DescriptorCount: DescriptorSetRingSize,
                DescriptorType: VulkanDescriptorType.SampledImage
            ),
                new(
                DescriptorCount: DescriptorSetRingSize,
                DescriptorType: VulkanDescriptorType.Sampler
            ),
            }
        );

        // One set + one prebuilt draw-command list per ring slot (the draw command bakes the set handle in, so the
        // Blit path just indexes — see DescriptorSetRingSize). The sampler never changes, so each set takes it once
        // here; a blit writes only the source image.
        var drawCommandsPerSet = new VulkanDrawCommand[DescriptorSetRingSize][];

        for (var setIndex = 0; (setIndex < DescriptorSetRingSize); setIndex++) {
            m_descriptorSets[setIndex] = m_descriptorAllocator.AllocateSet(
                device: device.Commands,
                descriptorSetLayoutHandle: blitPipeline.GroupLayoutHandles[((int)SurfaceBlitLayout.Group)],
                poolHandle: m_descriptorPool
            );
            m_descriptorAllocator.WriteSampler(
                arrayElement: 0,
                binding: SurfaceBlitLayout.SamplerBinding,
                descriptorSetHandle: m_descriptorSets[setIndex],
                device: device.Commands,
                samplerHandle: m_sampler
            );
            drawCommandsPerSet[setIndex] = [
                new VulkanDrawCommand(
                    DescriptorSetGroup: SurfaceBlitLayout.Group,
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
        m_graphicsPipelines = new Dictionary<AssetContentHash, IGpuPipeline> {
            [m_blitPipelineId] = blitPipeline,
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
            (m_graphicsPipelines is null) ||
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
                deviceContext: m_renderer,
                externalMemoryApi: m_externalMemoryApi,
                framebufferSetApi: m_framebufferSetApi,
                queueSubmitter: m_queueSubmitter
            );
            imageViewHandle = m_sharedImport.Import(
                height: surface.Height,
                sharedHandle: surface.SharedHandle,
                vulkanFormat: vulkanFormat,
                width: surface.Width
            );
        } else if (surface.IsCpuPixels) {
            m_rootUpload ??= new VulkanSurfaceUpload(
                bufferApi: m_bufferApi,
                commandBufferRecordingApi: m_commandBufferRecordingApi,
                commandResourcesFactory: m_commandResourcesFactory,
                deviceContext: m_renderer,
                framebufferSetApi: m_framebufferSetApi,
                offscreenImageApi: m_offscreenImageApi,
                queueSubmitter: m_queueSubmitter
            );
            imageViewHandle = m_rootUpload.Upload(
                format: GpuPixelFormats.FromSurfaceFormat(format: surface.Format),
                height: surface.Height,
                pixels: surface.Pixels,
                width: surface.Width
            );
        }

        if (imageViewHandle != m_lastWrittenImageView) {
            // Cycle to the NEXT ring set before writing (see DescriptorSetRingSize): the set being written was last
            // referenced by a blit the renderer's frame-slot wait already proved retired; the pending blit rides the
            // other set untouched.
            m_descriptorSetIndex = ((m_descriptorSetIndex + 1) % DescriptorSetRingSize);
            m_descriptorAllocator.WriteSampledImage(
                arrayElement: 0,
                binding: SurfaceBlitLayout.SourceImageBinding,
                descriptorSetHandle: m_descriptorSets[m_descriptorSetIndex],
                device: m_renderer.Device.Commands,
                imageViewHandle: imageViewHandle
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
