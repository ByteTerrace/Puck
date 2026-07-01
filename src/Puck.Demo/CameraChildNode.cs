using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.Hosting;
using Puck.Platform;
using Puck.Scene;
using Puck.Vulkan.Interfaces;

namespace Puck.Demo;

/// <summary>
/// A LIVE camera as a per-viewport WORLD child (the <see cref="LiveCameraSource"/> consumer): it uploads the newest
/// webcam frame (the M2 CPU-upload tier) onto the world's device and SAMPLES it — through a combined image sampler —
/// into a rect-sized, General-layout storage image, exactly the integer-copy source contract
/// <see cref="WorldProducerNode"/> composites into a viewport slot. The resample both scales the fixed-resolution
/// camera to the (animating) slot extent and applies the <see cref="CameraFit"/> policy (stretch vs center-crop), so a
/// camera drops into a viewport interchangeably with an SDF camera. Until the first frame arrives (or with no device) a
/// neutral placeholder fills the slot so the compositor always sees a valid same-device view.
/// </summary>
internal sealed class CameraChildNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;  // output format
    private const uint OutputBindingIndex = 0;                 // resample.comp: Output at binding 0 (register u0)
    private const uint PlaceholderEdge = 8;                    // the pre-first-frame gray placeholder source extent
    private const int ResamplePushByteLength = 32;             // ResampleParams { uint2 outExtent; float2 srcOrigin; float2 srcSize; uint cellSize; uint quantizeLevels; }
    private const uint SourceBindingIndex = 1;                 // resample.comp: Source combined-image-sampler at binding 1 (t0/s0)
    private const uint WorkgroupEdge = 8;

    private static readonly byte[] PlaceholderPixels = CreatePlaceholderPixels();

    private readonly IServiceProvider m_cameraServices;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "camera-child",
        SurfaceId: SurfaceId.New()
    );
    private readonly CameraFit m_fit;
    private readonly IServiceProvider m_gpuServices;
    private readonly int m_requestedHeight;
    private readonly int m_requestedWidth;
    private readonly ReadOnlyMemory<byte> m_resampleBytecode;
    private readonly string m_sourceId;
    private readonly byte[] m_resamplePush = new byte[ResamplePushByteLength];

    // Re-open cadence after a feed ends or an open fails, in render frames (~10 s at 60 Hz): long enough that device
    // re-enumeration is not hammered, short enough that replugging a camera comes back without a restart.
    private const int ReopenCooldownFrames = 600;

    private DirectXComputeWorldDevice? m_allocatorDevice;
    private nint m_boundSourceView;
    private bool m_cameraChecked;
    private ExternalClockSource? m_clockSource;
    private long m_lastPublishedVersion = -1;
    private bool m_noDeviceLogged;
    private int m_reopenCountdown;
    private IGpuSurfaceUpload? m_cameraUpload;
    private nint m_cameraView;
    private uint m_cameraViewHeight;
    private uint m_cameraViewWidth;
    private ICameraCaptureSession? m_cameraSession;
    private ICameraSharedCaptureSession? m_sharedSession;
    private IGpuExportableStorageImage?[] m_sharedTargets = [];
    private IGpuSurfaceImport?[] m_slotImports = [];
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuComputeRecorder? m_computeRecorder;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private IGpuComputeServices? m_gpu;
    private uint m_height;
    private long m_lastRenderedVersion = -1;
    private bool m_outputInitialized;
    private IGpuStorageImage? m_outputImage;
    private IGpuSurfaceUpload? m_placeholderUpload;
    private nint m_placeholderView;
    private nint m_pool;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private IGpuComputePipeline? m_resamplePipeline;
    private nint m_resampleSet;
    private IGpuShaderModule? m_resampleShaderModule;
    private bool m_resourcesReady;
    private nint m_sampler;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="CameraChildNode"/> class.</summary>
    /// <param name="gpuServices">The world's neutral GPU compute services (the child produces on the world's device).</param>
    /// <param name="cameraServices">The application services that resolve <see cref="ICameraCaptureService"/> (device-agnostic CPU pixels).</param>
    /// <param name="source">The document live-camera source (requested mode + fit policy).</param>
    /// <param name="sourceId">The stable rhythm-source identity this camera registers with the external-clock registry
    /// (e.g. <c>"camera:3"</c>) — the id a document's <c>host.genlock</c> elects; this node never elects itself.</param>
    /// <param name="directX">Whether the world device is Direct3D 12 (selects the DXIL resample kernel).</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public CameraChildNode(IServiceProvider gpuServices, IServiceProvider cameraServices, LiveCameraSource source, string sourceId, bool directX) {
        ArgumentNullException.ThrowIfNull(cameraServices);
        ArgumentNullException.ThrowIfNull(gpuServices);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceId);

        m_cameraServices = cameraServices;
        m_fit = source.Fit;
        m_gpuServices = gpuServices;
        m_sourceId = sourceId;
        m_requestedHeight = source.RequestedHeight;
        m_requestedWidth = source.RequestedWidth;
        m_resampleBytecode = File.ReadAllBytes(path: Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Shaders",
            "Resample",
            (directX ? "resample.comp.dxil" : "resample.comp.spv")
        ));
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (
            m_disposed ||
            (0 == context.TargetWidth) ||
            (0 == context.TargetHeight)
        ) {
            return default;
        }

        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return default;
        }

        EnsureResources(gpuDevice: gpuDevice, height: context.TargetHeight, width: context.TargetWidth);
        MaintainCamera();
        EnsureCamera();

        // Newest-frame-wins: skip the upload + resample dispatch (and its submit) when the camera has not delivered a new
        // frame since the last render and the output already holds it. The pump then renders at full rate between the
        // camera's own (~30 fps) arrivals instead of re-uploading every rendered frame. A resize clears m_outputInitialized
        // (via EnsureResources), forcing a re-render into the fresh output.
        var cameraVersion = (m_sharedSession?.FrameVersion ?? m_cameraSession?.FrameVersion ?? 0L);

        // Genlock: forward each NEW frame's arrival timestamp (stamped on the grabber thread, so it is accurate even
        // though this forwarding is render-quantized) into this camera's OWN rhythm channel. Whether the pacer follows
        // it is the HOST's election (host.genlock), never this node's decision. Published before the skip below — the
        // registry needs arrivals even on frames this node does no GPU work for.
        if ((m_clockSource is not null) && (cameraVersion != m_lastPublishedVersion) && (cameraVersion > 0L)) {
            var arrival = (m_sharedSession?.LastFrameTimestamp ?? m_cameraSession?.LastFrameTimestamp ?? 0L);

            if (0L != arrival) {
                m_clockSource.Publish(arrivalTimestamp: arrival, frameVersion: cameraVersion);
            }

            m_lastPublishedVersion = cameraVersion;
        }

        if (m_outputInitialized && (cameraVersion == m_lastRenderedVersion)) {
            return CurrentSurface();
        }

        UpdateSource();
        Render();

        m_lastRenderedVersion = cameraVersion;

        return CurrentSurface();
    }

    // A rect-sized, General-layout storage image: the integer-copy source contract WorldProducerNode composites.
    private Surface CurrentSurface() {
        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_outputImage!.ImageViewHandle,
            Width: m_width
        );
    }

    // Replug recovery: a feed that has permanently ended (device unplugged, end of stream) is torn down here, and the
    // device is re-opened after a cooldown — so plugging a camera back in brings the pane back without a restart. A
    // failed open also retries on the same cooldown rather than never. The last shown frame persists in the output
    // image while disconnected (never a black pane).
    private void MaintainCamera() {
        if ((m_sharedSession is not null) && m_sharedSession.IsEnded) {
            Console.Out.WriteLine(value: $"[camera] '{m_sharedSession.Name}' feed ended; re-opening in ~{ReopenCooldownFrames} frames.");
            ReleaseGpuTier();

            m_cameraChecked = false;
            m_reopenCountdown = ReopenCooldownFrames;
        } else if ((m_cameraSession is not null) && m_cameraSession.IsEnded) {
            Console.Out.WriteLine(value: $"[camera] '{m_cameraSession.Name}' feed ended; re-opening in ~{ReopenCooldownFrames} frames.");
            m_cameraSession.Dispose();

            m_cameraSession = null;
            m_cameraChecked = false;
            m_reopenCountdown = ReopenCooldownFrames;
        } else if (m_cameraChecked && (m_sharedSession is null) && (m_cameraSession is null) && (0 == m_reopenCountdown)) {
            // No feed at all (the last open found no device or failed): retry on the cooldown cadence.
            m_cameraChecked = false;
            m_reopenCountdown = ReopenCooldownFrames;
        }
    }

    // Resolve the camera service and open the default device — the GPU-resident zero-copy tier first (frames never
    // visit host memory), the CPU-upload tier as the correctness floor; on failure the placeholder fills the slot (and
    // MaintainCamera retries later). The active tier is logged (the plan's tier telemetry).
    private void EnsureCamera() {
        if (m_cameraChecked) {
            return;
        }

        if (m_reopenCountdown > 0) {
            m_reopenCountdown--;

            return;
        }

        m_cameraChecked = true;

        if (m_cameraServices.GetService(serviceType: typeof(ICameraCaptureService)) is not ICameraCaptureService service || !service.IsSupported) {
            if (!m_noDeviceLogged) {
                m_noDeviceLogged = true;

                Console.Out.WriteLine(value: "[camera] no capture backend for the viewport source; showing a placeholder.");
            }

            return;
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && TryOpenGpuTier(service: service)) {
            return;
        }

        var requestedWidth = ((m_requestedWidth > 0) ? m_requestedWidth : 640);
        var requestedHeight = ((m_requestedHeight > 0) ? m_requestedHeight : 480);

        if (service.TryOpenDefault(requestedWidth: requestedWidth, requestedHeight: requestedHeight, session: out var session)) {
            m_cameraSession = session;

            RegisterRhythmSource();
            Console.Out.WriteLine(value: $"[camera] viewport source '{session.Name}' {session.Width}x{session.Height} (CPU-upload tier).");

            return;
        }

        // Logged once; MaintainCamera keeps retrying quietly on its cooldown (a replugged device then comes back).
        if (!m_noDeviceLogged) {
            m_noDeviceLogged = true;

            Console.Out.WriteLine(value: "[camera] no capture device for the viewport source; showing a placeholder (re-probing periodically).");
        }
    }

    // The GPU-resident zero-copy tier: a Media Foundation GPU session converts frames to BGRA on the GPU and copies
    // them into D3D12-allocated simultaneous-access shared targets; this node imports each target ONCE on the world
    // device and, per camera frame, just rebinds the newest published slot's view as the resample source. The targets
    // are allocated on the world device when it is Direct3D 12, or on a bespoke LUID-matched D3D12 device for a Vulkan
    // world; either way they share the adapter, so the world-side import is the proven D3D12-handle path.
    [SupportedOSPlatform("windows10.0.10240")]
    private bool TryOpenGpuTier(ICameraCaptureService service) {
        try {
            long adapterLuid;
            IGpuDeviceContext allocatorContext;

            if (m_deviceContext is IDirectXDeviceContext directXWorld) {
                adapterLuid = new DirectXNativeDeviceApi().GetAdapterLuid(deviceHandle: directXWorld.Device.Handle);
                allocatorContext = m_deviceContext!;
            } else if ((m_cameraServices.GetService(serviceType: typeof(IVulkanDeviceContext)) is IVulkanDeviceContext vulkanHost)
                && (m_cameraServices.GetService(serviceType: typeof(IVulkanPhysicalDeviceApi)) is IVulkanPhysicalDeviceApi physicalDeviceApi)) {
                adapterLuid = physicalDeviceApi.GetDeviceLuid(
                    instanceHandle: vulkanHost.Instance.Handle,
                    physicalDeviceHandle: vulkanHost.LogicalDevice.PhysicalDevice.Handle
                );
                m_allocatorDevice = new DirectXComputeWorldDevice(hostProvider: m_cameraServices);
                allocatorContext = m_allocatorDevice.DeviceContext;
            } else {
                // No Direct3D 12 world device and no Vulkan host to LUID-match a bespoke allocator against (e.g. the
                // reverse Vulkan-produce-on-DX-host path) — the CPU tier covers it.
                return false;
            }

            if (!service.TryOpenSharedDefault(adapterLuid: adapterLuid, requestedWidth: m_requestedWidth, requestedHeight: m_requestedHeight, session: out var session)) {
                ReleaseGpuTier();

                return false;
            }

            const int RingSize = 3;

            var factory = new DirectXGpuSurfaceExportFactory();

            m_sharedSession = session;
            m_sharedTargets = new IGpuExportableStorageImage?[RingSize];
            m_slotImports = new IGpuSurfaceImport?[RingSize];

            var handles = new nint[RingSize];

            for (var index = 0; (index < RingSize); index++) {
                m_sharedTargets[index] = factory.CreateSimultaneousAccessStorageImage(
                    deviceContext: allocatorContext,
                    format: GpuPixelFormat.B8G8R8A8Unorm,
                    height: (uint)session.Height,
                    width: (uint)session.Width
                );
                m_slotImports[index] = m_gpu!.SurfaceTransferFactory.CreateImport(deviceContext: m_deviceContext!);
                handles[index] = m_sharedTargets[index]!.SharedHandle;
            }

            session.Start(sharedTargetHandles: handles);

            RegisterRhythmSource();
            Console.Out.WriteLine(value: $"[camera] viewport source '{session.Name}' {session.Width}x{session.Height} (GPU zero-copy tier).");

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[camera] the GPU zero-copy tier failed to open ({exception.Message}); falling back to the CPU-upload tier.");
            ReleaseGpuTier();

            return false;
        }
    }

    // A rhythm source registers only once a device has actually opened (so a camera-less pane never pollutes the
    // registry's auto-election), under this pane's stable identity — which a document's host.genlock may elect.
    // Idempotent across replug re-opens; whether anyone follows the rhythm is never this node's decision.
    private void RegisterRhythmSource() {
        m_clockSource ??= (m_cameraServices.GetService(serviceType: typeof(ExternalClockRegistry)) as ExternalClockRegistry)?.RegisterSource(sourceId: m_sourceId);
    }

    // GPU-tier teardown (open failure, device loss, dispose): stop the writer FIRST (the session's D3D11 device holds
    // the targets open), then the world-device imports, then the targets and their allocator.
    private void ReleaseGpuTier() {
        m_sharedSession?.Dispose();
        m_sharedSession = null;

        foreach (var import in m_slotImports) {
            import?.Dispose();
        }

        m_slotImports = [];

        foreach (var target in m_sharedTargets) {
            target?.Dispose();
        }

        m_sharedTargets = [];

        if ((m_allocatorDevice is not null) && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            m_allocatorDevice.Dispose();
            m_allocatorDevice = null;
        }
    }

    // Upload the newest camera frame (or the placeholder before the first frame), rebind the sampler if the source view
    // changed, and refresh the fit push constants for the current source dimensions.
    private void UpdateSource() {
        nint source;
        uint sourceWidth;
        uint sourceHeight;

        if ((m_sharedSession is not null) && (m_sharedSession.LatestSlot >= 0)) {
            // The GPU zero-copy tier: sample the newest published shared target. The per-slot import is a cache hit
            // after its first frame (the ring handles are stable), so steady state is at most a descriptor rebind.
            var slot = m_sharedSession.LatestSlot;

            source = m_slotImports[slot]!.Import(
                deviceContext: m_deviceContext!,
                format: GpuPixelFormat.B8G8R8A8Unorm,
                height: (uint)m_sharedSession.Height,
                sharedHandle: m_sharedTargets[slot]!.SharedHandle,
                width: (uint)m_sharedSession.Width
            );
            sourceHeight = (uint)m_sharedSession.Height;
            sourceWidth = (uint)m_sharedSession.Width;
        } else if ((m_cameraSession is not null) && m_cameraSession.TryCapture(out var frame) && frame.IsCpuPixels) {
            m_cameraView = m_cameraUpload!.Upload(deviceContext: m_deviceContext!, pixels: frame.Pixels, width: frame.Width, height: frame.Height, format: GpuPixelFormat.B8G8R8A8Unorm);
            m_cameraViewHeight = frame.Height;
            m_cameraViewWidth = frame.Width;
            source = m_cameraView;
            sourceHeight = frame.Height;
            sourceWidth = frame.Width;
        } else if (0 != m_cameraView) {
            // A camera opened and produced at least one frame; keep sampling the last uploaded frame.
            source = m_cameraView;
            sourceHeight = m_cameraViewHeight;
            sourceWidth = m_cameraViewWidth;
        } else {
            if (0 == m_placeholderView) {
                m_placeholderView = m_placeholderUpload!.Upload(deviceContext: m_deviceContext!, pixels: PlaceholderPixels, width: PlaceholderEdge, height: PlaceholderEdge, format: GpuPixelFormat.B8G8R8A8Unorm);
            }

            source = m_placeholderView;
            sourceHeight = PlaceholderEdge;
            sourceWidth = PlaceholderEdge;
        }

        WriteFit(sourceWidth: sourceWidth, sourceHeight: sourceHeight);

        if (source != m_boundSourceView) {
            m_descriptorAllocator!.WriteCombinedImageSampler(arrayElement: 0, binding: SourceBindingIndex, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, imageViewHandle: source, samplerHandle: m_sampler);
            m_boundSourceView = source;
        }
    }

    // ResampleParams: sample the source into the output rect. Sample = stretch the whole source; Fill = center-crop the
    // source to the output's aspect (normalized source UV origin/size). Cell size / quantization off.
    private void WriteFit(uint sourceWidth, uint sourceHeight) {
        var originX = 0f;
        var originY = 0f;
        var sizeX = 1f;
        var sizeY = 1f;

        if ((CameraFit.Fill == m_fit) && (sourceWidth > 0) && (sourceHeight > 0) && (m_width > 0) && (m_height > 0)) {
            var outAspect = ((float)m_width / m_height);
            var sourceAspect = ((float)sourceWidth / sourceHeight);

            if (sourceAspect > outAspect) {
                sizeX = (outAspect / sourceAspect);
                originX = ((1f - sizeX) * 0.5f);
            } else {
                sizeY = (sourceAspect / outAspect);
                originY = ((1f - sizeY) * 0.5f);
            }
        }

        var words = MemoryMarshal.Cast<byte, uint>(span: m_resamplePush.AsSpan());
        var floats = MemoryMarshal.Cast<byte, float>(span: m_resamplePush.AsSpan());

        words[0] = m_width;
        words[1] = m_height;
        floats[2] = originX;
        floats[3] = originY;
        floats[4] = sizeX;
        floats[5] = sizeY;
        words[6] = 1u; // cellSize (no pixelation)
        words[7] = 0u; // quantizeLevels (off)
    }

    private void EnsureResources(IGpuDeviceContext gpuDevice, uint height, uint width) {
        if (
            m_resourcesReady &&
            (m_width == width) &&
            (m_height == height)
        ) {
            return;
        }

        m_gpu ??= (IGpuComputeServices)m_gpuServices.GetService(serviceType: typeof(IGpuComputeServices))!;

        if (!m_resourcesReady) {
            m_computeRecorder = m_gpu.ComputeRecorder;
            m_deviceContext = gpuDevice;
            m_deviceHandle = gpuDevice.DeviceHandle;
            m_descriptorAllocator = m_gpu.DescriptorAllocator;
            m_queueSubmitter = m_gpu.QueueSubmitter;

            GpuComputeBinding[] resampleBindings = [
                new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
                new GpuComputeBinding(Binding: SourceBindingIndex, Kind: GpuComputeBindingKind.SampledImage),
            ];

            m_resampleShaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_resampleBytecode);
            m_resamplePipeline = m_gpu.ComputePipelineFactory.Create(
                bindings: resampleBindings,
                computeShaderModule: m_resampleShaderModule,
                deviceContext: gpuDevice,
                pushConstantBinding: new GpuPushConstantBinding(data: m_resamplePush, offset: 0, stageFlags: GpuShaderStage.Compute),
                samplerFilter: GpuSamplerFilter.Linear
            );
            m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: m_deviceHandle, filter: GpuSamplerFilter.Linear);

            var poolSizes = GpuDescriptorPoolSizes.ForSets(resampleBindings);

            m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);
            m_resampleSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_resamplePipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: gpuDevice);
            m_cameraUpload = m_gpu.SurfaceTransferFactory.CreateUpload(deviceContext: gpuDevice);
            m_placeholderUpload = m_gpu.SurfaceTransferFactory.CreateUpload(deviceContext: gpuDevice);
        }

        // (Re)create the rect-sized output and rebind it; the pipeline, sampler, pool, and uploads are extent-independent.
        m_outputImage?.Dispose();
        m_outputImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: height, width: width);
        m_descriptorAllocator!.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, imageViewHandle: m_outputImage.ImageViewHandle);
        m_outputInitialized = false;
        m_height = height;
        m_width = width;
        m_resourcesReady = true;
    }

    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        if (!m_outputInitialized) {
            // Bring the freshly created output into the General (UAV) working layout the resample writes and the
            // compositor reads; it then persists there (written each frame, read under the parent's per-frame barrier).
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, imageHandle: m_outputImage!.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);

            m_outputInitialized = true;
        }

        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_resamplePipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_resamplePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_resamplePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_resamplePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge), groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge), groupCountZ: 1);
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Fire-and-forget on the shared queue: enqueued ahead of the parent compositor's submit, which barriers this
        // node's output writes before its composite read.
        m_queueSubmitter!.Submit(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // The GPU resources belong to the (lost) world device; the next EnsureResources rebuilds them. A CPU-tier
        // camera session is device-independent (CPU pixels), so it survives; the GPU tier's session/ring/imports all
        // ride GPU devices, so tear the tier down and let the next frame re-open it (possibly landing on either tier).
        if (m_sharedSession is not null) {
            ReleaseGpuTier();

            m_cameraChecked = false;
        }

        m_resourcesReady = false;
        m_outputInitialized = false;
        m_boundSourceView = 0;
        m_cameraView = 0;
        m_lastRenderedVersion = -1;
        m_placeholderView = 0;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        // Stop the GPU tier's writer first (its D3D11 device holds the shared targets open), then drain the world
        // device before releasing what it reads.
        ReleaseGpuTier();
        m_deviceContext?.WaitIdle();
        m_cameraSession?.Dispose();
        m_cameraSession = null;
        m_cameraUpload?.Dispose();
        m_placeholderUpload?.Dispose();
        m_commandPool?.Dispose();
        m_resamplePipeline?.Dispose();

        if ((0 != m_sampler) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroySampler(deviceHandle: m_deviceHandle, samplerHandle: m_sampler);
            m_sampler = 0;
        }

        if ((0 != m_pool) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_outputImage?.Dispose();
        m_resampleShaderModule?.Dispose();
    }

    // A small neutral gray B8G8R8A8 tile shown until the first camera frame (or when no device opened).
    private static byte[] CreatePlaceholderPixels() {
        var pixels = new byte[PlaceholderEdge * PlaceholderEdge * 4];

        Array.Fill(array: pixels, value: (byte)0x20);

        for (var index = 3; (index < pixels.Length); index += 4) {
            pixels[index] = 0xFF; // opaque alpha (ignored by the blit, but keep the tile well-formed)
        }

        return pixels;
    }
}
