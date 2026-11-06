using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>
/// A device-free GPU that models memory for the one kernel whose effect a host law can predict: the region copy,
/// <c>region-copy.comp</c>, which every staged <see cref="GpuRegion"/> records. Every buffer is backed by bytes and
/// carries its own handle; host writes land in those bytes and are tallied per buffer; descriptor sets remember which
/// buffer each binding names; and a dispatch recorded while the pipeline built from <see cref="RegionCopyBytecode"/> is
/// bound runs that kernel's copy at record time: binding 0 holds requests, binding 2 source data, and binding 1 the
/// destination. The seven-word header states count, runs, source/destination bases, row width and source/destination
/// strides. The run table maps thread ordinal to logical word (a linear search here; the kernel uses binary search).
/// A zero row width reads packed source data by ordinal; a nonzero width maps the logical row and column through each
/// buffer's stride. Both modes copy uints bit-exactly; packed regions alias their request and source-data bindings. The
/// dispatch must carry exactly the groups the count needs, and nothing pushes constants to the copy. The copy runs when it
/// is recorded, which is its execution order only when the command buffers are submitted in the order they were recorded,
/// and does not model concurrent execution. A buffer clear zeroes its bytes, a copy copies them, and a readback reads them. A disposed buffer is forgotten. Everything else is <see cref="FakeGpuDevice"/>.
/// <para>Shader modules and pipelines are created on the thread pool, several at once
/// (<c>GpuPassPipelineCache.BuildConcurrency</c>), so handles come from an interlocked counter and the copy kernel is
/// identified by the handles of the modules built from its bytecode and the pipelines built from those modules, each a
/// concurrent set, never by one shared field a concurrent build could overwrite.</para>
/// </summary>
internal sealed class UploadModelGpu :
    IGpuDeviceContext,
    IGpuPipelineFactory,
    IGpuRecorder,
    IGpuBindings,
    IGpuShaderModuleFactory,
    IGpuBufferFactory,
    IGpuQueueSubmitter,
    IGpuCommandPoolFactory {
    /// <summary>The first bytecode byte that marks the region-copy kernel.</summary>
    public const byte RegionCopyBytecode = 0xF7;

    private readonly Dictionary<(nint Set, uint Binding), nint> m_bindings = [];
    private readonly ConcurrentDictionary<nint, MemoryBuffer> m_buffers = new();
    private readonly List<UploadModelBufferBarrier> m_bufferBarriers = [];

    private readonly FakeGpuDevice m_inner;

    private readonly ConcurrentDictionary<nint, byte> m_uploadModules = new();
    private readonly ConcurrentDictionary<nint, byte> m_uploadPipelines = new();
    private readonly ConcurrentDictionary<nint, string> m_pipelineNames = new();
    // The command buffers recorded since a barrier whose first scope holds the compute stage, which orders every earlier
    // compute read of a staged destination before a copy writes it, and the buffers a transition from the compute stage
    // orders the same way in one command buffer; a copy recorded under neither is refused.
    private readonly HashSet<nint> m_readsOrdered = [];
    private readonly HashSet<(nint CommandBuffer, nint Buffer)> m_bufferReadsOrdered = [];
    private readonly nint[] m_boundSets = new nint[8];

    private nint m_boundPipeline;
    private nint m_boundSet;

    /// <summary>Gets the pipeline the most recent bind selected.</summary>
    public string BoundPipelineName => m_pipelineNames[m_boundPipeline];

    private long m_nextHandle = 0x1000;

    /// <summary>Initializes a new instance of the <see cref="UploadModelGpu"/> class.</summary>
    public UploadModelGpu() {
        m_inner = new FakeGpuDevice();
        Services = new GpuDeviceServices {
            Bindings = this,
            BufferFactory = this,
            CommandPoolFactory = this,
            ImageFactory = m_inner.Services.ImageFactory,
            PipelineFactory = this,
            QueueSubmitter = this,
            Recorder = this,
            RenderPassFactory = m_inner.Services.RenderPassFactory,
            ShaderModuleFactory = this,
            SurfaceTransferFactory = m_inner.Services.SurfaceTransferFactory,
        };
    }

    /// <summary>Gets or sets the descriptor heap every pool is admitted into, or <see langword="null"/> for none; the heap
    /// of the wrapped <see cref="FakeGpuDevice"/>.</summary>
    public GpuDescriptorHeapBudget? DescriptorHeap {
        get => m_inner.DescriptorHeap;
        set => m_inner.DescriptorHeap = value;
    }
    /// <summary>Gets every descriptor pool created, in creation order, as its creation sized it.</summary>
    public IReadOnlyList<GpuDescriptorPoolSizes> PoolsCreated => m_inner.PoolsCreated;
    public long AdapterLuid => 0L;
    public GpuDeviceCapabilities? Capabilities => null;
    public GpuDeviceIdentity? Identity => null;
    /// <summary>Gets or sets the memory profile the device reports; the default reports nothing, which stages every
    /// region.</summary>
    public GpuMemoryProfile MemoryProfile { get; set; }
    /// <summary>Gets or sets the gate every compute pipeline named <see cref="ComputePipelineGateName"/> waits on before it
    /// is created, or <see langword="null"/> for none. Pipelines build on pool threads, never on the frame thread.</summary>
    public ManualResetEventSlim? ComputePipelineGate { get; set; }
    /// <summary>Gets or sets the name of the compute pipeline <see cref="ComputePipelineGate"/> holds.</summary>
    public string? ComputePipelineGateName { get; set; }
    /// <summary>Gets the model's buffers, bindings, pipelines, shader modules and recorder, and the
    /// <see cref="FakeGpuDevice"/>'s other services.</summary>
    public GpuDeviceServices Services { get; }
    /// <summary>Gets the buffer descriptors written into sets since the last <see cref="ResetTallies"/>.</summary>
    public int BufferDescriptorWrites { get; private set; }
    /// <summary>Gets the table-upload copies recorded since the last <see cref="ResetTallies"/>.</summary>
    public int UploadCopies { get; private set; }
    /// <summary>Gets the raw buffer-copy bytes recorded since the last tally reset.</summary>
    public ulong BufferCopyBytes { get; private set; }
    /// <summary>Gets every buffer barrier recorded, in recording order, with the accesses and stages it names.</summary>
    public IReadOnlyList<UploadModelBufferBarrier> BufferBarriers => m_bufferBarriers;

    /// <summary>Gets the bytes of the one device-local buffer of <paramref name="sizeBytes"/>.</summary>
    /// <param name="sizeBytes">The buffer's size; exactly one device-local buffer must have it.</param>
    /// <param name="part">An optional object-name part distinguishing equally sized tables.</param>
    /// <returns>The buffer's current contents.</returns>
    public byte[] DeviceLocal(ulong sizeBytes, string? part = null) => Single(
        hostVisible: false,
        part: part,
        sizeBytes: sizeBytes
    ).Memory;

    /// <summary>Gets the host-visible buffers created in the device-local aperture
    /// (<see cref="IGpuBufferFactory.CreateHostVisibleDeviceLocal"/>) and still live.</summary>
    public int ApertureBuffers => m_buffers.Values.Count(predicate: static buffer => buffer.Aperture);
    /// <summary>Gets the bytes of every live buffer, at each buffer's created size.</summary>
    public ulong BufferBytes => m_buffers.Values.Aggregate(
        func: static (total, buffer) => (total + buffer.SizeBytes),
        seed: 0UL
    );
    /// <summary>Gets the host-visible buffers created in host memory and still live.</summary>
    public int HostBuffers => m_buffers.Values.Count(predicate: static buffer => (buffer.HostVisible && !buffer.Aperture));

    /// <summary>Gets the bytes of the one host-visible buffer of <paramref name="sizeBytes"/>.</summary>
    /// <param name="sizeBytes">The buffer's size; exactly one host-visible buffer must have it.</param>
    /// <returns>The buffer's current contents.</returns>
    public byte[] HostVisible(ulong sizeBytes) => Single(
        hostVisible: true,
        sizeBytes: sizeBytes
    ).Memory;
    /// <summary>Returns the set most recently bound at a group, or zero before one was.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The set's handle.</returns>
    public nint BoundSet(uint group) => m_boundSets[group];

    /// <summary>Gets or sets the list every set bind is appended to, in recording order, as its group and set, or
    /// <see langword="null"/>, the default, to record none.</summary>
    public List<(uint Group, nint Set)>? SetBinds { get; set; }

    /// <summary>Gets or sets how many fenced submissions pass before the next one throws before any command reaches the
    /// queue, or -1, the default, for none.</summary>
    public int FencedSubmissionsBeforeRefusal { get; set; } = -1;

    /// <summary>Returns the handle of the buffer a set's binding names.</summary>
    /// <param name="set">The set.</param>
    /// <param name="binding">The binding.</param>
    /// <returns>The buffer's handle.</returns>
    public nint BufferAt(nint set, uint binding) => m_bindings[(set, binding)];
    /// <summary>Gets the bytes of the live buffer whose handle is <paramref name="bufferHandle"/>.</summary>
    /// <param name="bufferHandle">The buffer's handle.</param>
    /// <returns>The buffer's current contents.</returns>
    public byte[] Memory(nint bufferHandle) => m_buffers[bufferHandle].Memory;
    /// <summary>Gets the host-visible bytes written since the last <see cref="ResetTallies"/>, per buffer size, for
    /// every storage buffer that received any; uniform blocks are <see cref="BlockBytes"/>'s.</summary>
    /// <returns>Each written buffer's size and the bytes written to it.</returns>
    public (ulong SizeBytes, long Written)[] HostWrites() => [.. m_buffers.Values
        .Where(predicate: buffer => (!buffer.Uniform && (buffer.Written > 0L)))
        .Select(selector: buffer => (buffer.SizeBytes, buffer.Written))];
    /// <summary>Gets the host-visible bytes written to storage buffers since the last <see cref="ResetTallies"/>.</summary>
    /// <returns>The byte count.</returns>
    public long HostBytes() => m_buffers.Values.Where(predicate: static buffer => !buffer.Uniform).Sum(selector: buffer => buffer.Written);
    /// <summary>Gets the bytes written to uniform blocks since the last <see cref="ResetTallies"/>.</summary>
    /// <returns>The byte count.</returns>
    public long BlockBytes() => m_buffers.Values.Where(predicate: static buffer => buffer.Uniform).Sum(selector: buffer => buffer.Written);
    /// <summary>Zeroes the host-write and upload-copy tallies.</summary>
    public void ResetTallies() {
        foreach (var buffer in m_buffers.Values) {
            buffer.Written = 0L;
        }

        UploadCopies = 0;
        BufferCopyBytes = 0;
        BufferDescriptorWrites = 0;
    }
    public void WaitIdle() { }

    // A pipeline created from groups carries a set layout per group ordinal, as a backend's does.
    private Handles PipelineOf(GpuPipelineLayoutDescription? layout) {
        var groups = new nint[((layout is { Groups.Count: > 0 })
            ? (layout.Groups.Max(selector: static group => ((int)group.Ordinal)) + 1)
            : 0)];

        for (var index = 0; (index < groups.Length); index++) {
            groups[index] = NextHandle();
        }

        return new Handles(handle: NextHandle(), layout: NextHandle(), setLayout: NextHandle(), groups: groups);
    }

    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name) {
        // A held gate is a driver compiling on a cold cache: the creation waits in the build's pool thread until the
        // harness opens it, so the frames produced meanwhile are the ones a slow build leaves to the installed graph.
        if ((ComputePipelineGate is { } gate) && string.Equals(a: description.Name, b: ComputePipelineGateName, comparisonType: StringComparison.Ordinal)) {
            _ = gate.Wait(timeout: TimeSpan.FromSeconds(value: 60));
        }
        var pipeline = PipelineOf(layout: description.Layout);

        m_pipelineNames[pipeline.Handle] = description.Name;

        if (m_uploadModules.ContainsKey(key: computeShaderModule.Handle)) {
            _ = m_uploadPipelines.TryAdd(
                key: pipeline.Handle,
                value: 0
            );
        }

        return pipeline;
    }
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        var module = new Handles(handle: NextHandle(), layout: 0, setLayout: 0);

        if (
            !bytecode.IsEmpty &&
            (bytecode.Span[0] == RegionCopyBytecode)
        ) {
            _ = m_uploadModules.TryAdd(
                key: module.Handle,
                value: 0
            );
        }

        return module;
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) => Buffer(hostVisible: true, sizeBytes: sizeBytes, uniform: usage.HasFlag(flag: GpuBufferUsage.Uniform));
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisibleDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) => Buffer(aperture: true, hostVisible: true, sizeBytes: sizeBytes, uniform: usage.HasFlag(flag: GpuBufferUsage.Uniform));
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) => Buffer(hostVisible: false, sizeBytes: sizeBytes, part: name.Part);
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage, in GpuObjectName name) => throw new NotSupportedException();
    IGpuReadbackBuffer IGpuBufferFactory.CreateReadback(ulong sizeBytes, in GpuObjectName name) => Buffer(hostVisible: true, sizeBytes: sizeBytes);
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, in GpuObjectName name) =>
        PipelineOf(layout: description.Layout);

    long IGpuBindings.HeapReleaseRevision => m_inner.Services.Bindings.HeapReleaseRevision;

    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle, in GpuObjectName name) => NextHandle();
    bool IGpuBindings.CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) => m_inner.Services.Bindings.CanAdmit(owner: owner, pools: pools, refusal: out refusal);
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes, in GpuObjectName name) => m_inner.Services.Bindings.CreatePool(name: name, sizes: sizes);
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => m_inner.Services.Bindings.CreateSampler(filter: filter);
    void IGpuBindings.DestroyPool(nint poolHandle) { }
    void IGpuBindings.DestroySampler(nint samplerHandle) { }
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) {
        m_bindings[(descriptorSetHandle, binding)] = bufferHandle;
        BufferDescriptorWrites++;
    }
    void IGpuBindings.WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) => m_bindings[(descriptorSetHandle, binding)] = bufferHandle;
    void IGpuBindings.WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) { }
    void IGpuBindings.WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) { }
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) { }
    void IGpuRecorder.BeginCommandBuffer(nint commandBufferHandle) {
        _ = m_readsOrdered.Remove(item: commandBufferHandle);
        _ = m_bufferReadsOrdered.RemoveWhere(match: entry => (entry.CommandBuffer == commandBufferHandle));

    }
    void IGpuRecorder.EndCommandBuffer(nint commandBufferHandle) { }
    void IGpuRecorder.BeginDebugGroup(nint commandBufferHandle, string label) { }
    void IGpuRecorder.EndDebugGroup(nint commandBufferHandle) { }
    void IGpuRecorder.BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area) { }
    void IGpuRecorder.EndRenderPass(nint commandBufferHandle) { }
    void IGpuRecorder.BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) { }
    void IGpuRecorder.BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) { }
    void IGpuRecorder.SetScissor(nint commandBufferHandle, GpuPixelRect rect) { }
    void IGpuRecorder.Draw(nint commandBufferHandle, in GpuDrawParameters parameters) { }
    void IGpuRecorder.DrawIndexed(nint commandBufferHandle, uint indexCount) { }
    void IGpuRecorder.DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) { }

    // The model rasterizes nothing, so a counted indirect draw records nothing, as Draw does; every raster pass records one
    // (SdfMeshOcclusion.RecordDraws) whatever the device counts natively.
    bool IGpuRecorder.CanDrawIndirectCount => false;

    void IGpuRecorder.DrawIndirectCount(nint commandBufferHandle, nint pipelineLayoutHandle, nint argumentBufferHandle, ulong argumentBufferOffset, nint countBufferHandle, ulong countBufferOffset, uint maxDrawCount) { }
    void IGpuRecorder.ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) { }
    void IGpuRecorder.ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
        if (m_buffers.TryGetValue(key: bufferHandle, value: out var buffer)) {
            buffer.Memory.AsSpan(length: checked((int)sizeBytes), start: 0).Clear();
        }
    }
    void IGpuRecorder.CopyImage(nint commandBufferHandle, nint sourceImageHandle, nint destinationImageHandle, uint width, uint height) { }
    void IGpuRecorder.CopyBuffer(nint commandBufferHandle, nint sourceBufferHandle, nint destinationBufferHandle, ulong sizeBytes, ulong sourceOffsetBytes, ulong destinationOffsetBytes) {
        m_buffers[sourceBufferHandle].Memory.AsSpan(length: checked((int)sizeBytes), start: checked((int)sourceOffsetBytes))
            .CopyTo(destination: m_buffers[destinationBufferHandle].Memory.AsSpan(start: checked((int)destinationOffsetBytes)));
        BufferCopyBytes += sizeBytes;
    }
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) { }
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) {
        m_bufferBarriers.Add(item: new UploadModelBufferBarrier(
            Buffer: bufferHandle,
            DestinationAccess: destinationAccessMask,
            DestinationStages: destinationStageMask,
            SourceAccess: sourceAccessMask,
            SourceStages: sourceStageMask
        ));

        if (sourceStageMask.HasFlag(flag: GpuStage.ComputeShader)) {
            _ = m_bufferReadsOrdered.Add(item: (commandBufferHandle, bufferHandle));
        }
    }
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) {
        if (sourceStageMask.HasFlag(flag: GpuStage.ComputeShader)) {
            _ = m_readsOrdered.Add(item: commandBufferHandle);
        }
    }
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) {
        m_boundSet = descriptorSetHandle;
        m_boundSets[group] = descriptorSetHandle;
        SetBinds?.Add(item: (group, descriptorSetHandle));
    }
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => m_boundPipeline = pipelineHandle;
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        if (!m_uploadPipelines.ContainsKey(key: m_boundPipeline)) {
            return;
        }
        if (
            !m_readsOrdered.Contains(item: commandBufferHandle) &&
            !m_bufferReadsOrdered.Contains(item: (commandBufferHandle, m_bindings[(m_boundSet, 1u)]))
        ) {
            throw new InvalidOperationException(message: "A region copy was recorded with no barrier ordering the earlier compute reads of its destination before it.");
        }

        var request = MemoryMarshal.Cast<byte, uint>(span: m_buffers[m_bindings[(m_boundSet, GpuRegion.CopyRequestBinding)]].Memory.AsSpan());
        var source = MemoryMarshal.Cast<byte, uint>(span: m_buffers[m_bindings[(m_boundSet, GpuRegion.CopySourceBinding)]].Memory.AsSpan());
        var destination = MemoryMarshal.Cast<byte, uint>(span: m_buffers[m_bindings[(m_boundSet, 1u)]].Memory.AsSpan());

        var (count, runCount, blockBase, destinationBase) = (request[0], request[1], request[2], request[3]);
        const uint Header = GpuRegion.CopyHeaderWords;

        if ((groupCountX, groupCountY) != GpuRegion.CopyGroups(count: count)) {
            throw new InvalidOperationException(message: $"A copy of {count} words dispatched {groupCountX}×{groupCountY} groups.");
        }

        // The kernel's thread t is its row times CopyRowThreads plus its column, and copies every word from t in steps of
        // the dispatch's threads, so together they copy each word below the count once.
        for (var thread = 0u; (thread < count); thread++) {
            var run = (runCount - 1u);

            while (request[((int)((Header + (run * 2u)) + 1u))] > thread) {
                run--;
            }

            var word = (request[((int)(Header + (run * 2u)))] + (thread - request[((int)((Header + (run * 2u)) + 1u))]));
            var sourceWord = (blockBase + thread);
            var destinationWord = (destinationBase + word);
            if (request[4] != 0) {
                var row = (word / request[4]);
                var column = (word % request[4]);
                sourceWord = (blockBase + (row * request[5]) + column);
                destinationWord = (destinationBase + (row * request[6]) + column);
            }

            destination[((int)destinationWord)] = source[((int)sourceWord)];
        }

        UploadCopies++;
    }
    void IGpuRecorder.PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        if (m_uploadPipelines.ContainsKey(key: m_boundPipeline)) {
            throw new InvalidOperationException(message: "The region copy takes no push constants.");
        }
    }

    private MemoryBuffer Buffer(bool hostVisible, ulong sizeBytes, bool aperture = false, bool uniform = false, string? part = null) {
        var buffer = new MemoryBuffer(
            aperture: aperture,
            handle: NextHandle(),
            hostVisible: hostVisible,
            owner: m_buffers,
            part: part,
            sizeBytes: sizeBytes,
            uniform: uniform
        );

        m_buffers[buffer.BufferHandle] = buffer;

        return buffer;
    }
    private nint NextHandle() => ((nint)Interlocked.Increment(location: ref m_nextHandle));
    private MemoryBuffer Single(bool hostVisible, ulong sizeBytes, string? part = null) => m_buffers.Values.Single(predicate: buffer =>
        (!buffer.Uniform &&
        (buffer.HostVisible == hostVisible) &&
        (buffer.SizeBytes == sizeBytes) && ((part is null) || (buffer.Part == part)))
    );

    void IGpuQueueSubmitter.AddExternalWait(GpuExternalWait wait) => m_inner.Services.QueueSubmitter.AddExternalWait(wait: wait);
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() => m_inner.Services.QueueSubmitter.CreateSubmissionFence();
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles) {
        m_inner.Services.QueueSubmitter.Submit(commandBufferHandles: commandBufferHandles);
    }
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        if (FencedSubmissionsBeforeRefusal == 0) {
            FencedSubmissionsBeforeRefusal = -1;
            throw new InvalidOperationException(message: "Injected submission refusal.");
        }
        if (FencedSubmissionsBeforeRefusal > 0) {
            FencedSubmissionsBeforeRefusal--;
        }
        m_inner.Services.QueueSubmitter.Submit(
            commandBufferHandles: commandBufferHandles,
            fence: fence
        );
    }
    void IGpuQueueSubmitter.SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        m_inner.Services.QueueSubmitter.SubmitAndWait(commandBufferHandles: commandBufferHandles);
    }
    void IGpuQueueSubmitter.BeginBatch() => m_inner.Services.QueueSubmitter.BeginBatch();
    void IGpuQueueSubmitter.FlushBatch() => m_inner.Services.QueueSubmitter.FlushBatch();

    IGpuCommandPool IGpuCommandPoolFactory.Create(in GpuObjectName name) => new CommandPool(
        handle: NextHandle(),
        inner: m_inner.Services.CommandPoolFactory.Create(name: name)
    );

    // A command pool of the wrapped device with a command buffer handle of its own, so the model tells a submission's
    // command buffers apart.
    private sealed class CommandPool(nint handle, IGpuCommandPool inner) : IGpuCommandPool {
        public nint CommandBufferHandle => handle;

        public void Dispose() => inner.Dispose();
    }
    // A shader module or a compute or graphics pipeline: its own handle, plus the layout handles a pipeline carries.
    private sealed class Handles(nint handle, nint layout, nint setLayout, nint[]? groups = null) : IGpuComputePipeline, IGpuPipeline, IGpuShaderModule {
        public nint DescriptorSetLayoutHandle => setLayout;
        public IReadOnlyList<nint> GroupLayoutHandles => (groups ?? []);
        public nint Handle => handle;
        public nint LayoutHandle => layout;

        public void Dispose() { }
    }
    private sealed class MemoryBuffer(nint handle, bool hostVisible, bool aperture, ConcurrentDictionary<nint, MemoryBuffer> owner, ulong sizeBytes, bool uniform, string? part) : IGpuStorageBuffer, IGpuReadbackBuffer {
        public bool Aperture => aperture;
        public nint BufferHandle => handle;
        public bool HostVisible => hostVisible;
        public byte[] Memory { get; } = new byte[checked((int)sizeBytes)];
        public string? Part => part;
        public ulong SizeBytes => sizeBytes;
        public bool Uniform => uniform;
        public long Written { get; set; }

        public void Dispose() => _ = owner.TryRemove(key: handle, value: out _);
        public void Read(Span<byte> destination) => Memory.AsSpan(length: destination.Length, start: 0).CopyTo(destination: destination);
        public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged => Write(
            data: data,
            destinationOffsetBytes: 0UL
        );
        public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged {
            var bytes = MemoryMarshal.AsBytes(span: data);

            bytes.CopyTo(destination: Memory.AsSpan(start: checked((int)destinationOffsetBytes)));
            Written += bytes.Length;
        }
    }
}
/// <summary>One buffer barrier <see cref="UploadModelGpu"/> recorded.</summary>
/// <param name="Buffer">The buffer's handle.</param>
/// <param name="SourceAccess">The accesses the barrier orders before it.</param>
/// <param name="DestinationAccess">The accesses the barrier orders after it.</param>
/// <param name="SourceStages">The stages the barrier waits on.</param>
/// <param name="DestinationStages">The stages the barrier holds back.</param>
internal readonly record struct UploadModelBufferBarrier(nint Buffer, GpuAccess SourceAccess, GpuAccess DestinationAccess, GpuStage SourceStages, GpuStage DestinationStages);
