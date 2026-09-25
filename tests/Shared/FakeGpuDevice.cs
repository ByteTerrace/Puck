using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>
/// A device-free stand-in for every neutral GPU service: each call does nothing, and every handle is a fixed nonzero
/// value. It lets a node run its whole CPU path — recording, submission, fence waits, uploads — so the GPU work it counts
/// can be checked exactly without a device. By default a fence reads signaled once submitted, so every submission
/// completes as soon as a node polls it.
/// <para>
/// A 1×1 readback returns the SDF ISA report of the version given at construction, so an SDF engine passes its shader-set
/// handshake; any other readback returns zeroed pixels. Nothing here allocates once a node has created its resources.
/// </para>
/// <para>
/// Two modes serve the laws over the counting wrappers themselves. With <c>countCalls</c>, each member of a wrapped
/// interface adds one to its own entry in <see cref="Calls"/>, keyed <c>Interface.Member</c>, and allocates nothing once
/// that key exists; the dictionary is not synchronized, so a harness whose node builds on the thread pool leaves it off.
/// With <c>holdFences</c>, a submitted fence stays unsignaled until the test sets its <see cref="Fence.Completed"/>, and
/// a fence submitted again before its wait is refused. The submitter arms a fence by casting, as both backends do, so a
/// fence that is not this fake's own type fails the submit.
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
    private readonly bool m_countCalls;
    private readonly bool m_holdFences;
    private readonly byte m_reportVersion;

    /// <summary>Initializes a new instance of the <see cref="FakeGpuDevice"/> class.</summary>
    /// <param name="reportVersion">The ISA version a 1×1 readback reports.</param>
    /// <param name="countCalls">Whether each wrapped member counts its calls into <see cref="Calls"/>.</param>
    /// <param name="holdFences">Whether a submitted fence waits for the test to complete it.</param>
    public FakeGpuDevice(byte reportVersion = 0, bool countCalls = false, bool holdFences = false) {
        m_countCalls = countCalls;
        m_holdFences = holdFences;
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
    /// <summary>Gets or sets a hook every compute pipeline creation runs first, with the pipeline's description, on
    /// whatever thread creates it — a law holds a pipeline build by blocking here, and counts or orders creations.</summary>
    public Action<GpuComputePipelineDescription>? BeforeComputePipeline { get; set; }

    /// <summary>Gets each wrapped member's call count, keyed <c>Interface.Member</c>; empty unless the device counts
    /// calls.</summary>
    public Dictionary<string, int> Calls { get; } = new(comparer: StringComparer.Ordinal);

    public GpuDeviceCapabilities? Capabilities => null;
    public GpuDeviceIdentity? Identity => null;
    /// <summary>Gets the fence created last.</summary>
    public Fence? LastCreatedFence { get; private set; }
    /// <summary>Gets the fence submitted last, as it reached the device.</summary>
    public IGpuSubmissionFence? LastSubmittedFence { get; private set; }
    public GpuMemoryProfile MemoryProfile => default;
    /// <summary>Gets this device as every one of its own services.</summary>
    public GpuDeviceServices Services { get; }

    /// <summary>Gets every descriptor pool created, in creation order, as its creation sized it.</summary>
    public List<GpuDescriptorPoolSizes> PoolsCreated { get; } = [];

    /// <summary>Gets the number of submissions made, fenced or not.</summary>
    public int Submissions { get; private set; }

    /// <summary>Returns how many times a member was called.</summary>
    /// <param name="key">The member, keyed <c>Interface.Member</c>.</param>
    /// <returns>Its call count; zero unless the device counts calls.</returns>
    public int Count(string key) => (Calls.TryGetValue(key: key, value: out var count) ? count : 0);
    public void WaitIdle() => Hit(key: "IGpuDeviceContext.WaitIdle");

    private void Hit(string key) {
        if (m_countCalls) {
            CollectionsMarshal.GetValueRefOrAddDefault(dictionary: Calls, key: key, exists: out _)++;
        }
    }

    void IGpuRecorder.BeginCommandBuffer(nint commandBufferHandle) => Hit(key: "IGpuRecorder.BeginCommandBuffer");
    void IGpuRecorder.EndCommandBuffer(nint commandBufferHandle) => Hit(key: "IGpuRecorder.EndCommandBuffer");
    void IGpuRecorder.BeginDebugGroup(nint commandBufferHandle, string label) => Hit(key: "IGpuRecorder.BeginDebugGroup");
    void IGpuRecorder.EndDebugGroup(nint commandBufferHandle) => Hit(key: "IGpuRecorder.EndDebugGroup");
    void IGpuRecorder.BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area) => Hit(key: "IGpuRecorder.BeginRenderPass");
    void IGpuRecorder.EndRenderPass(nint commandBufferHandle) => Hit(key: "IGpuRecorder.EndRenderPass");
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => Hit(key: "IGpuRecorder.BindPipeline");
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle) => Hit(key: "IGpuRecorder.BindDescriptorSet");
    void IGpuRecorder.PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => Hit(key: "IGpuRecorder.PushConstants");
    void IGpuRecorder.BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) => Hit(key: "IGpuRecorder.BindVertexBuffer");
    void IGpuRecorder.BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) => Hit(key: "IGpuRecorder.BindIndexBuffer");
    void IGpuRecorder.SetScissor(nint commandBufferHandle, GpuPixelRect rect) => Hit(key: "IGpuRecorder.SetScissor");
    void IGpuRecorder.Draw(nint commandBufferHandle, in GpuDrawParameters parameters) => Hit(key: "IGpuRecorder.Draw");
    void IGpuRecorder.DrawIndexed(nint commandBufferHandle, uint indexCount) => Hit(key: "IGpuRecorder.DrawIndexed");
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) => Hit(key: "IGpuRecorder.Dispatch");
    void IGpuRecorder.DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) => Hit(key: "IGpuRecorder.DispatchIndirect");
    void IGpuRecorder.ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) => Hit(key: "IGpuRecorder.ClearStorageImage");
    void IGpuRecorder.ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) => Hit(key: "IGpuRecorder.ClearStorageBuffer");
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => Hit(key: "IGpuRecorder.TransitionImageLayout");
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => Hit(key: "IGpuRecorder.MemoryBarrier");
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => Hit(key: "IGpuRecorder.TransitionBuffer");
    IGpuCommandPool IGpuCommandPoolFactory.Create() {
        Hit(key: "IGpuCommandPoolFactory.Create");

        return new Resource(gpu: this);
    }
    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        BeforeComputePipeline?.Invoke(obj: description);
        Hit(key: "IGpuPipelineFactory.Create(compute)");

        return new Resource(gpu: this);
    }
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) {
        Hit(key: "IGpuPipelineFactory.Create(graphics)");

        return new Resource(gpu: this);
    }
    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) {
        Hit(key: "IGpuBindings.AllocateSet");

        return 7;
    }
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes) {
        Hit(key: "IGpuBindings.CreatePool");
        PoolsCreated.Add(item: sizes);

        return 8;
    }
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) {
        Hit(key: "IGpuBindings.CreateSampler");

        return 9;
    }
    void IGpuBindings.DestroyPool(nint poolHandle) => Hit(key: "IGpuBindings.DestroyPool");
    void IGpuBindings.DestroySampler(nint samplerHandle) => Hit(key: "IGpuBindings.DestroySampler");
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => Hit(key: "IGpuBindings.WriteCombinedImageSampler");
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) => Hit(key: "IGpuBindings.WriteBuffer");
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => Hit(key: "IGpuBindings.WriteStorageImage");
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() {
        Hit(key: "IGpuQueueSubmitter.CreateSubmissionFence");
        LastCreatedFence = new Fence(gpu: this);

        return LastCreatedFence;
    }
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles) {
        Hit(key: "IGpuQueueSubmitter.Submit");
        Submissions++;
    }
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        Hit(key: "IGpuQueueSubmitter.Submit(fence)");
        LastSubmittedFence = fence;
        // A backend accepts only its own fence type; a counting fence reaching here is a missed unwrap.
        ((Fence)fence).Arm();
        Submissions++;
    }
    void IGpuQueueSubmitter.SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        Hit(key: "IGpuQueueSubmitter.SubmitAndWait");
        Submissions++;
    }
    IGpuRenderPass IGpuRenderPassFactory.Create(GpuRenderPassDescription description) {
        Hit(key: "IGpuRenderPassFactory.Create");

        return new Resource(gpu: this);
    }
    IGpuFramebuffer IGpuRenderPassFactory.CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) {
        Hit(key: "IGpuRenderPassFactory.CreateFramebuffer");

        // A depth-only framebuffer takes its extent from the depth attachment.
        var extent = ((colors.Count > 0) ? colors[0] : depth);

        return new Resource(
            gpu: this,
            height: (extent?.Height ?? 1U),
            width: (extent?.Width ?? 1U)
        );
    }
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        Hit(key: "IGpuShaderModuleFactory.Create");

        return new Resource(gpu: this);
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) {
        Hit(key: "IGpuBufferFactory.CreateHostVisible");

        return new Resource(
            gpu: this,
            sizeBytes: sizeBytes
        );
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) {
        Hit(key: "IGpuBufferFactory.CreateHostVisible(data)");

        return new Resource(
            gpu: this,
            sizeBytes: ((ulong)data.Length)
        );
    }
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) {
        Hit(key: "IGpuBufferFactory.CreateDeviceLocal");

        return new Resource(
            gpu: this,
            sizeBytes: sizeBytes
        );
    }
    IGpuImage IGpuImageFactory.Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        Hit(key: "IGpuImageFactory.Create");

        return new Resource(
            gpu: this,
            height: height,
            width: width
        );
    }
    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport() => throw new NotSupportedException();
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback() => new Readback(reportVersion: m_reportVersion);
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload() => new SurfaceUpload();

    /// <summary>A submission fence. It reads signaled once submitted unless the device holds fences; a held fence reads
    /// signaled when nothing is armed or the test has completed it, as the backends' fences do.</summary>
    public sealed class Fence(FakeGpuDevice gpu) : IGpuSubmissionFence {
        /// <summary>Gets whether a submission is outstanding on the fence.</summary>
        public bool Armed { get; private set; }
        /// <summary>Gets or sets whether a held fence's outstanding submission has completed.</summary>
        public bool Completed { get; set; }
        public bool IsSignaled {
            get {
                gpu.Hit(key: "IGpuSubmissionFence.IsSignaled");

                return (!gpu.m_holdFences || !Armed || Completed);
            }
        }

        public void Dispose() => gpu.Hit(key: "IGpuSubmissionFence.Dispose");
        public void Wait() {
            gpu.Hit(key: "IGpuSubmissionFence.Wait");
            Armed = false;
            Completed = false;
        }

        internal void Arm() {
            if (
                gpu.m_holdFences &&
                Armed
            ) {
                throw new InvalidOperationException(message: "A submission is already outstanding on this fence.");
            }

            Armed = true;
            Completed = false;
        }
    }

    // Every resource kind in one: a nonzero handle for each member, the requested extent, and nothing to release.
    private sealed class Resource(FakeGpuDevice gpu, uint width = 1, uint height = 1, ulong sizeBytes = 0) :
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
        public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged => gpu.Hit(key: "IGpuStorageBuffer.Write");
        public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged => gpu.Hit(key: "IGpuStorageBuffer.Write(offset)");
    }
    // Every upload lands on one fixed view handle.
    private sealed class SurfaceUpload : IGpuSurfaceUpload {
        public const nint ViewHandle = 11;

        public void Dispose() { }
        public nint Upload(ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height) => ViewHandle;
    }
    private sealed class Readback(byte reportVersion) : IGpuSurfaceReadback {
        private byte[] m_pixels = [];

        public void Dispose() { }
        public ReadOnlyMemory<byte> Read(nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel, GpuImageLayout sourceLayout) {
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

            return m_pixels;
        }
    }
}
