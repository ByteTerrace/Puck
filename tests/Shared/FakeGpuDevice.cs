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
/// fence that is not this fake's own type fails the submit. With <c>trackObjects</c>, every object a factory, the
/// bindings or the submitter creates is a <see cref="Creation"/> in <see cref="Created"/> that counts its releases, and
/// every image and device-local buffer is counted in <see cref="Memory"/> as device-local memory, so a law compares what
/// was created with what was released, and reads the bytes still held.
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
    private readonly GpuObjectNaming m_naming;
    private readonly byte m_reportVersion;

    private readonly Dictionary<nint, Creation> m_trackedHandles = [];
    private readonly Lock m_trackGate = new();

    private readonly bool m_trackObjects;

    private int m_admissions;

    // Each live pool's admission into DescriptorHeap, by pool handle, and the next handle a pool takes while a heap is
    // set and objects are not tracked, so each such pool is released by its own handle.
    private readonly Dictionary<nint, (GpuDescriptorHeapBudget Heap, GpuDescriptorAdmission Admission)> m_heapPools = [];
    private nint m_nextHeapPool = 0x10000;

    /// <summary>Initializes a new instance of the <see cref="FakeGpuDevice"/> class.</summary>
    /// <param name="reportVersion">The ISA version a 1×1 readback reports.</param>
    /// <param name="countCalls">Whether each wrapped member counts its calls into <see cref="Calls"/>.</param>
    /// <param name="holdFences">Whether a submitted fence waits for the test to complete it.</param>
    /// <param name="trackObjects">Whether every created object is tracked in <see cref="Created"/> and
    /// <see cref="Memory"/>.</param>
    /// <param name="naming">The naming every creating member hands its object to, as a backend's do, or
    /// <see langword="null"/> for <see cref="GpuObjectNaming.Off"/>.</param>
    public FakeGpuDevice(byte reportVersion = 0, bool countCalls = false, bool holdFences = false, bool trackObjects = false, GpuObjectNaming? naming = null) {
        m_countCalls = countCalls;
        m_naming = (naming ?? GpuObjectNaming.Off);
        m_holdFences = holdFences;
        m_reportVersion = reportVersion;
        m_trackObjects = trackObjects;
        Services = new GpuDeviceServices {
            Bindings = this,
            BufferFactory = this,
            CommandPoolFactory = this,
            ImageFactory = this,
            Naming = m_naming,
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

    /// <summary>Gets every object created while the device tracks objects, in creation order; read it once no creation
    /// is running.</summary>
    public List<Creation> Created { get; } = [];

    /// <summary>The native device handle the fake's tracked memory is counted under.</summary>
    public const nint DeviceHandle = 1;

    public GpuDeviceIdentity? Identity => null;
    /// <summary>Gets the fence created last.</summary>
    public Fence? LastCreatedFence { get; private set; }
    /// <summary>Gets the fence submitted last, as it reached the device.</summary>
    public IGpuSubmissionFence? LastSubmittedFence { get; private set; }

    /// <summary>Gets the device-local memory of the tracked images and device-local buffers: an image is its width times
    /// its height times four bytes, a buffer its size.</summary>
    public GpuDeviceMemoryWork Memory { get; } = new(backend: "fake");

    /// <summary>Gets or sets the memory profile the device reports; the default reports nothing.</summary>
    public GpuMemoryProfile MemoryProfile { get; set; }
    /// <summary>Gets this device as every one of its own services.</summary>
    public GpuDeviceServices Services { get; }

    /// <summary>Gets every descriptor pool created, in creation order, as its creation sized it.</summary>
    public List<GpuDescriptorPoolSizes> PoolsCreated { get; } = [];

    /// <summary>Gets or sets the list every recorded image transition is appended to, in recording order, or
    /// <see langword="null"/> (the default) to record none, so a steady-frame allocation law is unaffected.</summary>
    public List<(nint Image, GpuImageLayout Old, GpuImageLayout New)>? ImageTransitions { get; set; }
    /// <summary>Gets the number of <see cref="IGpuBindings.CanAdmit"/> calls, admitted or refused. Counted whether or not
    /// the device counts calls, and synchronized, so a harness whose pipelines build on the thread pool can read it.</summary>
    public int Admissions => Volatile.Read(location: ref m_admissions);
    /// <summary>Gets or sets the descriptor heap <see cref="IGpuBindings.CanAdmit"/> checks a candidate against, or
    /// <see langword="null"/> to admit every candidate, as a Vulkan device does. While one is set, each created pool is
    /// admitted into it, or refused with <see cref="GpuDescriptorHeapRefusalException"/>, and returns its ranges when
    /// destroyed, as a Direct3D 12 device's pools do.</summary>
    public GpuDescriptorHeapBudget? DescriptorHeap { get; set; }
    /// <summary>Gets the number of submissions made, fenced or not.</summary>
    public int Submissions { get; private set; }

    /// <summary>Returns how many times a member was called.</summary>
    /// <param name="key">The member, keyed <c>Interface.Member</c>.</param>
    /// <returns>Its call count; zero unless the device counts calls.</returns>
    public int Count(string key) => (Calls.TryGetValue(key: key, value: out var count) ? count : 0);
    public void WaitIdle() => Hit(key: "IGpuDeviceContext.WaitIdle");

    // Records one creation when the device tracks objects: a unique handle, and the device-local bytes it holds.
    private Creation? Track(string kind, ulong deviceLocalBytes = 0UL, bool deviceLocal = false) {
        if (!m_trackObjects) {
            return null;
        }

        Creation creation;

        lock (m_trackGate) {
            var number = (Created.Count + 1);

            creation = new Creation(
                deviceLocal: deviceLocal,
                gpu: this,
                handle: ((nint)(0x1000 + (0x10 * number))),
                kind: kind,
                number: number
            );
            Created.Add(item: creation);
            m_trackedHandles.Add(
                key: creation.Handle,
                value: creation
            );
        }

        if (deviceLocal) {
            _ = Memory.CountAllocated(
                allocation: creation.Handle,
                bytes: ((long)deviceLocalBytes),
                device: DeviceHandle,
                role: GpuMemoryRole.DeviceLocal
            );
        }

        return creation;
    }
    // Releases the tracked creation behind a pool or sampler handle.
    private void ReleaseHandle(nint handle) {
        Creation? creation;

        lock (m_trackGate) {
            _ = m_trackedHandles.TryGetValue(
                key: handle,
                value: out creation
            );
        }

        creation?.Release();
    }
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
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) => Hit(key: "IGpuRecorder.BindDescriptorSet");
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
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) {
        ImageTransitions?.Add(item: (imageHandle, oldLayout, newLayout));
        Hit(key: "IGpuRecorder.TransitionImageLayout");
    }
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => Hit(key: "IGpuRecorder.MemoryBarrier");
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => Hit(key: "IGpuRecorder.TransitionBuffer");
    IGpuCommandPool IGpuCommandPoolFactory.Create(in GpuObjectName name) {
        Hit(key: "IGpuCommandPoolFactory.Create");

        return Named(
            kind: GpuObjectKind.CommandPool,
            name: in name,
            resource: new Resource(
                creation: Track(kind: "command pool"),
                gpu: this
            )
        );
    }
    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name) {
        BeforeComputePipeline?.Invoke(obj: description);
        Hit(key: "IGpuPipelineFactory.Create(compute)");

        return Named(
            kind: GpuObjectKind.Pipeline,
            name: in name,
            resource: new Resource(
                creation: Track(kind: "compute pipeline"),
                gpu: this,
                groupLayouts: GroupLayoutsOf(layout: description?.Layout)
            )
        );
    }
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, in GpuObjectName name) {
        Hit(key: "IGpuPipelineFactory.Create(graphics)");

        return Named(
            kind: GpuObjectKind.Pipeline,
            name: in name,
            resource: new Resource(
                creation: Track(kind: "graphics pipeline"),
                gpu: this,
                groupLayouts: GroupLayoutsOf(layout: description?.Layout)
            )
        );
    }
    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle, in GpuObjectName name) {
        Hit(key: "IGpuBindings.AllocateSet");
        m_naming.Name(
            handle: 7,
            kind: GpuObjectKind.DescriptorSet,
            name: in name
        );

        return 7;
    }
    bool IGpuBindings.CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) {
        Hit(key: "IGpuBindings.CanAdmit");
        _ = Interlocked.Increment(location: ref m_admissions);

        if (DescriptorHeap is { } heap) {
            return heap.CanAdmit(
                owner: owner,
                pools: pools,
                refusal: out refusal
            );
        }

        refusal = string.Empty;

        return true;
    }

    long IGpuBindings.HeapReleaseRevision {
        get {
            Hit(key: "IGpuBindings.HeapReleaseRevision");

            return (DescriptorHeap?.ReleaseRevision ?? 0L);
        }
    }

    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes, in GpuObjectName name) {
        Hit(key: "IGpuBindings.CreatePool");

        GpuDescriptorAdmission? admission = null;
        var heap = DescriptorHeap;

        if (
            (heap is not null) &&
            !heap.TryAdmit(
                admission: out admission,
                owner: "descriptor pool",
                pools: [sizes],
                refusal: out var refusal
            )
        ) {
            throw new GpuDescriptorHeapRefusalException(message: refusal);
        }

        PoolsCreated.Add(item: sizes);

        var handle = (Track(kind: "descriptor pool")?.Handle ?? ((heap is null)
            ? 8
            : m_nextHeapPool++));

        if (heap is not null) {
            m_heapPools[handle] = (heap, admission!);
        }

        m_naming.Name(
            handle: handle,
            kind: GpuObjectKind.DescriptorPool,
            name: in name
        );

        return handle;
    }
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) {
        Hit(key: "IGpuBindings.CreateSampler");

        return (Track(kind: "sampler")?.Handle ?? 9);
    }
    void IGpuBindings.DestroyPool(nint poolHandle) {
        Hit(key: "IGpuBindings.DestroyPool");

        if (m_heapPools.Remove(
            key: poolHandle,
            value: out var admitted
        )) {
            admitted.Heap.Release(admission: admitted.Admission);
        }

        ReleaseHandle(handle: poolHandle);
    }
    void IGpuBindings.DestroySampler(nint samplerHandle) {
        Hit(key: "IGpuBindings.DestroySampler");
        ReleaseHandle(handle: samplerHandle);
    }
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => Hit(key: "IGpuBindings.WriteCombinedImageSampler");
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) => Hit(key: "IGpuBindings.WriteBuffer");
    void IGpuBindings.WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) => Hit(key: "IGpuBindings.WriteConstantBuffer");
    void IGpuBindings.WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => Hit(key: "IGpuBindings.WriteSampledImage");
    void IGpuBindings.WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) => Hit(key: "IGpuBindings.WriteSampler");
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => Hit(key: "IGpuBindings.WriteStorageImage");
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() {
        Hit(key: "IGpuQueueSubmitter.CreateSubmissionFence");
        LastCreatedFence = new Fence(
            creation: Track(kind: "fence"),
            gpu: this
        );

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
    IGpuRenderPass IGpuRenderPassFactory.Create(GpuRenderPassDescription description, in GpuObjectName name) {
        Hit(key: "IGpuRenderPassFactory.Create");

        return Named(
            kind: GpuObjectKind.RenderPass,
            name: in name,
            resource: new Resource(
                creation: Track(kind: "render pass"),
                gpu: this
            )
        );
    }
    IGpuFramebuffer IGpuRenderPassFactory.CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) {
        Hit(key: "IGpuRenderPassFactory.CreateFramebuffer");

        // A depth-only framebuffer takes its extent from the depth attachment.
        var extent = ((colors.Count > 0) ? colors[0] : depth);

        return new Resource(
            creation: Track(kind: "framebuffer"),
            gpu: this,
            height: (extent?.Height ?? 1U),
            width: (extent?.Width ?? 1U)
        );
    }
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        Hit(key: "IGpuShaderModuleFactory.Create");

        return new Resource(
            creation: Track(kind: "shader module"),
            gpu: this
        );
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) {
        Hit(key: "IGpuBufferFactory.CreateHostVisible");

        return Named(
            kind: GpuObjectKind.Buffer,
            name: in name,
            resource: new Resource(
                creation: Track(kind: "host-visible buffer"),
                gpu: this,
                sizeBytes: sizeBytes
            )
        );
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisibleDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) {
        Hit(key: "IGpuBufferFactory.CreateHostVisibleDeviceLocal");

        return Named(
            kind: GpuObjectKind.Buffer,
            name: in name,
            resource: new Resource(
                creation: Track(
                    deviceLocal: true,
                    deviceLocalBytes: sizeBytes,
                    kind: "host-visible device-local buffer"
                ),
                gpu: this,
                sizeBytes: sizeBytes
            )
        );
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage, in GpuObjectName name) {
        Hit(key: "IGpuBufferFactory.CreateHostVisible(data)");

        return Named(
            kind: GpuObjectKind.Buffer,
            name: in name,
            resource: new Resource(
                creation: Track(kind: "host-visible buffer"),
                gpu: this,
                sizeBytes: ((ulong)data.Length)
            )
        );
    }
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) {
        Hit(key: "IGpuBufferFactory.CreateDeviceLocal");

        return Named(
            kind: GpuObjectKind.Buffer,
            name: in name,
            resource: new Resource(
                creation: Track(
                    deviceLocal: true,
                    deviceLocalBytes: sizeBytes,
                    kind: "device-local buffer"
                ),
                gpu: this,
                sizeBytes: sizeBytes
            )
        );
    }
    IGpuImage IGpuImageFactory.Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) {
        Hit(key: "IGpuImageFactory.Create");

        return Named(
            kind: GpuObjectKind.Image,
            name: in name,
            resource: new Resource(
                creation: Track(
                    deviceLocal: true,
                    deviceLocalBytes: ((((ulong)width) * height) * 4UL),
                    kind: "image"
                ),
                gpu: this,
                height: height,
                width: width
            )
        );
    }

    // Hands a created object to the device's naming, as a backend's creating members do, under the fake's fixed handle
    // for its kind.
    private Resource Named(Resource resource, GpuObjectKind kind, in GpuObjectName name) {
        m_naming.Name(
            handle: kind switch {
                GpuObjectKind.Buffer => resource.BufferHandle,
                GpuObjectKind.CommandPool => resource.CommandBufferHandle,
                GpuObjectKind.Image => resource.ImageHandle,
                _ => ((IGpuPipeline)resource).Handle,
            },
            kind: kind,
            name: in name
        );

        return resource;
    }

    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport() => throw new NotSupportedException();
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback() => new Readback(reportVersion: m_reportVersion);
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload() => new SurfaceUpload();

    /// <summary>A submission fence. It reads signaled once submitted unless the device holds fences; a held fence reads
    /// signaled when nothing is armed or the test has completed it, as the backends' fences do.</summary>
    public sealed class Fence(FakeGpuDevice gpu, Creation? creation = null) : IGpuSubmissionFence {
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

        public void Dispose() {
            gpu.Hit(key: "IGpuSubmissionFence.Dispose");
            creation?.Release();
        }
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
    /// <summary>One object created while the device tracks objects: its kind, its one-based creation number, its unique
    /// handle (the one a descriptor pool or sampler creation returns), and how often it was released.</summary>
    public sealed class Creation(FakeGpuDevice gpu, string kind, int number, nint handle, bool deviceLocal) {
        /// <summary>Gets how often the object was released.</summary>
        public int DisposeCount { get; private set; }
        /// <summary>Gets the object's unique handle.</summary>
        public nint Handle => handle;
        /// <summary>Gets the object's kind.</summary>
        public string Kind => kind;
        /// <summary>Gets the object's one-based creation number.</summary>
        public int Number => number;

        internal void Release() {
            lock (gpu.m_trackGate) {
                DisposeCount++;
            }

            if (deviceLocal) {
                _ = gpu.Memory.CountReleased(
                    allocation: handle,
                    device: DeviceHandle
                );
            }
        }

        public override string ToString() => $"#{number} {kind} (released {DisposeCount}x)";
    }

    // Every resource kind in one: a nonzero handle for each member, the requested extent, and, when the device tracks
    // objects, the creation its disposal releases.
    // The set layout a pipeline created from a layout allocates each group's sets against: 20 plus the group's ordinal
    // where it binds one, and zero where it binds none; none for a pipeline created without a layout.
    private static IReadOnlyList<nint> GroupLayoutsOf(GpuPipelineLayoutDescription? layout) => ((layout is null)
        ? []
        : [.. Enumerable.Range(
            count: ((int)(layout.Groups.Max(selector: static group => group.Ordinal) + 1)),
            start: 0
        ).Select(selector: ordinal => (layout.Groups.Any(predicate: group => (group.Ordinal == ordinal))
            ? ((nint)(20 + ordinal))
            : 0))]);

    private sealed class Resource(FakeGpuDevice gpu, Creation? creation = null, uint width = 1, uint height = 1, ulong sizeBytes = 0, IReadOnlyList<nint>? groupLayouts = null) :
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
        public IReadOnlyList<nint> GroupLayoutHandles => (groupLayouts ?? []);
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

        public void Dispose() => creation?.Release();
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
