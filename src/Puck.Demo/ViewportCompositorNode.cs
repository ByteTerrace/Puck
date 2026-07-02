using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Compositing;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>One composited pane: the normalized screen region and the node whose surface fills it.</summary>
/// <param name="Region">The normalized <c>[x, y, w, h]</c> rect of the output the source is copied into.</param>
/// <param name="Source">The child render node producing this pane's content (a rect-sized, General-layout storage image).</param>
internal readonly record struct ViewportPane(NormalizedRect Region, IRenderNode Source);

/// <summary>
/// The generic, SOURCE-AGNOSTIC viewport compositor: a compute <see cref="IRenderNode"/> that hosts N child nodes
/// (one per pane) and composites their surfaces into their screen regions via <c>viewport-composite.comp</c>. Unlike
/// <see cref="WorldProducerNode"/> it carries no SDF concern (no beam/cull/views stages) — it is just the composite
/// stage, standing alone, so a pane can be ANYTHING that produces a rect-sized storage image: a hosted test pattern,
/// a resampled image (<see cref="ResampleNode"/>), and — later — a captured window or a foreign engine.
/// <para>
/// Each pane source is produced first at its slot's pixel extent (so the 1:1 composite copy lands in bounds) and
/// left in the General (UAV) working layout; one memory barrier orders all their writes before the composite reads
/// them. The output is handed back shader-readable. A pane that is not already its region's exact pixel size is
/// wrapped in a <see cref="ResampleNode"/> upstream — the compositor itself never scales.
/// </para>
/// </summary>
internal sealed class ViewportCompositorNode : IRenderNode {
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int MaxViewports = 4; // the source array length in viewport-composite.comp (sources[4])
    private const uint OutputBindingIndex = 0; // viewport-composite.comp: Output at binding 0 (register u0)
    private const uint SourceBindingIndex = 1; // viewport-composite.comp: sources[] at binding 1 (register u1)
    private const uint WorkgroupEdge = 8;
    private const int CompositePushByteLength = (16 + ((sizeof(float) * 4) * MaxViewports)); // CompositeParams: uint2 extent + uint count + uint pad + float4 rects[4]

    private readonly nint[] m_boundSourceViews = new nint[MaxViewports];
    private readonly ReadOnlyMemory<byte> m_compositeBytecode;
    private readonly byte[] m_compositePush = new byte[CompositePushByteLength];
    private readonly NodeDescriptor m_descriptor = new(
        Name: "viewport-compositor",
        SurfaceId: SurfaceId.New()
    );
    private readonly string? m_capturePath;
    private readonly uint m_height;
    private readonly IReadOnlyList<ViewportPane> m_panes;
    private readonly IServiceProvider m_serviceProvider;
    private readonly uint m_width;
    private bool m_captured;
    private byte[]? m_capturedPixels;
    private Surface[] m_childSurfaces = [];
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuComputePipeline? m_compositePipeline;
    private nint m_compositeSet;
    private IGpuShaderModule? m_compositeShaderModule;
    private IGpuComputeRecorder? m_computeRecorder;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private IGpuComputeServices? m_gpu;
    private bool m_outputInitialized;
    private nint m_pool;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private IGpuSurfaceReadback? m_readback;
    private bool m_resourcesReady;
    private IGpuStorageImage? m_storageImage;

    /// <summary>Initializes a new instance of the <see cref="ViewportCompositorNode"/> class.</summary>
    /// <param name="serviceProvider">The service provider that resolves the neutral GPU compute services (the device comes from the host context).</param>
    /// <param name="compositeBytecode">The compiled <c>viewport-composite</c> kernel for the host backend (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="panes">The panes to composite, in slot order (1..4); each pane's source fills its region.</param>
    /// <param name="width">The output width in pixels.</param>
    /// <param name="height">The output height in pixels.</param>
    /// <param name="capturePath">An optional PNG path the first composited frame is written to.</param>
    public ViewportCompositorNode(IServiceProvider serviceProvider, ReadOnlyMemory<byte> compositeBytecode, IReadOnlyList<ViewportPane> panes, uint width, uint height, string? capturePath = null) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(panes);

        if ((panes.Count < 1) || (panes.Count > MaxViewports)) {
            throw new ArgumentException(message: $"A viewport compositor hosts 1..{MaxViewports} panes, not {panes.Count}.", paramName: nameof(panes));
        }

