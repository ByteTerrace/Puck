using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The mesh bake's sampling as background GPU work (<see cref="SdfGpuWork"/>): each job is a batch the residency's bake
/// takes (<see cref="SdfMeshPathBake.TryTake"/>), dispatched as the mask kernel writing each lattice's instance mask and
/// the bake kernel sampling every lattice through the field interpreter, its samples copied back and answered
/// (<see cref="SdfMeshPathBake.Complete(SdfMeshBakeBatch, ReadOnlySpan{byte})"/>). Each slot holds the batch's own inputs and outputs: its pass block, its
/// point table (sample points, then saved poses), its sampled table, its instance masks and samples, and a World set
/// binding the tables' current tables with that point table and sampled table in place of theirs.
/// <para>A slot's point and sampled tables, instance masks and samples grow to its largest batch and go once the slot has
/// recorded no batch for <see cref="SdfGpuWork.IdleSeconds"/> (<see cref="ReleaseIdle"/>), so a settled bake holds none.</para>
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

    private readonly SdfWorldTables m_tables;
    private readonly SdfMeshPathBake m_bake;
    private readonly GpuDeviceServices m_gpu;
    private readonly nint m_pool;
    private readonly nint m_frameSet;
    private readonly IGpuStorageBuffer m_frameBlock;
    private readonly IGpuBuffer m_counters;
    private readonly SlotResources[] m_slots = new SlotResources[SdfGpuWork.SlotCount];
    private readonly byte[] m_block = new byte[SdfMeshBakeBlock.SizeBytes];
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
                Block: scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(name: Name(part: "pass block", index: slot),
                    sizeBytes: ((ulong)IGpuBindings.ConstantBufferBytes(blockBytes: (uint)SdfMeshBakeBlock.SizeBytes)), usage: GpuBufferUsage.Uniform)),
                MaskSet: m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: layouts[((int)ShaderInterfaceGroup.Pass)], name: Name(part: "mask group", index: slot), poolHandle: m_pool),
                BakeSet: m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: layouts[((int)ShaderInterfaceGroup.Pass)], name: Name(part: "bake group", index: slot), poolHandle: m_pool),
                WorldSet: m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: layouts[((int)ShaderInterfaceGroup.World)], name: Name(part: "world group", index: slot), poolHandle: m_pool)
            );
        }
        scope.Complete();
    }

    /// <inheritdoc/>
    /// <remarks>Takes the bake's next batch for the tables' uploaded program, estimated at
    /// <see cref="SdfMeshPathBake.NanosecondsPerTap"/> a field evaluation.</remarks>
    public bool TryTake(SdfWorldTables tables, int uploadBytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out object? job, out double estimatedSeconds) {
        if (!m_bake.TryTake(batch: out var batch, field: tables.BackgroundProgram)) {
            job = null;
            estimatedSeconds = 0.0;
            return false;
        }

        var taps = 0L;

        foreach (var lattice in batch.Lattices) { taps += lattice.Taps; }
        job = batch;
        estimatedSeconds = ((taps * SdfMeshPathBake.NanosecondsPerTap) * 1e-9);
        return true;
    }
    /// <inheritdoc/>
    /// <remarks>Writes the slot's pass block and point table, binds them with the tables' current World set, and records
    /// the mask dispatch, one workgroup a lattice, then the bake dispatch, one invocation a sample, and the samples'
    /// copy back.</remarks>
    public void Record(object job, in SdfGpuWorkRecording recording) {
        var batch = (SdfMeshBakeBatch)job;
        var program = m_tables.BackgroundProgram!;
        var index = recording.Slot;
        var slot = m_slots[index];
        var samples = batch.Samples;
        var sampleBytes = (((ulong)samples) * SdfWorldPackage.MeshBakeSampleWords * sizeof(uint));
        var maskWords = program.InstanceMaskWordCount;
        var maskBytes = ((ulong)(SdfWorldPackage.MeshBakeBatch * (maskWords + ((maskWords + 31) >> 5)))) * sizeof(uint);
        var pointBytes = (((ulong)Math.Max(val1: 1, val2: batch.InputRows)) * PointRowBytes);
        var replaced = false;

        slot.LastRecorded = System.Diagnostics.Stopwatch.GetTimestamp();

        var sampledBytes = (((ulong)batch.SampledWords) * sizeof(uint));

        slot.Points = Ensure(buffer: slot.Points, bytes: pointBytes, create: bytes => m_gpu.BufferFactory.CreateHostVisible(name: Name(part: "points", index: index), sizeBytes: bytes, usage: GpuBufferUsage.Storage), replaced: ref replaced);
        var sampledReplaced = false;

        slot.Sampled = Ensure(buffer: slot.Sampled, bytes: sampledBytes, create: bytes => m_gpu.BufferFactory.CreateHostVisible(name: Name(part: "sampled", index: index), sizeBytes: bytes, usage: GpuBufferUsage.Storage), replaced: ref sampledReplaced);
        slot.Masks = Ensure(buffer: slot.Masks, bytes: Math.Max(val1: maskBytes, val2: sizeof(uint)), create: bytes => m_gpu.BufferFactory.CreateDeviceLocal(name: Name(part: "masks", index: index), sizeBytes: bytes, usage: GpuBufferUsage.Storage), replaced: ref replaced);
        slot.Samples = Ensure(buffer: slot.Samples, bytes: Math.Max(val1: sampleBytes, val2: sizeof(uint)), create: bytes => m_gpu.BufferFactory.CreateDeviceLocal(name: Name(part: "samples", index: index), sizeBytes: bytes, usage: GpuBufferUsage.Storage), replaced: ref replaced);
        if (replaced || !slot.PassSetsWritten) { WritePassSets(slot: slot); }

        foreach (var (row, rows) in batch.PointRuns()) {
            ((IGpuStorageBuffer)slot.Points).Write<Vector4>(data: rows.Span, destinationOffsetBytes: (((ulong)row) * PointRowBytes));
        }
        // The slot keeps the table its last job wrote: a batch of the same snapshots at the same handles rewrites nothing.
        if (sampledReplaced || !SameSampled(batch: batch, slot: slot)) {
            // The table lives only through this write: native storage from the bake's allocator, charged while it lasts,
            // rather than a pooled array a large table would allocate afresh and drop.
            using var owner = MemoryAccount.Shared.CreateOwner(name: "mesh-bake-sampled");
            using var words = new OwnedBuffer<uint>(allocator: m_bake.Library.Allocator, observer: owner);

            words.Resize(length: batch.SampledWords, clear: false, preserve: false);
            batch.WriteSampled(table: words.GetSpan());
            slot.Sampled.Write<uint>(data: words.GetSpan());
            slot.SampledHandles = batch.SampledHandles;
            slot.SampledWritten = batch.Sampled;
        }
        Array.Clear(array: m_block);
        SdfMeshBakeBlock.Write(batch: batch, block: m_block, instanceMaskWordCount: (uint)maskWords, workCounterDetailRow: 0u, workCounterRow: 0u);
        slot.Block.Write<byte>(data: m_block);
        m_tables.WriteBackgroundWorldSet(overrides: [(SdfWorldPackage.MeshBakePoints, slot.Points), (SdfWorldPackage.SampledValues, slot.Sampled)], set: slot.WorldSet);

        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var mask = m_tables.Pipeline(kernel: SdfKernel.MeshBakeMask);
        var sampler = m_tables.Pipeline(kernel: SdfKernel.MeshBake);

        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: slot.Masks.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        Bind(commandBuffer: commandBuffer, pass: slot.MaskSet, pipeline: mask, recorder: recorder, world: slot.WorldSet);
        recorder.Dispatch(commandBufferHandle: commandBuffer, groupCountX: SdfWorldPackage.MeshBakeBatch, groupCountY: 1, groupCountZ: 1);
        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: slot.Masks.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: slot.Samples.BufferHandle,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        Bind(commandBuffer: commandBuffer, pass: slot.BakeSet, pipeline: sampler, recorder: recorder, world: slot.WorldSet);
        recorder.Dispatch(commandBufferHandle: commandBuffer, groupCountX: (uint)((samples + 63) / 64), groupCountY: 1, groupCountZ: 1);
        recording.CopyToReadback(bytes: sampleBytes, source: slot.Samples);
    }
    /// <inheritdoc/>
    /// <remarks>Reads the samples on the bake's pool, ahead of its queued stages, releases the slot as soon as they are
    /// copied, and answers the batch there; a batch whose samples could not be read goes back to its groups.</remarks>
    public void Complete(object job, SdfGpuWorkReadback readback) {
        var batch = (SdfMeshBakeBatch)job;

        SdfMeshPathBake.RunFirst(work: () => {
            var length = readback.Length;
            // The copy belongs to this answer alone: native storage from the bake's allocator, charged while it lasts
            // (the shared pool allocates anything past a mebibyte afresh and drops it, which the samples are).
            using var owner = MemoryAccount.Shared.CreateOwner(name: "mesh-bake-readback");
            using var bytes = new OwnedBuffer<byte>(allocator: m_bake.Library.Allocator, observer: owner);

            try {
                try {
                    bytes.Resize(length: length, clear: false, preserve: false);
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
    public void Abandon(object job) => m_bake.Abandon(batch: (SdfMeshBakeBatch)job);
    /// <inheritdoc/>
    /// <remarks>Disposes the slot's point and sampled tables, instance masks and samples; the slot's next batch creates
    /// them at its own size and rewrites its pass sets and sampled table.</remarks>
    public void ReleaseIdle(int slot, long idleBefore) {
        var resources = m_slots[slot];

        if ((resources.LastRecorded >= idleBefore) || ((resources.Points ?? resources.Sampled ?? resources.Masks ?? resources.Samples) is null)) { return; }
        resources.Points?.Dispose();
        resources.Sampled?.Dispose();
        resources.Masks?.Dispose();
        resources.Samples?.Dispose();
        resources.Points = null;
        resources.Sampled = null;
        resources.Masks = null;
        resources.Samples = null;
        resources.SampledWritten = null;
        resources.SampledHandles = 0;
        resources.PassSetsWritten = false;
    }
    /// <inheritdoc/>
    public GpuQueueCategory Category => GpuQueueCategory.Bake;
    /// <inheritdoc/>
    public string Describe(object job) => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"bake-batch samples={((SdfMeshBakeBatch)job).Samples} input-rows={((SdfMeshBakeBatch)job).InputRows}");
    /// <inheritdoc/>
    /// <remarks>Each slot's pass block, point table and sampled table (host-visible) and its instance masks and samples
    /// (device-local), with the frame block and the counters.</remarks>
    public GpuMemoryBytes OwnedBytes {
        get {
            var device = m_counters.SizeBytes;
            var host = m_frameBlock.SizeBytes;

            foreach (var slot in m_slots) {
                host += slot.Block.SizeBytes + (slot.Points?.SizeBytes ?? 0UL) + (slot.Sampled?.SizeBytes ?? 0UL);
                device += (slot.Masks?.SizeBytes ?? 0UL) + (slot.Samples?.SizeBytes ?? 0UL);
            }
            return new GpuMemoryBytes(DeviceLocal: device, HostVisible: host);
        }
    }

    /// <inheritdoc/>
    /// <remarks>The service disposes its clients once no job of theirs is in flight.</remarks>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var slot in m_slots) {
            slot.Block.Dispose();
            slot.Points?.Dispose();
            slot.Sampled?.Dispose();
            slot.Masks?.Dispose();
            slot.Samples?.Dispose();
        }
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
    private void WritePassSets(SlotResources slot) {
        var layout = SdfWorldInterfaces.MeshBakeLayout;
        var unused = m_tables.DummyBuffer;

        foreach (var set in ((ReadOnlySpan<nint>)[slot.MaskSet, slot.BakeSet])) {
            m_gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: slot.Block.BufferHandle, bufferSize: slot.Block.SizeBytes, descriptorSetHandle: set);
            m_tables.WriteInterfaceBuffer(buffer: m_counters, layout: layout, member: ShaderWorkCounters.Buffer, set: set);
        }
        m_tables.WriteInterfaceBuffer(buffer: slot.Masks!, layout: layout, member: SdfWorldPackage.InstanceMasksWritten, set: slot.MaskSet);
        m_tables.WriteInterfaceBuffer(buffer: unused, layout: layout, member: SdfWorldPackage.InstanceMasks, set: slot.MaskSet);
        m_tables.WriteInterfaceBuffer(buffer: unused, layout: layout, member: SdfWorldPackage.MeshBakeSamplesWritten, set: slot.MaskSet);
        m_tables.WriteInterfaceBuffer(buffer: slot.Masks!, layout: layout, member: SdfWorldPackage.InstanceMasks, set: slot.BakeSet);
        m_tables.WriteInterfaceBuffer(buffer: unused, layout: layout, member: SdfWorldPackage.InstanceMasksWritten, set: slot.BakeSet);
        m_tables.WriteInterfaceBuffer(buffer: slot.Samples!, layout: layout, member: SdfWorldPackage.MeshBakeSamplesWritten, set: slot.BakeSet);
        slot.PassSetsWritten = true;
    }
    // A slot's buffer of at least the bytes, replaced in whole granules (SdfGpuWork.Granule) when short: the slot's last
    // job has completed and its readback was released, so nothing reads the buffer it replaces.
    private static T Ensure<T>(T? buffer, ulong bytes, Func<ulong, T> create, ref bool replaced) where T : class, IGpuBuffer {
        if ((buffer is not null) && (buffer.SizeBytes >= bytes)) { return buffer; }

        buffer?.Dispose();
        replaced = true;
        return create(arg: SdfGpuWork.Granule(bytes: bytes));
    }
    private static GpuObjectName Name(string part, int index = GpuObjectName.NoIndex) =>
        new(index: index, owner: SdfWorldTables.ObjectOwner, part: $"mesh-bake {part}");

    // One slot's own resources: its pass block, its two pass sets and World set, and the buffers its job reads and writes.
    private sealed class SlotResources(IGpuStorageBuffer Block, nint MaskSet, nint BakeSet, nint WorldSet) {
        public IGpuStorageBuffer Block { get; } = Block;
        public nint MaskSet { get; } = MaskSet;
        public nint BakeSet { get; } = BakeSet;
        public nint WorldSet { get; } = WorldSet;
        public IGpuStorageBuffer? Points { get; set; }
        public IGpuStorageBuffer? Sampled { get; set; }
        // The batch snapshots and handle count the sampled table was last written with.
        public (int Handle, Puck.SignedDistance.SdfSampledSnapshot Snapshot)[]? SampledWritten { get; set; }
        public int SampledHandles { get; set; }
        public IGpuBuffer? Masks { get; set; }
        public IGpuBuffer? Samples { get; set; }
        public bool PassSetsWritten { get; set; }
        // The Stopwatch reading of the slot's last recorded batch.
        public long LastRecorded { get; set; }
    }
}
