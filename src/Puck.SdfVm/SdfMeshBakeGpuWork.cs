using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The mesh bake's sampling as background GPU work (<see cref="SdfGpuWork"/>): each job is a batch the residency's bake
/// takes (<see cref="SdfMeshPathBake.TryTake(Puck.SignedDistance.SdfProgram?, out SdfMeshBakeBatch?, long)"/>), dispatched as the mask kernel writing each lattice's instance mask and
/// the bake kernel sampling every lattice through the field interpreter, its samples copied back and answered
/// (<see cref="SdfMeshPathBake.Complete(SdfMeshBakeBatch, ReadOnlySpan{byte})"/>). Each slot holds the batch's own inputs and outputs: its pass block, its
/// point table (sample points, then saved poses), its sampled table, its instance masks and samples, and a World set
/// binding the tables' current tables with that point table and sampled table in place of theirs.
/// <para>A slot's point and sampled tables, instance masks and samples are the service's
/// (<see cref="SdfGpuWorkRecording.HostBuffer"/>, <see cref="SdfGpuWorkRecording.DeviceBuffer"/>): reserved before they
/// are allocated, grown to the slot's largest batch and gone once no batch has needed them for
/// <see cref="SdfGpuWork.IdleSeconds"/>, so a settled bake holds none.</para>
/// <para>Invariant: a batch samples the program the tables last uploaded. The bake takes a batch only for the program
/// it meshes, and only when that is the tables' uploaded program (<see cref="SdfWorldTables.BackgroundProgram"/>), so
/// the field the World set binds is the field the batch's groups were planned from.</para>
/// <para>Invariant: a lattice samples exactly the snapshots its group's geometry hashed. The sampled values the kernel
/// reads come from one place, the slot's sampled table, written here from the batch's snapshots
/// (<see cref="SdfMeshBakeBatch.WriteSampled"/>), which the bake takes from its groups' sealed snapshots
/// (<see cref="SdfMeshBakeGroup.SampledSnapshots"/>), one snapshot a handle; the tables bind a filler there.</para>
/// </summary>
internal sealed class SdfMeshBakeGpuWork : ISdfGpuWorkClient {
    // The words the work counters' one row and its details take at most.
    private const int CounterBytes = 256;
    private const int PointRowBytes = 16;
    // The parts of a slot's buffers, by which the service keeps them for the slot's next batch.
    private const string PointsPart = "points";
    private const string SampledPart = "sampled";
    private const string MasksPart = "masks";
    private const string SamplesPart = "samples";

    private readonly SdfWorldTables m_tables;
    private readonly SdfMeshPathBake m_bake;
    private readonly GpuDeviceServices m_gpu;
    private readonly nint m_pool;
    private readonly nint m_frameSet;
    private readonly IGpuStorageBuffer m_frameBlock;
    private readonly IGpuBuffer m_counters;

    private readonly SlotResources[] m_slots = new SlotResources[SdfGpuWork.SlotCount];
    private readonly byte[] m_block = new byte[SdfMeshBakeBlock.SizeBytes];
    // The measured GPU time of one field evaluation, following the mean: a batch mixes several groups' lattices whose
    // evaluations differ in cost, and a rate that rose to the costliest batch at once would shrink every batch after it.
    private readonly SdfGpuWorkRate m_rate = new(priorSecondsPerUnit: (SdfMeshPathBake.NanosecondsPerTap * 1e-9), rise: SdfGpuWorkRate.Fall);

    private bool m_disposed;