        m_capturePath = capturePath;
        m_compositeBytecode = compositeBytecode;
        m_height = height;
        m_panes = panes;
        m_serviceProvider = serviceProvider;
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <summary>The RGBA pixels read back the first time this node captured (its <c>capturePath</c> was set); empty
    /// until then. Lets a cross-backend gate diff two backends' composites without re-reading the GPU.</summary>
    internal ReadOnlyMemory<byte> CapturedPixels => m_capturedPixels;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // The shared device is an inherited host capability (every node in the tree composites on one device).
        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return default;
        }

        ProduceChildren(context: in context);
        EnsureResources(gpuDevice: gpuDevice);
        BindSources();
        PackComposite();
        Render();

        if (
            (m_capturePath is not null) &&
            !m_captured
        ) {
            m_capturedPixels = ReadPixels().ToArray();
            PngImage.Write(height: (int)m_height, path: m_capturePath, rgba: m_capturedPixels, width: (int)m_width);
            m_captured = true;
        }

        return new Surface(
            ImageViewHandle: m_storageImage!.ImageViewHandle,
            Width: m_width,
            Height: m_height,
            Format: SurfaceFormat.R8G8B8A8Unorm
        );
    }

    // Produce each pane's source surface at its slot's pixel rect (matching the 1:1 composite copy). Sources resolve
    // the same shared device from the forwarded host context; their submits are enqueued ahead of the compositor's.
    private void ProduceChildren(in FrameContext context) {
        if (m_childSurfaces.Length == 0) {
            m_childSurfaces = new Surface[m_panes.Count];
        }

        for (var slot = 0; (slot < m_panes.Count); slot++) {
            var region = m_panes[slot].Region;

            m_childSurfaces[slot] = m_panes[slot].Source.ProduceFrame(context: context with {
                TargetHeight = Math.Max(1u, (uint)(region.Height * m_height)),
                TargetWidth = Math.Max(1u, (uint)(region.Width * m_width)),
            });
        }
    }
    private void EnsureResources(IGpuDeviceContext gpuDevice) {
        if (m_resourcesReady) {
            return;
        }

        m_gpu ??= (IGpuComputeServices)m_serviceProvider.GetService(serviceType: typeof(IGpuComputeServices))!;
        m_deviceContext = gpuDevice;
        m_deviceHandle = gpuDevice.DeviceHandle;
        m_computeRecorder = m_gpu.ComputeRecorder;
        m_descriptorAllocator = m_gpu.DescriptorAllocator;
        m_queueSubmitter = m_gpu.QueueSubmitter;

        GpuComputeBinding[] bindings = [
            new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: SourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
        ];

        m_compositeShaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_compositeBytecode);
        m_compositePipeline = m_gpu.ComputePipelineFactory.Create(
            bindings: bindings,
            computeShaderModule: m_compositeShaderModule,
            deviceContext: gpuDevice,
            pushConstantBinding: new GpuPushConstantBinding(data: m_compositePush, offset: 0, stageFlags: GpuShaderStage.Compute)
        );
        m_storageImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: m_height, width: m_width);

        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);

        m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);
        m_compositeSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_compositePipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        m_descriptorAllocator.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, imageViewHandle: m_storageImage.ImageViewHandle);
        m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: gpuDevice);
        m_resourcesReady = true;
    }

    // Bind (or rebind when a source's image-view changed) the source array. Array elements past the live pane count
    // duplicate slot 0 (Vulkan requires every bound array element to be a valid descriptor); the kernel never reads them.
    private void BindSources() {
        var fillerView = SourceViewForSlot(slot: 0);

        for (var element = 0u; (element < MaxViewports); element++) {
            var view = ((element < m_panes.Count) ? SourceViewForSlot(slot: (int)element) : fillerView);

            if (view == m_boundSourceViews[element]) {
                continue;
            }

            m_descriptorAllocator!.WriteStorageImage(arrayElement: element, binding: SourceBindingIndex, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, imageViewHandle: view);
            m_boundSourceViews[element] = view;
        }
    }
    private nint SourceViewForSlot(int slot) {
        var view = m_childSurfaces[slot].ImageViewHandle;

        if (0 == view) {
            throw new InvalidOperationException(message: $"The source node for viewport {slot} did not produce a same-device storage-image surface (a composited pane must hand back a general-layout storage image view).");
        }

        return view;
    }

    // Pack CompositeParams: the output extent, the live pane count, and each pane's normalized rect.
    private void PackComposite() {
        var uints = MemoryMarshal.Cast<byte, uint>(span: m_compositePush.AsSpan());

        uints[0] = m_width;
        uints[1] = m_height;           // imageExtent
        uints[2] = (uint)m_panes.Count; // viewportCount
        uints[3] = 0;                   // pad

        var floats = MemoryMarshal.Cast<byte, float>(span: m_compositePush.AsSpan());

        for (var index = 0; (index < m_panes.Count); index++) {
            var region = m_panes[index].Region;
            var b = (4 + (index * 4)); // rects[] start after the 4-dword (16-byte) header

            floats[b + 0] = region.X;
            floats[b + 1] = region.Y;
            floats[b + 2] = region.Width;
            floats[b + 3] = region.Height;
        }
    }
    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;
        // After the first frame the output rests shader-readable (the host/gate sampled or read it back); the first
        // frame starts undefined.
        var outputOldLayout = (m_outputInitialized ? GpuImageLayout.ShaderReadOnly : GpuImageLayout.Undefined);
        var outputSourceAccess = (m_outputInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None);
        var outputSourceStage = (m_outputInitialized ? GpuComputeStage.FragmentShader : GpuComputeStage.TopOfPipe);

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Each pane source submitted its writes fire-and-forget on the shared queue ahead of this command buffer; a
        // global memory barrier (covering all prior compute writes on the queue) orders them before the composite read.
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            imageHandle: m_storageImage!.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: outputOldLayout,
            sourceAccessMask: outputSourceAccess,
            sourceStageMask: outputSourceStage
        );

        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_compositePipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_compositePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_compositePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_compositePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountZ: 1
        );

        // Hand the output off shader-readable (a same-device host samples it; the gate reads it back).
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.FragmentShader,
            deviceHandle: m_deviceHandle,
            imageHandle: m_storageImage.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Gate-driven one-shot: block so the captured readback reflects a complete frame (the pane submits are earlier
        // on the same queue, so waiting on this also waits on them).
        m_queueSubmitter!.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);
        m_outputInitialized = true;
    }
    private ReadOnlyMemory<byte> ReadPixels() {
        m_readback ??= m_gpu!.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext!);

        return m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext!,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            sourceImageHandle: m_storageImage!.ImageHandle,
            width: m_width
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_deviceContext?.WaitIdle();

        foreach (var pane in m_panes) {
            pane.Source.Dispose();
        }

        m_commandPool?.Dispose();
        m_compositePipeline?.Dispose();

        if ((0 != m_pool) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_storageImage?.Dispose();
        m_compositeShaderModule?.Dispose();
        m_readback?.Dispose();
    }
}
