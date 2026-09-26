using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>
/// A device-free GPU that models memory for the one kernel whose effect a host law can predict: the region copy,
/// <c>region-copy.comp</c>, which every staged <see cref="GpuRegion"/> records. Every buffer is backed by bytes and
/// carries its own handle; host writes land in those bytes and are tallied per buffer; descriptor sets remember which
/// buffer each binding names; and a dispatch recorded while the pipeline built from <see cref="RegionCopyBytecode"/> is
/// bound runs that kernel's copy at record time, from the set's binding 0 into its binding 1: the source leads with
/// <c>(count, runCount, blockBase, destinationBase)</c> and <c>(block offset, first thread)</c> per run, and thread
/// <c>i</c> below the count copies <c>destination[destinationBase + word] = source[blockBase + word]</c>, where
/// <c>word</c> is found in that run table (a linear search here; the kernel's binary search finds the same run). The
/// dispatch must carry exactly the groups the count needs, and nothing pushes constants to the copy. The copy runs when it
/// is recorded, which is its execution order only when the command buffers are submitted in the order they were recorded,
/// so the model also replays each submission's buffer transitions in submission order as Direct3D 12 tracks them
/// (<see cref="StateConflicts"/>). A disposed buffer is forgotten. Everything else is <see cref="FakeGpuDevice"/>.
/// <para>Shader modules and pipelines are created on the thread pool, several at once
/// (<c>SdfWorldPipelines.BuildConcurrency</c>), so handles come from an interlocked counter and the copy kernel is
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
    private readonly Dictionary<nint, MemoryBuffer> m_buffers = [];
    private readonly List<string> m_stateConflicts = [];
    private readonly Dictionary<nint, List<BufferTransition>> m_transitions = [];

    private readonly FakeGpuDevice m_inner;

    private readonly ConcurrentDictionary<nint, byte> m_uploadModules = new();
    private readonly ConcurrentDictionary<nint, byte> m_uploadPipelines = new();
    // The command buffers recorded since a barrier whose first scope holds the compute stage, which orders every earlier
    // compute read of a staged destination before a copy writes it; a copy recorded in any other is refused.
    private readonly HashSet<nint> m_readsOrdered = [];

    private nint m_boundPipeline;
    private nint m_boundSet;

    private long m_nextHandle = 0x1000;

    /// <summary>Initializes a new instance of the <see cref="UploadModelGpu"/> class.</summary>
    /// <param name="reportVersion">The ISA version a 1×1 readback reports.</param>
    public UploadModelGpu(byte reportVersion) {
        m_inner = new FakeGpuDevice(reportVersion: reportVersion);
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
    /// <summary>Gets the model's buffers, bindings, pipelines, shader modules and recorder, and the
    /// <see cref="FakeGpuDevice"/>'s other services.</summary>
    public GpuDeviceServices Services { get; }
    /// <summary>Gets the table-upload copies recorded since the last <see cref="ResetTallies"/>.</summary>
    public int UploadCopies { get; private set; }
    /// <summary>Gets every device-local buffer transition that declared another state than the one the command buffers
    /// before it in the same submission left its buffer in, as Direct3D 12 reports it: a buffer's state carries from one
    /// command list to the next of one submission and decays only between submissions, and a list's first transition
    /// of a buffer starts from the state its declared source access names (<c>DirectXBufferStates.RequiredState</c>).
    /// A host-visible buffer never transitions there, so it is not tracked.</summary>
    public IReadOnlyList<string> StateConflicts => m_stateConflicts;

    /// <summary>Gets the bytes of the one device-local buffer of <paramref name="sizeBytes"/>.</summary>
    /// <param name="sizeBytes">The buffer's size; exactly one device-local buffer must have it.</param>
    /// <returns>The buffer's current contents.</returns>
    public byte[] DeviceLocal(ulong sizeBytes) => Single(
        hostVisible: false,
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
    }
    public void WaitIdle() { }

    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name) {
        // A pipeline created from groups carries a set layout per group ordinal, as a backend's does.
        var groups = new nint[((description.Layout is { Groups.Count: > 0 } layout)
            ? (layout.Groups.Max(selector: static group => ((int)group.Ordinal)) + 1)
            : 0)];

        for (var index = 0; (index < groups.Length); index++) {
            groups[index] = NextHandle();
        }

        var pipeline = new Handles(handle: NextHandle(), layout: NextHandle(), setLayout: NextHandle(), groups: groups);

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
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) => Buffer(hostVisible: false, sizeBytes: sizeBytes);
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage, in GpuObjectName name) => throw new NotSupportedException();
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, in GpuObjectName name) => throw new NotSupportedException();

    long IGpuBindings.HeapReleaseRevision => m_inner.Services.Bindings.HeapReleaseRevision;

    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle, in GpuObjectName name) => NextHandle();
    bool IGpuBindings.CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) => m_inner.Services.Bindings.CanAdmit(owner: owner, pools: pools, refusal: out refusal);
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes, in GpuObjectName name) => m_inner.Services.Bindings.CreatePool(name: name, sizes: sizes);
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => m_inner.Services.Bindings.CreateSampler(filter: filter);
    void IGpuBindings.DestroyPool(nint poolHandle) { }
    void IGpuBindings.DestroySampler(nint samplerHandle) { }
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) => m_bindings[(descriptorSetHandle, binding)] = bufferHandle;
    void IGpuBindings.WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) { }
    void IGpuBindings.WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) { }
    void IGpuBindings.WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) { }
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) { }
    void IGpuRecorder.BeginCommandBuffer(nint commandBufferHandle) {
        _ = m_readsOrdered.Remove(item: commandBufferHandle);

        if (m_transitions.TryGetValue(
            key: commandBufferHandle,
            value: out var transitions
        )) {
            transitions.Clear();
        }
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
    void IGpuRecorder.ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) { }
    void IGpuRecorder.ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) { }
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) { }
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) {
        if (!m_transitions.TryGetValue(
            key: commandBufferHandle,
            value: out var transitions
        )) {
            transitions = [];
            m_transitions.Add(
                key: commandBufferHandle,
                value: transitions
            );
        }

        transitions.Add(item: new BufferTransition(
            After: StateOf(access: destinationAccessMask, stages: destinationStageMask),
            Buffer: bufferHandle,
            Declared: StateOf(access: sourceAccessMask, stages: sourceStageMask)
        ));
    }
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) {
        if (sourceStageMask.HasFlag(flag: GpuStage.ComputeShader)) {
            _ = m_readsOrdered.Add(item: commandBufferHandle);
        }
    }
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) => m_boundSet = descriptorSetHandle;
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => m_boundPipeline = pipelineHandle;
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        if (!m_uploadPipelines.ContainsKey(key: m_boundPipeline)) {
            return;
        }
        if (!m_readsOrdered.Contains(item: commandBufferHandle)) {
            throw new InvalidOperationException(message: "A region copy was recorded with no barrier ordering the earlier compute reads of its destination before it.");
        }

        var source = MemoryMarshal.Cast<byte, uint>(span: m_buffers[m_bindings[(m_boundSet, 0u)]].Memory.AsSpan());
        var destination = MemoryMarshal.Cast<byte, uint>(span: m_buffers[m_bindings[(m_boundSet, 1u)]].Memory.AsSpan());

        var (count, runCount, blockBase, destinationBase) = (source[0], source[1], source[2], source[3]);
        const uint Header = GpuRegion.CopyHeaderWords;

        if ((groupCountX, groupCountY) != GpuRegion.CopyGroups(count: count)) {
            throw new InvalidOperationException(message: $"A copy of {count} words dispatched {groupCountX}×{groupCountY} groups.");
        }

        // The kernel's thread i is its row times CopyRowThreads plus its column; every thread of the dispatch below the
        // count copies.
        if (((((ulong)groupCountX) * GpuRegion.CopyWorkgroupSize) * groupCountY) < count) {
            throw new InvalidOperationException(message: $"A copy of {count} words dispatched too few threads.");
        }

        for (var thread = 0u; (thread < count); thread++) {
            var run = (runCount - 1u);

            while (source[((int)((Header + (run * 2u)) + 1u))] > thread) {
                run--;
            }

            var word = (source[((int)(Header + (run * 2u)))] + (thread - source[((int)((Header + (run * 2u)) + 1u))]));

            destination[((int)(destinationBase + word))] = source[((int)(blockBase + word))];
        }

        UploadCopies++;
    }
    void IGpuRecorder.PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        if (m_uploadPipelines.ContainsKey(key: m_boundPipeline)) {
            throw new InvalidOperationException(message: "The region copy takes no push constants.");
        }
    }

    private MemoryBuffer Buffer(bool hostVisible, ulong sizeBytes, bool aperture = false, bool uniform = false) {
        var buffer = new MemoryBuffer(
            aperture: aperture,
            handle: NextHandle(),
            hostVisible: hostVisible,
            owner: m_buffers,
            sizeBytes: sizeBytes,
            uniform: uniform
        );

        m_buffers[buffer.BufferHandle] = buffer;

        return buffer;
    }
    // The Direct3D 12 state an access needs, as DirectXBufferStates.RequiredState reads it: a write is unordered access,
    // an indirect read the argument state, a shader read the non-pixel state and the pixel state too when a fragment
    // stage reads, and anything else common.
    private static BufferState StateOf(GpuAccess access, GpuStage stages) {
        if (0 != (access & (GpuAccess.TransferWrite | GpuAccess.ShaderWrite))) {
            return BufferState.UnorderedAccess;
        }

        if (0 != (access & GpuAccess.IndirectCommandRead)) {
            return BufferState.IndirectArgument;
        }

        if (0 != (access & GpuAccess.ShaderRead)) {
            return ((0 != (stages & GpuStage.FragmentShader))
                ? BufferState.NonPixelShaderResource | BufferState.PixelShaderResource
                : BufferState.NonPixelShaderResource);
        }

        return BufferState.Common;
    }
    // Replays one submission's buffer transitions in submission order. Each command list holds the states its own
    // transitions leave, its first transition of a buffer starting from the declared state, and hands them to the next
    // list; a transition that holds a read state already covering its target records nothing, as Direct3D 12's does.
    private void Replay(ReadOnlySpan<nint> commandBufferHandles) {
        var carried = new Dictionary<nint, BufferState>();

        for (var index = 0; (index < commandBufferHandles.Length); index++) {
            if (!m_transitions.TryGetValue(
                key: commandBufferHandles[index],
                value: out var transitions
            )) {
                continue;
            }

            var held = new Dictionary<nint, BufferState>();

            foreach (var transition in transitions) {
                if (
                    !m_buffers.TryGetValue(
                        key: transition.Buffer,
                        value: out var buffer
                    ) ||
                    buffer.HostVisible
                ) {
                    continue;
                }

                if (!held.TryGetValue(
                    key: transition.Buffer,
                    value: out var before
                )) {
                    before = transition.Declared;

                    if (
                        carried.TryGetValue(
                            key: transition.Buffer,
                            value: out var actual
                        ) &&
                        (actual != before)
                    ) {
                        m_stateConflicts.Add(item: $"command buffer {index} of a submission of {commandBufferHandles.Length} transitions buffer 0x{transition.Buffer:x} from {before}, but the command buffers before it left the buffer in {actual}");
                    }
                }

                held[transition.Buffer] = (((before == transition.After) || ((transition.After != BufferState.UnorderedAccess) && ((before & transition.After) == transition.After)))
                    ? before
                    : transition.After);
            }

            foreach (var (bufferHandle, state) in held) {
                carried[bufferHandle] = state;
            }
        }
    }
    private nint NextHandle() => ((nint)Interlocked.Increment(location: ref m_nextHandle));
    private MemoryBuffer Single(bool hostVisible, ulong sizeBytes) => m_buffers.Values.Single(predicate: buffer =>
        (!buffer.Uniform &&
        (buffer.HostVisible == hostVisible) &&
        (buffer.SizeBytes == sizeBytes))
    );

    void IGpuQueueSubmitter.AddExternalWait(GpuExternalWait wait) => m_inner.Services.QueueSubmitter.AddExternalWait(wait: wait);
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() => m_inner.Services.QueueSubmitter.CreateSubmissionFence();
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles) {
        Replay(commandBufferHandles: commandBufferHandles);
        m_inner.Services.QueueSubmitter.Submit(commandBufferHandles: commandBufferHandles);
    }
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        Replay(commandBufferHandles: commandBufferHandles);
        m_inner.Services.QueueSubmitter.Submit(
            commandBufferHandles: commandBufferHandles,
            fence: fence
        );
    }
    void IGpuQueueSubmitter.SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        Replay(commandBufferHandles: commandBufferHandles);
        m_inner.Services.QueueSubmitter.SubmitAndWait(commandBufferHandles: commandBufferHandles);
    }

    // The Direct3D 12 buffer states the replay tracks, as their D3D12_RESOURCE_STATES bits combine.
    [Flags]
    private enum BufferState {
        Common = 0,
        NonPixelShaderResource = 1,
        PixelShaderResource = 2,
        UnorderedAccess = 4,
        IndirectArgument = 8,
    }
    // One recorded buffer transition: the state its declared source access names, and the state its target access
    // needs.
    private readonly record struct BufferTransition(nint Buffer, BufferState Declared, BufferState After);

    IGpuCommandPool IGpuCommandPoolFactory.Create(in GpuObjectName name) => new CommandPool(
        handle: NextHandle(),
        inner: m_inner.Services.CommandPoolFactory.Create(name: name)
    );

    // A command pool of the wrapped device with a command buffer handle of its own, so the replay tells a submission's
    // command buffers apart.
    private sealed class CommandPool(nint handle, IGpuCommandPool inner) : IGpuCommandPool {
        public nint CommandBufferHandle => handle;

        public void Dispose() => inner.Dispose();
    }
    // A shader module or pipeline: its own handle, plus the layout handles a pipeline carries.
    private sealed class Handles(nint handle, nint layout, nint setLayout, nint[]? groups = null) : IGpuComputePipeline, IGpuShaderModule {
        public nint DescriptorSetLayoutHandle => setLayout;
        public IReadOnlyList<nint> GroupLayoutHandles => (groups ?? []);
        public nint Handle => handle;
        public nint LayoutHandle => layout;

        public void Dispose() { }
    }
    private sealed class MemoryBuffer(nint handle, bool hostVisible, bool aperture, Dictionary<nint, MemoryBuffer> owner, ulong sizeBytes, bool uniform) : IGpuStorageBuffer {
        public bool Aperture => aperture;
        public nint BufferHandle => handle;
        public bool HostVisible => hostVisible;
        public byte[] Memory { get; } = new byte[checked((int)sizeBytes)];
        public ulong SizeBytes => sizeBytes;
        public bool Uniform => uniform;
        public long Written { get; set; }

        public void Dispose() => _ = owner.Remove(key: handle);
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
