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
    IGpuBufferInitializationRecorder,
    IGpuCommandRecorder,
    IGpuComputeCommandPoolFactory,
    IGpuComputePipelineFactory,
    IGpuComputeRecorder,
    IGpuComputeServices,
    IGpuDescriptorAllocator,
    IGpuDeviceContext,
    IGpuImageInitializationRecorder,
    IGpuPipelineFactory,
    IGpuQueueSubmitter,
    IGpuShaderModuleFactory,
    IGpuStorageBufferFactory,
    IGpuImageFactory,
    IGpuGeometryBufferFactory,
    IGpuRenderPassFactory,
    IGpuSurfaceTransferFactory {
    private readonly byte m_reportVersion;

    /// <summary>Initializes a new instance of the <see cref="FakeGpuDevice"/> class.</summary>
    /// <param name="reportVersion">The ISA version a 1×1 readback reports.</param>
    public FakeGpuDevice(byte reportVersion) {
        m_reportVersion = reportVersion;
    }

    public long AdapterLuid => 0L;
    /// <summary>Gets or sets a hook every compute pipeline creation runs first, on whatever thread creates it — a law
    /// holds a pipeline build by blocking here.</summary>
    public Action? BeforeComputePipeline { get; set; }
    public IGpuComputeCommandPoolFactory CommandPoolFactory => this;
    public IGpuComputePipelineFactory ComputePipelineFactory => this;
    public IGpuComputeRecorder ComputeRecorder => this;
    public IGpuDescriptorAllocator DescriptorAllocator => this;
    public nint DeviceHandle => 1;
    public GpuDeviceIdentity? Identity => null;
    public IGpuImageFactory ImageFactory => this;
    public GpuMemoryProfile MemoryProfile => default;
    public IGpuQueueSubmitter QueueSubmitter => this;
    public IGpuShaderModuleFactory ShaderModuleFactory => this;
    public IGpuStorageBufferFactory StorageBufferFactory => this;
    public IGpuSurfaceTransferFactory SurfaceTransferFactory => this;
    /// <summary>Gets the number of submissions made, fenced or not.</summary>
    public int Submissions { get; private set; }

    public void WaitIdle() { }

    void IGpuBufferInitializationRecorder.ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) { }
    void IGpuCommandRecorder.BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) { }
    void IGpuCommandRecorder.BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) { }
    void IGpuCommandRecorder.BeginRenderPass(nint deviceHandle, nint commandBufferHandle, IGpuFramebuffer framebuffer) { }
    void IGpuCommandRecorder.BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) { }
    void IGpuCommandRecorder.BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) { }
    void IGpuCommandRecorder.BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) { }
    void IGpuCommandRecorder.BindIndexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) { }
    void IGpuCommandRecorder.Draw(nint deviceHandle, nint commandBufferHandle, in GpuDrawParameters parameters) { }
    void IGpuCommandRecorder.DrawIndexed(nint deviceHandle, nint commandBufferHandle, uint indexCount) { }
    void IGpuCommandRecorder.EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) { }
    void IGpuCommandRecorder.EndDebugGroup(nint deviceHandle, nint commandBufferHandle) { }
    void IGpuCommandRecorder.EndRenderPass(nint deviceHandle, nint commandBufferHandle) { }
    void IGpuCommandRecorder.PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) { }
    void IGpuCommandRecorder.SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) { }
    IGpuComputeCommandPool IGpuComputeCommandPoolFactory.Create(IGpuDeviceContext deviceContext) => new Resource();
    IGpuComputePipeline IGpuComputePipelineFactory.Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        BeforeComputePipeline?.Invoke();

        return new Resource();
    }
    void IGpuComputeRecorder.BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) { }
    void IGpuComputeRecorder.BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) { }
    void IGpuComputeRecorder.BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) { }
    void IGpuComputeRecorder.BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) { }
    void IGpuComputeRecorder.Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) { }
    void IGpuComputeRecorder.DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) { }
    void IGpuComputeRecorder.EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) { }
    void IGpuComputeRecorder.EndDebugGroup(nint deviceHandle, nint commandBufferHandle) { }
    void IGpuComputeRecorder.MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
    void IGpuComputeRecorder.PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) { }
    void IGpuComputeRecorder.TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
    void IGpuComputeRecorder.TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
    nint IGpuDescriptorAllocator.AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) => 7;
    nint IGpuDescriptorAllocator.CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes) => 8;
    nint IGpuDescriptorAllocator.CreateSampler(nint deviceHandle, GpuSamplerFilter filter) => 9;
    void IGpuDescriptorAllocator.DestroyPool(nint deviceHandle, nint poolHandle) { }
    void IGpuDescriptorAllocator.DestroySampler(nint deviceHandle, nint samplerHandle) { }
    void IGpuDescriptorAllocator.WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) { }
    void IGpuDescriptorAllocator.WriteRawBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, bool writable) { }
    void IGpuDescriptorAllocator.WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) { }
    void IGpuDescriptorAllocator.WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) { }
    void IGpuDescriptorAllocator.WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) { }
    void IGpuDescriptorAllocator.WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) { }
    void IGpuImageInitializationRecorder.ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) { }
    IGpuPipeline IGpuPipelineFactory.Create(IGpuDeviceContext deviceContext, IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, uint width, uint height) => new Resource();
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence(IGpuDeviceContext deviceContext) => new Fence();
    void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => Submissions++;
    void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        // A backend accepts only its own fence type; a counting fence reaching here is a missed unwrap.
        ((Fence)fence).Armed = true;
        Submissions++;
    }
    void IGpuQueueSubmitter.SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => Submissions++;
    IGpuRenderPass IGpuRenderPassFactory.Create(IGpuDeviceContext deviceContext, GpuRenderPassDescription description) => new Resource();
    IGpuFramebuffer IGpuRenderPassFactory.CreateFramebuffer(IGpuDeviceContext deviceContext, IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) => new Resource(width: colors[0].Width, height: colors[0].Height);
    IGpuShaderModule IGpuShaderModuleFactory.Create(IGpuDeviceContext deviceContext, GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => new Resource();
    IGpuStorageBuffer IGpuStorageBufferFactory.Create(IGpuDeviceContext deviceContext, ulong sizeBytes) => new Resource(sizeBytes: sizeBytes);
    IGpuBuffer IGpuStorageBufferFactory.CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes) => new Resource(sizeBytes: sizeBytes);
    IGpuBuffer IGpuStorageBufferFactory.CreateDeviceLocalIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) => new Resource(sizeBytes: sizeBytes);
    IGpuStorageBuffer IGpuStorageBufferFactory.CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) => new Resource(sizeBytes: sizeBytes);
    IGpuImage IGpuImageFactory.Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) => new Resource(width: width, height: height);
    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback(IGpuDeviceContext deviceContext) => new Readback(reportVersion: m_reportVersion);
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload(IGpuDeviceContext deviceContext) => new SurfaceUpload();
    IGpuBuffer IGpuGeometryBufferFactory.Create(IGpuDeviceContext deviceContext, ReadOnlySpan<byte> data, GpuBufferUsage usage) => new Resource(sizeBytes: ((ulong)data.Length));

    private sealed class Fence : IGpuSubmissionFence {
        public bool Armed { get; set; }
        public bool IsSignaled => true;

        public void Dispose() { }
        public void Wait() => Armed = false;
    }
    // Every resource kind in one: a nonzero handle for each member, the requested extent, and nothing to release.
    private sealed class Resource(uint width = 1, uint height = 1, ulong sizeBytes = 0) :
        IGpuComputeCommandPool,
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