    /// <summary>Returns the descriptors the client's pool holds: a frame set, and a World set and two pass sets a
    /// slot.</summary>
    internal static GpuDescriptorPoolSizes PoolSizes {
        get {
            var groups = SdfWorldInterfaces.MeshBakeLayout.PipelineLayout(stages: GpuShaderStage.Compute).Groups;
            var sizes = GpuDescriptorPoolSizes.ForGroups(groups: groups.Where(predicate: static group => (group.Ordinal == ((uint)ShaderInterfaceGroup.Frame))).ToArray());
            var world = GpuDescriptorPoolSizes.ForGroups(groups: groups.Where(predicate: static group => (group.Ordinal == ((uint)ShaderInterfaceGroup.World))).ToArray());
            var pass = GpuDescriptorPoolSizes.ForGroups(groups: groups.Where(predicate: static group => (group.Ordinal == ((uint)ShaderInterfaceGroup.Pass))).ToArray());

            for (var slot = 0; (slot < SdfGpuWork.SlotCount); slot++) {
                sizes += world;
                sizes += pass;
                sizes += pass;
            }
            return sizes;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="SdfMeshBakeGpuWork"/> class: its descriptor pool, its frame
    /// set and the counters its kernels count into; each slot's buffers are created on its first job.</summary>
    /// <param name="tables">The tables whose World set and bake pipelines the jobs use, and whose device creates the
    /// client's objects.</param>
    /// <param name="bake">The bake whose batches the jobs dispatch.</param>
    internal SdfMeshBakeGpuWork(SdfWorldTables tables, SdfMeshPathBake bake) {
        m_tables = tables;
        m_bake = bake;
        m_gpu = tables.Gpu;

        using var scope = new GpuCreationScope();
        var layouts = tables.Pipeline(kernel: SdfKernel.MeshBake).GroupLayoutHandles;
        var parameters = SdfWorldInterfaces.MeshBakeParameters;

        m_pool = scope.Own(handle: m_gpu.Bindings.CreatePool(name: Name(part: "descriptors"), sizes: PoolSizes), release: m_gpu.Bindings.DestroyPool);
        m_frameBlock = scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(name: Name(part: "frame block"),
            sizeBytes: ((ulong)IGpuBindings.ConstantBufferBytes(blockBytes: parameters.FrameBlockSizeBytes)), usage: GpuBufferUsage.Uniform));

        var frameBytes = new byte[parameters.FrameBlockSizeBytes];

        parameters.WriteFrame(block: frameBytes, frame: 0UL, values: default);
        m_frameBlock.Write<byte>(data: frameBytes);
        m_frameSet = m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: layouts[((int)ShaderInterfaceGroup.Frame)], name: Name(part: "frame group"), poolHandle: m_pool);
        m_gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: m_frameBlock.BufferHandle, bufferSize: m_frameBlock.SizeBytes, descriptorSetHandle: m_frameSet);
        m_counters = scope.Own(created: m_gpu.BufferFactory.CreateDeviceLocal(name: Name(part: "counters"), sizeBytes: CounterBytes, usage: GpuBufferUsage.Storage));
        for (var slot = 0; (slot < m_slots.Length); slot++) {
            m_slots[slot] = new SlotResources(
                Block: scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(name: Name(index: slot, part: "pass block"),
                    sizeBytes: ((ulong)IGpuBindings.ConstantBufferBytes(blockBytes: ((uint)SdfMeshBakeBlock.SizeBytes))), usage: GpuBufferUsage.Uniform)),
                MaskSet: m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: layouts[((int)ShaderInterfaceGroup.Pass)], name: Name(index: slot, part: "mask group"), poolHandle: m_pool),
                BakeSet: m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: layouts[((int)ShaderInterfaceGroup.Pass)], name: Name(index: slot, part: "bake group"), poolHandle: m_pool),
                WorldSet: m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: layouts[((int)ShaderInterfaceGroup.World)], name: Name(index: slot, part: "world group"), poolHandle: m_pool)
            );
        }
        scope.Complete();
    }

    /// <inheritdoc/>
    /// <remarks>Takes the bake's next batch for the tables' uploaded program, its field evaluations cut to the allowance
    /// at the bake's measured rate (<see cref="SdfMeshPathBake.BatchTaps"/> for a held frame), and estimated at that rate.
    /// With nothing queued the first lattice is taken whatever its evaluations, since a lattice is never split; while
    /// work is queued a first lattice past what is left waits, priced so the service holds room for it.</remarks>
    public bool TryTake(SdfWorldTables tables, int uploadBytes, SdfGpuWorkAllowance allowance, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) =>
        Take(allowance: allowance, estimatedSeconds: out estimatedSeconds, job: out job, owed: false, tables: tables);
    /// <inheritdoc/>
    /// <remarks>Takes a batch of the lattices of groups that owe the frame alone
    /// (<see cref="SdfMeshPathBake.TryTakeOwed"/>), cut and priced as <see cref="TryTake"/> cuts and prices one.</remarks>
    public bool TryTakeOwed(SdfWorldTables tables, int uploadBytes, SdfGpuWorkAllowance allowance, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) =>
        Take(allowance: allowance, estimatedSeconds: out estimatedSeconds, job: out job, owed: true, tables: tables);

    private bool Take(SdfWorldTables tables, SdfGpuWorkAllowance allowance, bool owed, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) {
        var mostTaps = allowance.UnitsWithin(most: SdfMeshPathBake.BatchTaps, rate: m_rate);
        SdfMeshBakeBatch? batch;
        long heldTaps;
        var taken = (owed
            ? m_bake.TryTakeOwed(batch: out batch, field: tables.BackgroundProgram, firstFits: allowance.Queued, heldTaps: out heldTaps, mostTaps: mostTaps)
            : m_bake.TryTake(batch: out batch, field: tables.BackgroundProgram, firstFits: allowance.Queued, heldTaps: out heldTaps, mostTaps: mostTaps));

        if (!taken) {
            job = null;
            estimatedSeconds = m_rate.SecondsOf(units: heldTaps);
            return false;
        }
        job = batch!;
        estimatedSeconds = m_rate.SecondsOf(units: batch!.Taps);
        return true;
    }
    /// <inheritdoc/>
    /// <remarks>Writes the slot's pass block and point table, binds them with the tables' current World set, and records
    /// the mask dispatch, one workgroup a lattice, then the bake dispatch, one invocation a sample, and the samples'
    /// copy back.</remarks>
    public void Record(object job, in SdfGpuWorkRecording recording) {
        var batch = ((SdfMeshBakeBatch)job);
        var program = m_tables.BackgroundProgram!;
        var index = recording.Slot;
        var slot = m_slots[index];
        var samples = batch.Samples;
        var sampleBytes = ((((ulong)samples) * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint));
        var maskWords = program.InstanceMaskWordCount;
        var maskBytes = (((ulong)(SdfWorldPackage.MeshBakeBatch * (maskWords + ((maskWords + 31) >> 5)))) * sizeof(uint));
        var pointBytes = (((ulong)Math.Max(val1: 1, val2: batch.InputRows)) * PointRowBytes);
        var sampledBytes = (((ulong)batch.SampledWords) * sizeof(uint));
        // Every buffer the batch holds is asked for before anything is recorded or written, so a batch the device has
        // no room for is handed back whole.
        var points = recording.HostBuffer(bytes: pointBytes, part: PointsPart, usage: GpuBufferUsage.Storage);
        var sampled = recording.HostBuffer(bytes: sampledBytes, part: SampledPart, usage: GpuBufferUsage.Storage);
        var masks = recording.DeviceBuffer(bytes: Math.Max(val1: maskBytes, val2: sizeof(uint)), part: MasksPart, usage: GpuBufferUsage.Storage);
        var samplesBuffer = recording.DeviceBuffer(bytes: Math.Max(val1: sampleBytes, val2: sizeof(uint)), part: SamplesPart, usage: GpuBufferUsage.Storage);

        // The pass sets name the buffers they were written with, so a replaced one is written again.
        if (!ReferenceEquals(objA: slot.PassMasks, objB: masks) || !ReferenceEquals(objA: slot.PassSamples, objB: samplesBuffer)) {
            WritePassSets(masks: masks, samples: samplesBuffer, slot: slot);
            slot.PassMasks = masks;
            slot.PassSamples = samplesBuffer;
        }
        foreach (var (row, rows) in batch.PointRuns()) {
            points.Write<Vector4>(data: rows.Span, destinationOffsetBytes: (((ulong)row) * PointRowBytes));
        }
        // The slot keeps the table its last job wrote: a batch of the same snapshots at the same handles rewrites nothing.
        if (!ReferenceEquals(objA: slot.SampledTable, objB: sampled) || !SameSampled(batch: batch, slot: slot)) {
            // The table lives only through this write: native storage from the bake's allocator, charged while it lasts,
            // rather than a pooled array a large table would allocate afresh and drop.
            using var owner = MemoryAccount.Shared.CreateOwner(memoryClass: Puck.Abstractions.Memory.MemoryClass.Workspace, name: "mesh-bake-sampled");
            using var words = new OwnedBuffer<uint>(allocator: m_bake.Library.Allocator, observer: owner);

            words.Resize(length: batch.SampledWords, clear: false, preserve: false);
            batch.WriteSampled(table: words.GetSpan());
            sampled.Write<uint>(data: words.GetSpan());
            slot.SampledTable = sampled;
            slot.SampledHandles = batch.SampledHandles;
            slot.SampledWritten = batch.Sampled;
        }
        Array.Clear(array: m_block);
        SdfMeshBakeBlock.Write(batch: batch, block: m_block, instanceMaskWordCount: ((uint)maskWords), workCounterDetailRow: 0u, workCounterRow: 0u);
        slot.Block.Write<byte>(data: m_block);
        m_tables.WriteBackgroundWorldSet(overrides: [(SdfWorldPackage.MeshBakePoints, points), (SdfWorldPackage.SampledValues, sampled)], set: slot.WorldSet);

        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var mask = m_tables.Pipeline(kernel: SdfKernel.MeshBakeMask);
        var sampler = m_tables.Pipeline(kernel: SdfKernel.MeshBake);

        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: masks.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        Bind(commandBuffer: commandBuffer, pass: slot.MaskSet, pipeline: mask, recorder: recorder, world: slot.WorldSet);
        recorder.Dispatch(commandBufferHandle: commandBuffer, groupCountX: SdfWorldPackage.MeshBakeBatch, groupCountY: 1, groupCountZ: 1);
        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: masks.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: samplesBuffer.BufferHandle,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        Bind(commandBuffer: commandBuffer, pass: slot.BakeSet, pipeline: sampler, recorder: recorder, world: slot.WorldSet);
        recorder.Dispatch(commandBufferHandle: commandBuffer, groupCountX: ((uint)((samples + 63) / 64)), groupCountY: 1, groupCountZ: 1);
        recording.CopyToReadback(bytes: sampleBytes, source: samplesBuffer);
    }
    /// <inheritdoc/>
    /// <remarks>Reads the samples on the bake's pool, ahead of its queued stages, releases the slot as soon as they are
    /// copied, and answers the batch there; a batch whose samples could not be read goes back to its groups.</remarks>
    public void Complete(object job, SdfGpuWorkReadback readback) {
        var batch = ((SdfMeshBakeBatch)job);

        if (readback.GpuSeconds is { } seconds) { m_rate.Observe(seconds: seconds, units: batch.Taps); }
        SdfMeshPathBake.RunFirst(work: () => {
            var length = readback.Length;
            // The copy belongs to this answer alone: native storage from the bake's allocator, charged while it lasts
            // (the shared pool allocates anything past a mebibyte afresh and drops it, which the samples are).
            using var owner = MemoryAccount.Shared.CreateOwner(memoryClass: Puck.Abstractions.Memory.MemoryClass.Workspace, name: "mesh-bake-readback");
            using var bytes = new OwnedBuffer<byte>(allocator: m_bake.Library.Allocator, observer: owner);

            try {
                try {
                    bytes.Resize(clear: false, length: length, preserve: false);
                    readback.Read(destination: bytes.GetSpan());
                } finally {
                    readback.Release();
                }
                m_bake.Complete(batch: batch, samples: bytes.GetSpan());
            } catch {
                m_bake.Abandon(batch: batch);
                throw;
            }
        });
    }
    /// <inheritdoc/>
    public void Abandon(object job) => m_bake.Abandon(batch: ((SdfMeshBakeBatch)job));
    /// <inheritdoc/>
    public GpuQueueCategory Category => GpuQueueCategory.Bake;

    /// <inheritdoc/>
    public string Describe(object job) => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"bake-batch samples={((SdfMeshBakeBatch)job).Samples} input-rows={((SdfMeshBakeBatch)job).InputRows}");

    /// <inheritdoc/>
    /// <remarks>Each slot's pass block and the frame block (host-visible) and the counters (device-local).</remarks>
    public GpuMemoryBytes OwnedBytes {
        get {
            var host = m_frameBlock.SizeBytes;

            foreach (var slot in m_slots) { host += slot.Block.SizeBytes; }
            return new GpuMemoryBytes(DeviceLocal: m_counters.SizeBytes, HostVisible: host);
        }
    }

    /// <inheritdoc/>
    /// <remarks>The service disposes its clients once no job of theirs is in flight.</remarks>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var slot in m_slots) { slot.Block.Dispose(); }
        m_counters.Dispose();
        m_frameBlock.Dispose();
        m_gpu.Bindings.DestroyPool(poolHandle: m_pool);
    }

    // Whether a slot's sampled table holds a batch's snapshots at its handles, by snapshot object.
    private static bool SameSampled(SdfMeshBakeBatch batch, SlotResources slot) {
        var written = slot.SampledWritten;

        if ((written is null) || (slot.SampledHandles != batch.SampledHandles) || (written.Length != batch.Sampled.Length)) { return false; }
        for (var index = 0; (index < written.Length); index++) {
            if ((written[index].Handle != batch.Sampled[index].Handle) || !ReferenceEquals(objA: written[index].Snapshot, objB: batch.Sampled[index].Snapshot)) { return false; }
        }
        return true;
    }
    // Binds a kernel and its three groups: the client's frame set, the job's pass set and its World set.
    private void Bind(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, nint pass, nint world) {
        recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, pipelineHandle: pipeline.Handle);
        recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, descriptorSetHandle: m_frameSet, group: ((uint)ShaderInterfaceGroup.Frame), pipelineLayoutHandle: pipeline.LayoutHandle);
        recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, descriptorSetHandle: world, group: ((uint)ShaderInterfaceGroup.World), pipelineLayoutHandle: pipeline.LayoutHandle);
        recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, descriptorSetHandle: pass, group: ((uint)ShaderInterfaceGroup.Pass), pipelineLayoutHandle: pipeline.LayoutHandle);
    }
    // Writes a slot's two pass sets: the mask's writes the instance masks, the bake's reads them and writes the samples,
    // and each binds the tables' unused-member buffer at what it does not touch, as the kernels' declarations require.
    private void WritePassSets(SlotResources slot, IGpuBuffer masks, IGpuBuffer samples) {
        var layout = SdfWorldInterfaces.MeshBakeLayout;
        var unused = m_tables.DummyBuffer;

        foreach (var set in ((ReadOnlySpan<nint>)[slot.MaskSet, slot.BakeSet])) {
            m_gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: slot.Block.BufferHandle, bufferSize: slot.Block.SizeBytes, descriptorSetHandle: set);
            m_tables.WriteInterfaceBuffer(buffer: m_counters, layout: layout, member: ShaderWorkCounters.Buffer, set: set);
        }
        m_tables.WriteInterfaceBuffer(buffer: masks, layout: layout, member: SdfWorldPackage.InstanceMasksWritten, set: slot.MaskSet);
        m_tables.WriteInterfaceBuffer(buffer: unused, layout: layout, member: SdfWorldPackage.InstanceMasks, set: slot.MaskSet);
        m_tables.WriteInterfaceBuffer(buffer: unused, layout: layout, member: SdfWorldPackage.MeshBakeSamplesWritten, set: slot.MaskSet);
        m_tables.WriteInterfaceBuffer(buffer: masks, layout: layout, member: SdfWorldPackage.InstanceMasks, set: slot.BakeSet);
        m_tables.WriteInterfaceBuffer(buffer: unused, layout: layout, member: SdfWorldPackage.InstanceMasksWritten, set: slot.BakeSet);
        m_tables.WriteInterfaceBuffer(buffer: samples, layout: layout, member: SdfWorldPackage.MeshBakeSamplesWritten, set: slot.BakeSet);
    }
    private static GpuObjectName Name(string part, int index = GpuObjectName.NoIndex) =>
        new(index: index, owner: SdfWorldTables.ObjectOwner, part: $"mesh-bake {part}");

    // One slot's own resources: its pass block, its two pass sets and World set, the buffers its pass sets name, and
    // the sampled table it last wrote with what it wrote. The buffers are the service's, kept here only to tell a
    // replaced one by its object.
    private sealed class SlotResources(IGpuStorageBuffer Block, nint MaskSet, nint BakeSet, nint WorldSet) {
        public IGpuStorageBuffer Block { get; } = Block;
        public nint MaskSet { get; } = MaskSet;
        public nint BakeSet { get; } = BakeSet;
        public nint WorldSet { get; } = WorldSet;

        public IGpuBuffer? PassMasks { get; set; }
        public IGpuBuffer? PassSamples { get; set; }
        public IGpuBuffer? SampledTable { get; set; }
        // The batch snapshots and handle count the sampled table was last written with.
        public (int Handle, Puck.SignedDistance.SdfSampledSnapshot Snapshot)[]? SampledWritten { get; set; }
        public int SampledHandles { get; set; }
    }
}
