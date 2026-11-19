using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The probe scene's acceleration structures, on a device created with ray queries whose pipeline set holds the probe
// kernels' ray-query variants (SdfKernelSet.RaysOf): one bottom level a geometry of the published scene, built from the
// mesh region's words in place and compacted, then one top level over the scene's draws, each draw an instance whose
// index is its record's in the uploaded scene. A query whose kernel traces takes the variant once the top level stands for
// the published scene; until then, and wherever the frame's lever leaves ray queries off, it takes the software walk.
//
// Invariant: a structure is disposed only once no job that could read it can still run. Every job is given a serial as
// TryTake admits it and keeps it in m_flying until it is answered or handed back, so every serial below the oldest flying
// one has landed: that is the client's completion point, the shape GpuRetirementQueue releases by. A structure leaves
// service only through Retire, which holds it until the last serial admitted so far has landed, and DrainRetired releases
// it then. A job admitted before Retire may hold the structure (its query's Top, its build's inputs); one admitted after
// cannot, since Retire runs under the gate after the structure stopped being published. Nothing else disposes a structure
// while the client lives: Bottom and Top expose none, and Dispose, which frees the rest, runs after the service drained.
internal sealed partial class SdfMeshProbeGpuWork {
    private readonly IGpuAccelerationStructures? m_structures;
    private readonly nint m_raysPool;
    private readonly Dictionary<SdfMeshIrradianceScene.Geometry, Bottom> m_bottoms = new(ReferenceEqualityComparer.Instance);
    // TryTakeStructures' scratch: the bottoms one pass has already taken into its build or compaction.
    private readonly HashSet<Bottom> m_takenBottoms = new(ReferenceEqualityComparer.Instance);
    private readonly GpuRetirementQueue<IGpuAccelerationStructure> m_retired = new();
    private readonly List<long> m_flying = [];
    // Seconds a structure job takes a triangle (a bottom's build or copy) or an instance (a top's build).
    private readonly SdfGpuWorkRate m_structureRate = new(priorSecondsPerUnit: 2e-8);
    private long m_nextSerial = 1;
    private Top? m_top;
    private bool m_structureInFlight;
    // A structure job the service handed back (no room, a failed recording) waits this long before the next is offered,
    // doubling to MostStructureBackoff while they keep coming back, so the software walk's queries are taken meanwhile.
    // A finished structure job or a new published scene clears it.
    private static readonly TimeSpan LeastStructureBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MostStructureBackoff = TimeSpan.FromSeconds(8);
    private TimeSpan m_structureBackoff;
    private long m_structureRetryAt;
    private SdfMeshIrradianceScene? m_structureBackoffScene;
    private long m_structureBytes;
    private long m_topsBuilt;

    /// <summary>Returns the descriptors the client's ray-query pool holds on a device that runs the variants: a trace
    /// set and a relight set a slot, the whole of each ray-query interface's groups.</summary>
    internal static GpuDescriptorPoolSizes RaysPoolSizes {
        get {
            var sizes = GpuDescriptorPoolSizes.ForGroups(SdfWorldInterfaces.ProbeTraceRaysLayout.PipelineLayout(GpuShaderStage.Compute).Groups) + GpuDescriptorPoolSizes.ForGroups(SdfWorldInterfaces.ProbeRelightRaysLayout.PipelineLayout(GpuShaderStage.Compute).Groups);
            var total = sizes;

            for (var slot = 1; (slot < SdfGpuWork.SlotCount); slot++) { total += sizes; }

            return total;
        }
    }

