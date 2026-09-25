using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>
/// A device-free stand-in for every neutral GPU service a render node records through: each call does nothing, and
/// every handle is a fixed nonzero value. It lets a node run its whole CPU path — recording, submission, fence waits,
/// uploads — so the GPU work it counts can be checked exactly without a device. Fences read signaled once submitted,
/// so every submission completes as soon as a node polls it.
/// <para>
/// A 1×1 readback returns the SDF ISA report of the version given at construction, so an SDF engine passes its shader-set
/// handshake; any other readback returns zeroed pixels. Nothing here allocates once a node has created its resources.
/// </para>
/// </summary>
internal sealed class FakeGpuDevice :
    IGpuRecorder,
    IGpuCommandPoolFactory,
    IGpuPipelineFactory,
    IGpuBindings,
    IGpuDeviceContext,
    IGpuQueueSubmitter,
    IGpuShaderModuleFactory,
    IGpuBufferFactory,
    IGpuImageFactory,
    IGpuRenderPassFactory,
    IGpuSurfaceTransferFactory {
    private readonly byte m_reportVersion;

    /// <summary>Initializes a new instance of the <see cref="FakeGpuDevice"/> class.</summary>
    /// <param name="reportVersion">The ISA version a 1×1 readback reports.</param>
    public FakeGpuDevice(byte reportVersion) {
        m_reportVersion = reportVersion;
        Services = new GpuDeviceServices {
            Bindings = this,
            BufferFactory = this,
            CommandPoolFactory = this,
            ImageFactory = this,
            PipelineFactory = this,
            QueueSubmitter = this,
            Recorder = this,
            RenderPassFactory = this,
            ShaderModuleFactory = this,
            SurfaceTransferFactory = this,
        };
    }

    public long AdapterLuid => 0L;
    /// <summary>Gets or sets a hook every compute pipeline creation runs first, on whatever thread creates it — a law
    /// holds a pipeline build by blocking here.</summary>
    public Action? BeforeComputePipeline { get; set; }
    public GpuDeviceCapabilities? Capabilities => null;
    public GpuDeviceIdentity? Identity => null;
    public GpuMemoryProfile MemoryProfile => default;
    /// <summary>Gets this device as every one of its own services.</summary>
    public GpuDeviceServices Services { get; }
    /// <summary>Gets every descriptor pool created, in creation order, as its creation sized it.</summary>
    public List<GpuDescriptorPoolSizes> PoolsCreated { get; } = [];
    /// <summary>Gets the number of submissions made, fenced or not.</summary>
    public int Submissions { get; private set; }

    public void WaitIdle() { }

    void IGpuRecorder.BeginCommandBuffer(nint commandBufferHandle) { }
    void IGpuRecorder.EndCommandBuffer(nint commandBufferHandle) { }
    void IGpuRecorder.BeginDebugGroup(nint commandBufferHandle, string label) { }
    void IGpuRecorder.EndDebugGroup(nint commandBufferHandle) { }
    void IGpuRecorder.BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area) { }
    void IGpuRecorder.EndRenderPass(nint commandBufferHandle) { }
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) { }
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle) { }
    void IGpuRecorder.PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) { }
    void IGpuRecorder.BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) { }
    void IGpuRecorder.BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) { }
    void IGpuRecorder.SetScissor(nint commandBufferHandle, GpuPixelRect rect) { }
    void IGpuRecorder.Draw(nint commandBufferHandle, in GpuDrawParameters parameters) { }
    void IGpuRecorder.DrawIndexed(nint commandBufferHandle, uint indexCount) { }
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) { }
    void IGpuRecorder.DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) { }
    void IGpuRecorder.ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) { }
    void IGpuRecorder.ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) { }
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) { }
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) { }
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) { }
    IGpuCommandPool IGpuCommandPoolFactory.Create() => new Resource();
    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        BeforeComputePipeline?.Invoke();

        return new Resource();
    }
    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) => 7;
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes) {
        PoolsCreated.Add(item: sizes);

        return 8;
    }
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => 9;
    void IGpuBindings.DestroyPool(nint poolHandle) { }
    void IGpuBindings.DestroySampler(nint samplerHandle) { }
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) { }
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) { }
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) { }
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) => new Resource();
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() => new Fence();
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles) => Submissions++;
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        // A backend accepts only its own fence type; a counting fence reaching here is a missed unwrap.
        ((Fence)fence).Armed = true;
        Submissions++;
    }
    void IGpuQueueSubmitter.SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) => Submissions++;
    IGpuRenderPass IGpuRenderPassFactory.Create(GpuRenderPassDescription description) => new Resource();
    IGpuFramebuffer IGpuRenderPassFactory.CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) => new Resource(width: colors[0].Width, height: colors[0].Height);
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => new Resource();
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) => new Resource(sizeBytes: sizeBytes);
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) => new Resource(sizeBytes: sizeBytes);
    IGpuImage IGpuImageFactory.Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) => new Resource(width: width, height: height);
    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport() => throw new NotSupportedException();
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback() => new Readback(reportVersion: m_reportVersion);
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload() => new SurfaceUpload();
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) => new Resource(sizeBytes: ((ulong)data.Length));

    private sealed class Fence : IGpuSubmissionFence {
        public bool Armed { get; set; }
        public bool IsSignaled => true;

        public void Dispose() { }
        public void Wait() => Armed = false;
    }
    // Every resource kind in one: a nonzero handle for each member, the requested extent, and nothing to release.
    private sealed class Resource(uint width = 1, uint height = 1, ulong sizeBytes = 0) :
        IGpuCommandPool,
        IGpuComputePipeline,
        IGpuFramebuffer,
        IGpuImage,
        IGpuPipeline,
        IGpuRenderPass,
        IGpuShaderModule,
        IGpuStorageBuffer {
        public nint BufferHandle => 3;
        public nint CommandBufferHandle => 2;
        public nint DescriptorSetLayoutHandle => 10;
        public GpuRenderPassDescription Description { get; } = new(Colors: [new GpuColorAttachment(FinalLayout: GpuImageLayout.ShaderReadOnly, Format: GpuPixelFormat.R8G8B8A8Unorm, Load: GpuAttachmentLoad.Clear, Store: GpuAttachmentStore.Store)]);
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public IGpuRenderPass RenderPass => this;
        public GpuImageUsage Usage => GpuImageUsage.Sampled;

        nint IGpuComputePipeline.Handle => 11;
        nint IGpuPipeline.Handle => 11;
        nint IGpuShaderModule.Handle => 6;

        public uint Height => height;
        public nint ImageHandle => 4;
        public nint ImageViewHandle => 5;
        public nint LayoutHandle => 13;
        public ulong SizeBytes => sizeBytes;
        public uint Width => width;

        public void Dispose() { }
        public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged { }
        public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged { }
    }
    // Every upload lands on one fixed view handle.
    private sealed class SurfaceUpload : IGpuSurfaceUpload {
        public const nint ViewHandle = 11;

        public void Dispose() { }
        public nint Upload(IGpuDeviceContext deviceContext, ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height) => ViewHandle;
    }
    private sealed class Readback(byte reportVersion) : IGpuSurfaceReadback {
        private byte[] m_pixels = [];

        public void Dispose() { }
        public bool IsReadComplete() => true;
        public ReadOnlyMemory<byte> MapPixels() => m_pixels;
        public ReadOnlyMemory<byte> Read(IGpuDeviceContext deviceContext, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel, GpuImageLayout sourceLayout) {
            SubmitRead(
                bytesPerPixel: bytesPerPixel,
                deviceContext: deviceContext,
                format: format,
                height: height,
                sourceImageHandle: sourceImageHandle,
                sourceLayout: sourceLayout,
                width: width
            );

            return m_pixels;
        }
        public void SubmitRead(IGpuDeviceContext deviceContext, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel, GpuImageLayout sourceLayout) {
            var length = checked((int)((width * height) * bytesPerPixel));

            if (m_pixels.Length != length) {
                m_pixels = new byte[length];
            }

            if (length == 4) {
                m_pixels[0] = 0x53;
                m_pixels[1] = 0x44;
                m_pixels[2] = reportVersion;
                m_pixels[3] = reportVersion;
            }
        }
    }
}
