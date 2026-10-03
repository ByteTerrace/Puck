using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>One residency's queue-ordered cache and host regions. Package builds retain it across tier replacements.</summary>
public sealed class SdfIndirectCache : IDisposable {
    private readonly GpuRegionCopyPool m_copies;
    private readonly WorkCounterSet m_work;
    private readonly Dictionary<IrradianceBrickKey, int> m_slots = [];
    private readonly HashSet<IrradianceBrickKey> m_placed = [];
    private readonly SortedSet<int>[] m_free;
    private readonly byte[] m_bricks;
    private readonly byte[] m_updates;
    private readonly uint[] m_traceStates;

    private IrradianceSchedule m_schedule;
    private IrradianceFramePlan? m_pending;
    private int m_holds = 1;
    private bool m_completed;

    /// <summary>Creates the fixed tier pool and its region rings.</summary>
    public SdfIndirectCache(SdfIndirectLayout layout, GpuDeviceServices gpu, GpuMemoryProfile profile, IGpuComputePipeline copyPipeline, float farDistance, WorkCounterSet work, uint epoch) {
        Layout = layout;
        FarDistance = farDistance;
        Epoch = epoch;
        m_work = work;
        m_schedule = NewSchedule();
        m_free = new SortedSet<int>[layout.Pools.Count];
        var offset = 0;

        for (var level = 0; (level < m_free.Length); level++) {
            m_free[level] = new SortedSet<int>(collection: Enumerable.Range(offset, layout.Pools[level]));
            offset += layout.Pools[level];
        }
        m_bricks = new byte[(layout.BrickCapacity * 16)];
        m_updates = new byte[((layout.TraceBudget + (2 * layout.ClassifyBudget)) * 16)];
        m_traceStates = new uint[layout.ProbeCapacity];
        using var scope = new GpuCreationScope();

        Buffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: new GpuObjectName(owner: "sdf-indirect", part: "cache"), sizeBytes: layout.ByteLength, usage: GpuBufferUsage.Storage));
        var names = new[] { "bricks", "updates", "directions", "trace-states" }.Select(selector: part => new GpuObjectName(owner: "sdf-indirect", part: part)).ToArray();

        m_copies = scope.Own(created: new GpuRegionCopyPool(bindings: gpu.Bindings, copyPipeline: copyPipeline, name: new GpuObjectName(owner: "sdf-indirect", part: "region-copies"), regions: names, slotCount: SdfWorldTables.FrameRingSize));
        var lengths = new[] { m_bricks.Length, m_updates.Length, (layout.RaysPerProbe * 16), (m_traceStates.Length * sizeof(uint)) };
        var regions = new GpuRegion[lengths.Length];

        Regions = regions;
        for (var region = 0; (region < lengths.Length); region++) {
            regions[region] = scope.Own(created: new GpuRegion(bindings: gpu.Bindings, buffers: gpu.BufferFactory, byteCount: lengths[region], copyPipeline: copyPipeline,
                copySets: m_copies.Region(index: region), memory: GpuResidency.RingMemory(profile: profile), name: names[region],
                policy: GpuResidency.Select(byteCount: ((ulong)lengths[region]), profile: profile, readersInFlight: true), recorder: gpu.Recorder, slotCount: SdfWorldTables.FrameRingSize));
        }
        var directions = new float[(layout.RaysPerProbe * 4)];

        for (var ray = 0; (ray < layout.RaysPerProbe); ray++) {
            var direction = IrradianceLattice.BaseDirection(count: layout.RaysPerProbe, index: ray);

            directions[(ray * 4)] = ((float)direction.X);
            directions[((ray * 4) + 1)] = ((float)direction.Y);
            directions[((ray * 4) + 2)] = ((float)direction.Z);
        }
        Regions[2].Write(offset: 0, bytes: MemoryMarshal.AsBytes(span: directions.AsSpan()));
        ClearBricks();
        scope.Complete();
    }

    /// <summary>Gets the tier's exact layout.</summary>
    public SdfIndirectLayout Layout { get; }
    /// <summary>Gets the residency-owned allocation published by the graph.</summary>
    public IGpuBuffer Buffer { get; }
    /// <summary>Gets the brick, update, direction and submitted-strata regions.</summary>
    public IReadOnlyList<GpuRegion> Regions { get; }
    /// <summary>Gets the current geometry epoch.</summary>
    public uint Epoch { get; private set; }
    /// <summary>Gets the successful submission generation used by immutable feedback proofs.</summary>
    public uint Frame { get; private set; } = 1;
    /// <summary>Gets the full-ray far distance.</summary>
    public float FarDistance { get; }
    /// <summary>Gets the pending placement count.</summary>
    public int PlaceCount => (m_pending?.Placed.Count ?? 0);
    /// <summary>Gets the pending partition count.</summary>
    public int ClassifyCount => (m_pending?.Classified.Count ?? 0);
    /// <summary>Gets the pending trace stratum count.</summary>
    public int TraceCount => (m_pending?.Traces.Count ?? 0);
    /// <summary>Gets whether this schedule has GPU work.</summary>
    public bool HasWork => (((PlaceCount + ClassifyCount) + TraceCount) != 0);
    /// <summary>Gets whether publication owes a schedule, including a table-only eviction.</summary>
    public bool NeedsPublish => (m_pending is not null);
    /// <summary>Gets whether all current demand has completed a successful trace submission.</summary>
    public bool IsComplete => (m_schedule.IsComplete && (m_pending is null));
    /// <summary>Gets every GPU byte, including region rings and shadows.</summary>
    public GpuMemoryBytes Bytes => Regions.Aggregate(new GpuMemoryBytes(DeviceLocal: Buffer.SizeBytes, HostVisible: 0), (bytes, region) => (bytes + region.OwnedBytes));

    internal bool IsDisposed => (Volatile.Read(location: ref m_holds) <= 0);

    /// <summary>Retains the allocation for a graph's lifetime.</summary>
    public SdfIndirectCache Retain() {
        var holds = Volatile.Read(location: ref m_holds);

        while (true) {
            ObjectDisposedException.ThrowIf(condition: (holds <= 0), instance: this);
            var observed = Interlocked.CompareExchange(comparand: holds, location1: ref m_holds, value: (holds + 1));

            if (observed == holds) { return this; }
            holds = observed;
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (Interlocked.Decrement(location: ref m_holds) != 0) { return; }
        foreach (var region in Regions) { region.Dispose(); }
        m_copies.Dispose();
        Buffer.Dispose();
    }
    /// <summary>Invalidates all slots on a program upload; no old cell or proof remains valid.</summary>
    public void Reset(uint epoch) {
        Epoch = epoch;
        Frame = 1;
        m_schedule = NewSchedule();
        m_pending = null;
        m_slots.Clear();
        m_placed.Clear();
        Array.Clear(array: m_traceStates);
        Regions[3].Write(offset: 0, bytes: MemoryMarshal.AsBytes(span: m_traceStates.AsSpan()));
        var offset = 0;

        for (var level = 0; (level < m_free.Length); level++) {
            m_free[level].Clear();
            m_free[level].UnionWith(other: Enumerable.Range(offset, Layout.Pools[level]));
            offset += Layout.Pools[level];
        }
        m_completed = false;
        ClearBricks();
    }
    /// <summary>Plans once until a successful submission commits the same list.</summary>
    public void Plan(IrradianceFrameInputs inputs) {
        if (m_pending is not null) { return; }
        m_pending = m_schedule.Frame(inputs: inputs);
        foreach (var key in m_pending.Evicted) {
            Array.Clear(array: m_traceStates, index: (m_slots[key] * SdfIndirectLayout.ProbesPerBrick), length: SdfIndirectLayout.ProbesPerBrick);
            m_free[key.Level].Add(item: m_slots[key]); m_slots.Remove(key: key); m_placed.Remove(item: key);
        }
        foreach (var key in m_pending.Allocated) {
            var slot = m_free[key.Level].Min;

            m_free[key.Level].Remove(item: slot);
            m_slots.Add(key: key, value: slot);
        }
        ClearBricks();
        m_placed.UnionWith(other: m_pending.Placed);
        foreach (var (key, slot) in m_slots) {
            if (m_placed.Contains(item: key)) { Write(m_bricks, slot, key.X, key.Y, key.Z, key.Level); }
        }
        Regions[0].Write(bytes: m_bricks, offset: 0);
        var row = 0;

        foreach (var key in m_pending.Placed) { Write(m_updates, row++, m_slots[key], 0, key.Level, 0); }
        foreach (var key in m_pending.Classified) { Write(m_updates, row++, m_slots[key], 0, key.Level, 0); }
        foreach (var update in m_pending.Traces) {
            Write(m_updates, row++, ProbeSlot(probe: update.Probe), update.Stratum, update.Probe.Level, ((int)update.Reason));
        }
        Regions[1].Write(offset: 0, bytes: m_updates.AsSpan(length: (row * 16), start: 0));
        Regions[3].Write(offset: 0, bytes: MemoryMarshal.AsBytes(span: m_traceStates.AsSpan()));
        if (!HasWork && (m_pending.Allocated.Count == 0) && (m_pending.Evicted.Count == 0)) { m_pending = null; }
    }
    /// <summary>Counts and commits only the schedule actually submitted.</summary>
    public void Submitted() {
        if (m_pending is not { } plan) { return; }
        foreach (var update in plan.Traces) {
            m_traceStates[ProbeSlot(probe: update.Probe)] |= (1u << update.Stratum);
        }
        m_work.Add(SdfIndirectWork.Rays, (plan.Traces.Count * IrradianceLattice.RaysPerStratum));
        m_work.Add(SdfIndirectWork.Probes, plan.Traces.Count);
        foreach (var reason in Enum.GetValues<IrradianceUpdateReason>()) { Count($"indirect.probes.scheduled.{reason.ToString().ToLowerInvariant()}", plan.Traces.Count(predicate: update => (update.Reason == reason))); }
        foreach (var (keys, action) in new[] { (plan.Allocated, "allocated"), (plan.Evicted, "evicted"), (plan.Refused, "refused") }) {
            for (var level = 0; (level < Layout.Levels.Count); level++) { Count($"indirect.bricks.{action}.{Layout.Levels[level].Name}", keys.Count(predicate: key => (key.Level == level))); }
        }
        if (!m_completed && m_schedule.IsComplete) { Count(amount: 1, name: "indirect.sweeps.completed"); }
        m_completed = m_schedule.IsComplete;
        m_pending = null;
        Frame++;
    }

    private int ProbeSlot(IrradianceProbeKey probe) {
        var brick = IrradianceLattice.BrickOf(key: probe);

        return ((((m_slots[brick] * SdfIndirectLayout.ProbesPerBrick) + (probe.X - (brick.X * 4)))
            + ((probe.Y - (brick.Y * 4)) * 4)) + ((probe.Z - (brick.Z * 4)) * 16));
    }
    private IrradianceSchedule NewSchedule() => new(Layout.Levels, Layout.Pools, Layout.TraceBudget, Layout.ClassifyBudget, FarDistance);
    private void Count(string name, int amount) => m_work.Add(SdfIndirectWork.Kinds.Single(predicate: kind => (kind.Name == name)), amount);
    private void ClearBricks() {
        Array.Clear(array: m_bricks);
        for (var slot = 0; (slot < Layout.BrickCapacity); slot++) { BinaryPrimitives.WriteInt32LittleEndian(destination: m_bricks.AsSpan(start: ((slot * 16) + 12)), value: -1); }
        Regions[0].Write(bytes: m_bricks, offset: 0);
    }
    private static void Write(byte[] bytes, int row, int x, int y, int z, int w) {
        var span = bytes.AsSpan(length: 16, start: (row * 16));

        BinaryPrimitives.WriteInt32LittleEndian(destination: span, value: x);
        BinaryPrimitives.WriteInt32LittleEndian(destination: span[4..], value: y);
        BinaryPrimitives.WriteInt32LittleEndian(destination: span[8..], value: z);
        BinaryPrimitives.WriteInt32LittleEndian(destination: span[12..], value: w);
    }
}