    /// <summary>Returns whether a device runs the probe kernels' ray-query variants and has acceleration structures: the
    /// one predicate the client creates its ray-query pool and sets by, and the tables' admission counts that pool by
    /// (<see cref="SdfWorldTables.DescriptorPoolsOf"/>).</summary>
    /// <param name="device">The device.</param>
    /// <returns><see langword="true"/> when the device runs every variant the client dispatches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    internal static bool RunsRays(IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return ((device.Services.AccelerationStructures is not null) &&
            SdfWorldPipelines.Runs(device: device, kernel: SdfKernel.ProbeTraceRays) && SdfWorldPipelines.Runs(device: device, kernel: SdfKernel.ProbeRelightRays) &&
            SdfWorldPipelines.Runs(device: device, kernel: SdfKernel.ProbeCameraSampleRays));
    }
    // The device's structures when it runs every ray-query variant the client dispatches, or null. The pipeline set leases
    // exactly the variants the device runs (SdfWorldPipelines.Acquire), so this agrees with RunsRays.
    private static IGpuAccelerationStructures? StructuresOf(SdfWorldTables tables) =>
        RunsRays(device: tables.Device) ? tables.Gpu.AccelerationStructures : null;
    // The top level a query whose kernel traces takes: the published one when it stands for the published scene, the
    // frame's lever leaves ray queries on and the variant's pipeline is built; null takes the software walk. Under the
    // gate, as TryTake admits the query.
    private Top? TopFor(Job query) {
        var kernel = KernelOf(query: query);

        return ((m_structures is not null) && m_volume.RayQueries && (m_top is { } top) && SdfMeshIrradianceScene.SameGpuContent(first: top.Scene, second: m_scene) &&
            (SdfKernelSet.RaysOf(kernel: kernel) is var rays) && (rays != kernel) && m_tables.Pipelines.IsBuilt(kernel: rays))
            ? top
            : null;
    }
    private static SdfKernel KernelOf(Job query) => KindOf(query: query) switch {
        JobKind.CameraSample => SdfKernel.ProbeCameraSample,
        JobKind.Select => SdfKernel.ProbeSelect,
        JobKind.Relight => SdfKernel.ProbeRelight,
        _ => SdfKernel.ProbeTrace,
    };

    // Admits a job under the gate: its serial joins the flying set, so nothing it may read is disposed before its answer.
    private long Admit() {
        var serial = m_nextSerial++;

        m_flying.Add(item: serial);

        return serial;
    }
    // Ends a job's flight, then disposes what no flying job can still read. Under the gate.
    private void Land(long serial) {
        if (serial != 0L) { _ = m_flying.Remove(item: serial); }
        DrainRetired();
    }
    // Takes a structure out of service: it is disposed once every job admitted before now has landed. Under the gate.
    private void Retire(IGpuAccelerationStructure? structure) {
        if (structure is null) { return; }
        m_retired.Retire(after: (ulong)(m_nextSerial - 1L), item: structure);
    }
    // Releases what every job admitted before its retirement has landed past: every serial below the oldest flying one,
    // or every serial admitted when none flies. Under the gate.
    private void DrainRetired() {
        var oldest = m_nextSerial;

        foreach (var serial in m_flying) { oldest = Math.Min(val1: oldest, val2: serial); }
        m_retired.Release(completed: (ulong)(oldest - 1L), release: m_releaseStructure ??= ReleaseStructure);
    }
    private Action<IGpuAccelerationStructure>? m_releaseStructure;
    private void ReleaseStructure(IGpuAccelerationStructure structure) {
        m_structureBytes -= (long)structure.SizeBytes;
        structure.Dispose();
    }
    private IGpuAccelerationStructure CreateStructure(GpuAccelerationStructureLevel level, ulong bytes, string part) {
        var structure = m_structures!.Create(level: level, name: Name(part: part), sizeBytes: bytes);

        m_structureBytes += (long)structure.SizeBytes;

        return structure;
    }

    // Takes the next structure job the published scene owes, one at a time: the bottoms its geometries lack, cut to
    // the allowance by the measured rate; then their compaction; then the top level over its draws. While work is queued a
    // job past what is left of the allowance waits, decided before any bottom changes state. Under the gate.
    private bool TryTakeStructures(SdfWorldTables tables, SdfGpuWorkAllowance allowance, [NotNullWhen(true)] out object? job, out double estimatedSeconds) {
        job = null;
        estimatedSeconds = 0;
        if (m_structures is null) { return false; }
        ReleaseStaleStructures();
        if (m_structureInFlight || !m_volume.RayQueries || (m_scene is not { } scene) ||
            ((m_top is { } top) && SdfMeshIrradianceScene.SameGpuContent(first: top.Scene, second: scene))) { return false; }
        if ((m_structureBackoff != TimeSpan.Zero) && SdfMeshIrradianceScene.SameGpuContent(first: m_structureBackoffScene, second: scene) &&
            (Stopwatch.GetTimestamp() < m_structureRetryAt)) { return false; }

        var count = scene.GpuInstanceCount;

        if (count > (int)GpuAccelerationStructureInstance.MaxIndex) { return false; }

        var build = new List<(Bottom Bottom, SdfMesh Mesh)>();
        var compact = new List<Bottom>();
        var most = allowance.UnitsWithin(most: long.MaxValue, rate: m_structureRate);
        var units = 0L;
        // The first bottom held back while work is queued, whose build the next take waits for room for.
        var held = 0L;
        var ready = true;

        // Many instances share one geometry's bottom: each bottom is taken once, whichever instance names it first.
        m_takenBottoms.Clear();
        for (var index = 0; (index < count); index++) {
            var (geometry, mesh, _) = scene.GpuInstance(index: index);

            if (geometry.Count == 0) { continue; }
            if (!m_bottoms.TryGetValue(key: geometry, value: out var bottom)) {
                bottom = new Bottom(geometry: geometry);
                m_bottoms.Add(key: geometry, value: bottom);
            }
            if (bottom.State == BottomState.Ready) { continue; }
            ready = false;
            if (bottom.State == BottomState.Wanted) {
                if (((build.Count != 0) || allowance.Queued) && ((units + geometry.Count) > most)) {
                    if ((build.Count == 0) && (held == 0L)) { held = geometry.Count; }
                    continue;
                }
                if (!m_takenBottoms.Add(item: bottom)) { continue; }
                build.Add(item: (bottom, mesh));
                units += geometry.Count;
            } else if ((bottom.State == BottomState.Built) && m_takenBottoms.Add(item: bottom)) {
                compact.Add(item: bottom);
            }
        }
        if (build.Count != 0) {
            if (!tables.TryProbeScene(scene: scene, placements: out var placements, meshes: out var meshes, version: out _)) { return false; }
            var builds = new StructureJob.Build[build.Count];

            for (var index = 0; (index < build.Count); index++) {
                var (bottom, mesh) = build[index];
                var placement = placements[mesh];
                var inputs = GpuAccelerationStructureInputs.Triangles(
                    indexOffsetBytes: (ulong)placement.IndexWord * sizeof(uint),
                    source: meshes,
                    triangleCount: (uint)bottom.Geometry.Count,
                    vertexCount: (uint)bottom.Geometry.Positions.Length,
                    vertexOffsetBytes: (ulong)placement.VertexWord * sizeof(uint),
                    vertexStrideBytes: (uint)SdfMeshRecord.VertexWords * sizeof(uint)
                );

                builds[index] = new StructureJob.Build(Bottom: bottom, Inputs: inputs, Sizes: m_structures.Size(inputs: in inputs));
                bottom.State = BottomState.Building;
            }
            job = new StructureJob(serial: Admit(), kind: StructureKind.Bottoms) { Builds = builds, Units = units };
        } else if (compact.Count != 0) {
            units = compact.Sum(selector: static bottom => (long)bottom.Geometry.Count);
            if (!allowance.Admits(seconds: m_structureRate.SecondsOf(units: units))) {
                estimatedSeconds = m_structureRate.SecondsOf(units: units);
                return false;
            }
            foreach (var bottom in compact) { bottom.State = BottomState.Compacting; }
            job = new StructureJob(serial: Admit(), kind: StructureKind.Compaction) { Compactions = [.. compact], Units = units };
        } else if (ready) {
            if (!allowance.Admits(seconds: m_structureRate.SecondsOf(units: count))) {
                estimatedSeconds = m_structureRate.SecondsOf(units: count);
                return false;
            }
            var bottoms = new Bottom?[count];

            for (var index = 0; (index < count); index++) {
                var geometry = scene.GpuInstance(index: index).Geometry;

                bottoms[index] = ((geometry.Count == 0) ? null : m_bottoms[geometry]);
            }
            job = new StructureJob(serial: Admit(), kind: StructureKind.Top) { Scene = scene.Acquire(), Instances = bottoms, Units = count };
        } else {
            estimatedSeconds = m_structureRate.SecondsOf(units: held);
            return false;
        }

        m_structureInFlight = true;
        estimatedSeconds = m_structureRate.SecondsOf(units: ((StructureJob)job).Units);

        return true;
    }

    // Records a structure job: every buffer and readback it holds is reserved before any structure is created, so a job
    // the device has no room for is handed back owning nothing.
    private void RecordStructures(StructureJob job, in SdfGpuWorkRecording recording) {
        var recorder = recording.Recorder;
        var command = recording.CommandBuffer;

        switch (job.Kind) {
            case StructureKind.Bottoms: {
                var builds = job.Builds!;
                var scratchBytes = builds.Max(selector: static build => build.Sizes.ScratchBytes);
                var sizeBytes = (ulong)builds.Length * sizeof(ulong);
                var scratch = recording.DeviceBuffer(part: "structure-scratch", bytes: scratchBytes, sizing: SdfGpuWorkSizing.Fit, usage: (GpuBufferUsage.Storage | GpuBufferUsage.AccelerationStructureInput));
                var sizes = recording.DeviceBuffer(part: "structure-sizes", bytes: sizeBytes, sizing: SdfGpuWorkSizing.Fit, usage: GpuBufferUsage.Storage);

                recording.ReserveReadback(bytes: sizeBytes);
                for (var index = 0; (index < builds.Length); index++) {
                    var build = builds[index];
                    var inputs = build.Inputs;
                    var structure = CreateStructure(bytes: build.Sizes.StructureBytes, level: GpuAccelerationStructureLevel.Bottom, part: "bottom");

                    job.Created.Add(item: structure);
                    recorder.BuildAccelerationStructure(commandBufferHandle: command, destination: structure, inputs: in inputs, scratch: scratch);
                    recorder.WriteCompactedSize(commandBufferHandle: command, destination: sizes, destinationOffsetBytes: (ulong)index * sizeof(ulong), source: structure);
                }
                recording.CopyToReadback(bytes: sizeBytes, source: sizes);
                break;
            }
            case StructureKind.Compaction:
                foreach (var bottom in job.Compactions!) {
                    var compacted = CreateStructure(bytes: bottom.CompactedBytes, level: GpuAccelerationStructureLevel.Bottom, part: "bottom compacted");

                    job.Created.Add(item: compacted);
                    recorder.CompactAccelerationStructure(commandBufferHandle: command, destination: compacted, source: bottom.Structure!);
                }
                break;
            default: {
                var bottoms = job.Instances!;
                const int RecordBytes = GpuAccelerationStructureInstance.Bytes;
                var instanceBytes = (ulong)Math.Max(val1: 1, val2: bottoms.Length) * (ulong)RecordBytes;
                var instances = recording.HostBuffer(part: "structure-instances", bytes: instanceBytes, sizing: SdfGpuWorkSizing.Fit, usage: (GpuBufferUsage.Storage | GpuBufferUsage.AccelerationStructureInput));
                var inputs = GpuAccelerationStructureInputs.Instances(instanceCount: 0U, instanceOffsetBytes: 0UL, source: instances);
                var records = ArrayPool<byte>.Shared.Rent(minimumLength: (int)instanceBytes);
                var written = 0U;

                try {
                    for (var index = 0; (index < bottoms.Length); index++) {
                        if (bottoms[index] is not { Structure: { } bottom }) { continue; }
                        var transform = job.Scene!.GpuInstance(index: index).ObjectToWorld;

                        GpuAccelerationStructureInstance.Write(bottom: bottom, destination: records.AsSpan(start: (int)(written * (uint)RecordBytes), length: RecordBytes), index: (uint)index, objectToWorld: in transform);
                        written++;
                    }
                    instances.Write<byte>(data: records.AsSpan(start: 0, length: (int)(written * (uint)RecordBytes)));
                } finally {
                    ArrayPool<byte>.Shared.Return(array: records);
                }

                inputs = inputs with { Count = written };
                var sizes = m_structures!.Size(inputs: in inputs);
                var scratch = recording.DeviceBuffer(part: "structure-scratch", bytes: Math.Max(val1: 1UL, val2: sizes.ScratchBytes), sizing: SdfGpuWorkSizing.Fit, usage: (GpuBufferUsage.Storage | GpuBufferUsage.AccelerationStructureInput));
                var structure = CreateStructure(bytes: Math.Max(val1: 1UL, val2: sizes.StructureBytes), level: GpuAccelerationStructureLevel.Top, part: "top");

                job.Created.Add(item: structure);
                recorder.BuildAccelerationStructure(commandBufferHandle: command, destination: structure, inputs: in inputs, scratch: scratch);
                break;
            }
        }
    }

    // Answers a structure job: a bottom build reads each compacted size, a compaction puts its copies in service and
    // retires the uncompacted builds, and a top level is published, retiring the one it replaces and every bottom
    // neither it nor the published scene names.
    private void CompleteStructures(StructureJob job, SdfGpuWorkReadback readback) {
        Span<byte> sizes = stackalloc byte[0];
        byte[]? read = null;

        try {
            if (readback.Length != 0) {
                read = new byte[readback.Length];
                readback.Read(destination: read);
                sizes = read;
            }
        } finally {
            readback.Release();
        }
        lock (m_gate) {
            m_structureInFlight = false;
            m_structureBackoff = TimeSpan.Zero;
            if (readback.GpuSeconds is { } seconds) {
                m_structureRate.Observe(seconds: seconds, units: Math.Max(val1: 1L, val2: job.Units));
                m_volume.Counters.Add(SdfMeshIrradiance.GpuStructureNanoseconds, (long)(seconds * 1e9));
            }
            switch (job.Kind) {
                case StructureKind.Bottoms:
                    for (var index = 0; (index < job.Builds!.Length); index++) {
                        var bottom = job.Builds[index].Bottom;

                        bottom.Structure = job.Created[index];
                        bottom.CompactedBytes = BinaryPrimitives.ReadUInt64LittleEndian(source: sizes[(index * sizeof(ulong))..]);
                        // A size the driver did not answer keeps the uncompacted build in service.
                        bottom.State = ((bottom.CompactedBytes == 0UL) ? BottomState.Ready : BottomState.Built);
                    }
                    m_volume.Counters.Add(SdfMeshIrradiance.StructuresBuilt, job.Builds.Length);
                    break;
                case StructureKind.Compaction:
                    for (var index = 0; (index < job.Compactions!.Length); index++) {
                        var bottom = job.Compactions[index];

                        Retire(structure: bottom.Structure);
                        bottom.Structure = job.Created[index];
                        bottom.State = BottomState.Ready;
                    }
                    break;
                default:
                    var replaced = m_top;

                    m_top = new Top(structure: job.Created[0], instances: job.Instances!) { Scene = job.Scene! };
                    job.Scene = null;
                    m_topsBuilt++;
                    if (replaced is not null) {
                        Retire(structure: replaced.Structure);
                        replaced.Scene.Dispose();
                    }
                    RetireUnnamedBottoms();
                    break;
            }
            job.Created.Clear();
            Land(serial: job.Serial);
        }
    }
    // Hands a structure job back: what it created leaves service unused, its bottoms are owed again, and structure work
    // backs off for the scene so the software walk's queries are taken first. Under the gate.
    private void AbandonStructures(StructureJob job) {
        m_structureInFlight = false;
        m_structureBackoff = ((m_structureBackoff == TimeSpan.Zero) ? LeastStructureBackoff : TimeSpan.FromTicks(Math.Min(MostStructureBackoff.Ticks, 2 * m_structureBackoff.Ticks)));
        m_structureRetryAt = Stopwatch.GetTimestamp() + (long)(m_structureBackoff.TotalSeconds * Stopwatch.Frequency);
        m_structureBackoffScene?.Dispose();
        m_structureBackoffScene = m_scene?.Acquire();
        foreach (var structure in job.Created) { Retire(structure: structure); }
        job.Created.Clear();
        if (job.Builds is { } builds) {
            foreach (var build in builds) { build.Bottom.State = BottomState.Wanted; }
        }
        if (job.Compactions is { } compactions) {
            foreach (var bottom in compactions) { bottom.State = BottomState.Built; }
        }
        job.Scene?.Dispose();
        job.Scene = null;
        Land(serial: job.Serial);
    }
    // Takes out of service what the current publication no longer uses: with the lever off, every structure; otherwise a
    // top level whose scene is no longer the published one, and the bottoms only it named. A top whose scene the client
    // replaced with one exporting the same words (a trimmed copy) claims the replacement instead, so it pins no scene the
    // client let go. Under the gate.
    private void ReleaseStaleStructures() {
        if (m_top is { } top) {
            if (!m_volume.RayQueries || !SdfMeshIrradianceScene.SameGpuContent(first: top.Scene, second: m_scene)) {
                Retire(structure: top.Structure);
                top.Scene.Dispose();
                m_top = null;
                RetireUnnamedBottoms();
            } else if (!ReferenceEquals(objA: top.Scene, objB: m_scene)) {
                var adopted = m_scene!.Acquire();

                top.Scene.Dispose();
                top.Scene = adopted;
            }
        }
        if (!m_volume.RayQueries && (m_bottoms.Count != 0)) { RetireUnnamedBottoms(); }
        if ((m_structureBackoffScene is { } deferred) && !SdfMeshIrradianceScene.SameGpuContent(first: deferred, second: m_scene)) {
            deferred.Dispose();
            m_structureBackoffScene = null;
            m_structureBackoff = TimeSpan.Zero;
        }
        // What it retired goes now if no job admitted before it still flies, rather than at the next job's landing, which
        // a residency nothing renders may never reach.
        DrainRetired();
    }
    // Retires every bottom neither the published top nor, while the lever is on, the published scene names. Under the
    // gate.
    private void RetireUnnamedBottoms() {
        var named = new HashSet<SdfMeshIrradianceScene.Geometry>(comparer: ReferenceEqualityComparer.Instance);

        if (m_top is { } top) {
            foreach (var bottom in top.Instances) {
                if (bottom is not null) { _ = named.Add(item: bottom.Geometry); }
            }
        }
        if (m_volume.RayQueries && (m_scene is { } scene)) {
            for (var index = 0; (index < scene.GpuInstanceCount); index++) { _ = named.Add(item: scene.GpuInstance(index: index).Geometry); }
        }
        foreach (var (geometry, bottom) in m_bottoms.ToArray()) {
            if (named.Contains(item: geometry) || (bottom.State is BottomState.Building or BottomState.Compacting)) { continue; }
            Retire(structure: bottom.Structure);
            _ = m_bottoms.Remove(key: geometry);
        }
    }
    // What the structures hold, for the volume's memory line: every byte still held (retired ones included), the bottoms'
    // bytes and the triangles they cover, whose ratio is the driver's and compares backends apart from scenes, the top's
    // bytes, and the ready bottoms kept uncompacted because the driver answered no compacted size.
    private string DescribeStructures() {
        var ready = 0;
        var uncompacted = 0;
        var bottomBytes = 0UL;
        var triangles = 0L;

        foreach (var bottom in m_bottoms.Values) {
            if (bottom.State == BottomState.Ready) {
                ready++;
                if (bottom.CompactedBytes == 0UL) { uncompacted++; }
            }
            bottomBytes += bottom.Structure?.SizeBytes ?? 0UL;
            if (bottom.Structure is not null) { triangles += bottom.Geometry.Count; }
        }
        return FormattableString.Invariant($"trace-structures={((m_structures is null) ? "unsupported" : (m_volume.RayQueries ? "on" : "off"))} trace-structure-device-bytes={m_structureBytes} trace-structure-bottom-bytes={bottomBytes} trace-structure-bottom-triangles={triangles} trace-structure-top-bytes={m_top?.Structure.SizeBytes ?? 0UL} trace-structure-bottoms={m_bottoms.Count} trace-structure-bottoms-ready={ready} trace-structure-bottoms-uncompacted={uncompacted} trace-structure-top-instances={m_top?.Instances.Length ?? 0} trace-structure-tops-built={m_topsBuilt} trace-structure-retired={m_retired.Count}");
    }
    // Frees every structure the client holds; the service has drained, so no submission reads them.
    private void DisposeStructures() {
        m_retired.Release(completed: ulong.MaxValue, release: static structure => structure.Dispose());
        foreach (var bottom in m_bottoms.Values) { bottom.Structure?.Dispose(); bottom.Structure = null; }
        m_bottoms.Clear();
        if (m_top is { } top) {
            top.Structure.Dispose();
            top.Scene.Dispose();
            m_top = null;
        }
        m_structureBackoffScene?.Dispose();
        m_structureBackoffScene = null;
        m_structureBytes = 0;
        if (m_raysPool != 0) { m_gpu.Bindings.DestroyPool(m_raysPool); }
    }

    private enum BottomState { Wanted, Building, Built, Compacting, Ready }
    // One geometry's bottom level: wanted, being built, built with its compacted size read, being compacted, or ready,
    // its structure the one a top level names.
    private sealed class Bottom(SdfMeshIrradianceScene.Geometry geometry) {
        public SdfMeshIrradianceScene.Geometry Geometry { get; } = geometry;
        public BottomState State { get; set; }
        public IGpuAccelerationStructure? Structure { get; set; }
        public ulong CompactedBytes { get; set; }
    }
    // The published top level, the scene whose draws are its instances (a claim, moved to an equivalent scene the client
    // adopts), and each instance's bottom (null for a draw of no triangles, which has no instance).
    private sealed class Top(IGpuAccelerationStructure structure, Bottom?[] instances) {
        public IGpuAccelerationStructure Structure { get; } = structure;
        public required SdfMeshIrradianceScene Scene { get; set; }
        public Bottom?[] Instances { get; } = instances;
    }
    private enum StructureKind { Bottoms, Compaction, Top }
    private sealed class StructureJob(long serial, StructureKind kind) {
        public readonly record struct Build(Bottom Bottom, GpuAccelerationStructureInputs Inputs, GpuAccelerationStructureSizes Sizes);

        public long Serial { get; } = serial;
        public StructureKind Kind { get; } = kind;
        public Build[]? Builds { get; init; }
        public Bottom[]? Compactions { get; init; }
        public SdfMeshIrradianceScene? Scene { get; set; }
        public Bottom?[]? Instances { get; init; }
        public long Units { get; init; }
        // The structures the job's recording created, in its builds' or compactions' order.
        public List<IGpuAccelerationStructure> Created { get; } = [];
    }
}
