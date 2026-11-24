using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The volume's batched hit queries on the residency's background GPU service.</summary>
internal sealed partial class SdfMeshProbeGpuWork : ISdfGpuWorkClient {
    private readonly SdfMeshIrradiance m_volume;
    private readonly GpuDeviceServices m_gpu;
    private readonly SdfWorldTables m_tables;
    private readonly IGpuStorageBuffer m_directions;
    private readonly IGpuStorageBuffer m_zero;
    private readonly nint m_pool;
    private readonly Slot[] m_slots = new Slot[SdfGpuWork.SlotCount];
    private readonly Lock m_gate = new();
    private readonly Queue<Job> m_pending = new();
    // The measured GPU cost of each kind's unit (UnitsOf), indexed by JobKind: what TryTake charges a job and what sizes
    // trace, relight and selection jobs to the live frame allowance. Complete measures them on the recording
    // thread; the coordinator reads them as it cuts jobs.
    private readonly SdfGpuWorkRate[] m_rates = [
        new(priorSecondsPerUnit: SdfMeshProbeDepth.HitCount * 1e-8),
        new(priorSecondsPerUnit: 1e-8),
        new(priorSecondsPerUnit: 1e-8),
        new(priorSecondsPerUnit: SdfMeshProbeDepth.HitCount * 1e-8),
    ];
    // The coordinator's window (Run): the parts of one call it has submitted and not yet read, oldest
    // first, at most SdfGpuWork.SlotCount, and the answer arrays the parts answer into, a ring of SlotCount. Part k answers
    // into ring entry k mod SlotCount, which part k + SlotCount reuses only after the coordinator has read part k, since
    // the window holds at most SlotCount parts. The coordinator's alone: one coordinator calls Trace, Select and Relight
    // at a time.
    private readonly Queue<Job> m_window = new();
    private readonly uint[][] m_partAnswers = new uint[SdfGpuWork.SlotCount][];
    private long m_parts;
    // The selection's changes by geometry, ascending, each with the hierarchy walks of it and of every later one
    // (SelectTestsOf).
    private readonly List<(uint Geometry, int Walks)> m_selectNewer = [];
    private bool m_disposed;
    private bool m_stopping;
    private GpuRegion? m_feedback;
    // Whether a relayout's old feedback table waits in the tables' retirement, and the callback that clears this when the
    // retirement releases it.
    private bool m_feedbackRetiring;
    private Action? m_feedbackReleased;
    private readonly GpuFittedBuffer m_hitCache;
    private readonly GpuFittedBuffer m_cameraSampleCache;
    // Where a relight batch's jobs copy their results on the device, at each job's first probe: the batch's last job
    // copies every probe's record from it into the feedback volume (RecordCommit), so the batch commits once, after
    // every one of its relights, and no job holds its slot's readback for another to copy from.
    private readonly GpuFittedBuffer m_batchOutput;
    // Whether a new hit cache's clear, or a new camera-sample cache's fill, is still owed: set as the cache is made and
    // discharged only by the completion of a job that recorded it.
    private bool m_hitCacheOwed;
    private bool m_cameraSampleCacheOwed;
    private int m_feedbackWords;
    private SdfMeshIrradianceScene? m_scene;
    private IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> m_scenePlacements = new Dictionary<SdfMesh, SdfMeshRegionMesh>();
    private long m_placementVersion = -1;
    // The queries taken and not yet answered or handed back, and whether one of them is exclusive (Exclusive).
    // Invariant: a query that replaces, clears or fills state every query shares (the hit cache, the camera-sample cache, the
    // feedback volume's size, the batch output, the published scene) is taken only when no other query is in flight, and
    // no query is taken while it is in flight. TryTake is the one door that admits a query, so each such replacement
    // sees no recorded work that reads what it replaces; every other query reads shared state only through barriers its
    // own recording orders after earlier submissions on the one queue.
    private int m_inFlight;
    private bool m_exclusive;
    // The head job waiting for its scene's meshes to be resident, and since when.
    private static readonly TimeSpan AdmissionWait = TimeSpan.FromSeconds(2);
    private Job? m_waiting;
    private long m_waitingSince;
    private long? m_feedbackRefusedAt;

    public TimeSpan? UntilDue {
        get {
            lock (m_gate) {
                if (m_feedbackRefusedAt is { } refused) { return SdfGpuWork.DeferralRetry - m_volume.Time.GetElapsedTime(refused); }
                return m_waiting is null ? null : AdmissionWait - m_volume.Time.GetElapsedTime(m_waitingSince);
            }
        }
    }

    /// <summary>Returns the descriptors the client's pool holds: a trace set and a relight set a slot, the whole of each
    /// interface's groups.</summary>
    internal static GpuDescriptorPoolSizes PoolSizes {
        get {
            var sizes = GpuDescriptorPoolSizes.ForGroups(SdfWorldInterfaces.ProbeTraceLayout.PipelineLayout(GpuShaderStage.Compute).Groups) + GpuDescriptorPoolSizes.ForGroups(SdfWorldInterfaces.ProbeRelightLayout.PipelineLayout(GpuShaderStage.Compute).Groups);
            var total = sizes;

            for (var slot = 1; (slot < SdfGpuWork.SlotCount); slot++) { total += sizes; }

            return total;
        }
    }

    internal SdfMeshProbeGpuWork(SdfWorldTables tables, SdfMeshIrradiance volume) {
        Array.Fill(m_partAnswers, []);
        m_volume = volume;
        m_gpu = tables.Gpu;
        m_batchOutput = new(m_gpu.BufferFactory, Name("batch-output"));
        m_cameraSampleCache = new(m_gpu.BufferFactory, Name("camera-sample-cache"));
        m_hitCache = new(m_gpu.BufferFactory, Name("hit-cache"), sizing: GpuBufferSizing.Exact);
        m_tables = tables;
        using var scope = new GpuCreationScope();
        var pipeline = tables.Pipeline(SdfKernel.ProbeTrace);
        var relightPipeline = tables.Pipeline(SdfKernel.ProbeRelight);
        m_pool = scope.Own(handle: m_gpu.Bindings.CreatePool(name: Name(part: "pool"), sizes: PoolSizes), release: m_gpu.Bindings.DestroyPool);
        m_directions = scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(name: Name(part: "directions"),
            sizeBytes: SdfMeshProbeDepth.HitCount * 16, usage: GpuBufferUsage.Storage));
        var directions = new Vector4[SdfMeshProbeDepth.HitCount];
        for (var i = 0; i < directions.Length; i++) { directions[i] = new(SdfMeshProbeDepth.RayDirections[i], SdfMeshProbeDepth.RayWeights[i]); }
        m_directions.Write<Vector4>(directions);
        m_zero = scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(name: Name(part: "zero"), sizeBytes: 4, usage: GpuBufferUsage.Storage));
        m_zero.Write<uint>([0]);
        for (var i = 0; i < m_slots.Length; i++) {
            m_slots[i] = new Slot(
                m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: m_pool),
                m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: relightPipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: m_pool));
        }
        // A device that runs the ray-query variants holds a set of each ray-query interface a slot too, from a pool only
        // such a device lays out.
        m_structures = StructuresOf(tables: tables);
        m_shared = tables.Grant.Selector.TraceBottoms;
        if (m_structures is not null) {
            var tracePipeline = tables.Pipeline(SdfKernel.ProbeTraceRays);
            var relightRaysPipeline = tables.Pipeline(SdfKernel.ProbeRelightRays);
            m_raysPool = scope.Own(handle: m_gpu.Bindings.CreatePool(name: Name(part: "rays pool"), sizes: RaysPoolSizes), release: m_gpu.Bindings.DestroyPool);
            foreach (var slot in m_slots) {
                slot.RaysSet = m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: tracePipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: m_raysPool);
                slot.RaysRelightSet = m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: relightRaysPipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: m_raysPool);
            }
        }
        scope.Complete();
    }

    // One coordinator submits a call's jobs and sleeps on their answers; it never holds the device thread. A job owns a
    // scene claim until completion or abandonment. The service owns every slot's fence and readback token.
    // Each call is cut into jobs whose measured cost fits the live frame allowance, sized as each is submitted
    // from the kind's rate as it then stands (Run). Its jobs are independent: each answers its own rows and writes only
    // its own cache slots or batch output rows.
    // A trace also copies each origin's hits into the device hit cache at its slot, so a relight reads them there and the
    // host never uploads hits it read back.
    internal uint[] Trace(SdfMeshIrradianceScene scene, Memory<Vector4> origins, ReadOnlyMemory<uint> cacheSlots, uint hitCapacity, uint[]? reuse = null) =>
        Run(count: origins.Length, rowWords: SdfMeshProbeDepth.HitWords, reuse: reuse, next: start => {
            var count = Math.Min(val1: TraceOriginsPerJob, val2: (origins.Length - start));

            return new Job(scene.Acquire(), origins.Slice(start: start, length: count)) { CacheSlots = cacheSlots.Slice(start: start, length: count), HitCapacity = hitCapacity };
        });
    // The GPU time one job is cut to, read again for every job: the presentation budget's live owed allowance while the
    // volume owes the views light (their first, or what a lighting jump left stale where they look, OwesViews), whose
    // jobs the service takes under it (TryTakeOwed), and its frame allowance otherwise.
    private double FrameAllowanceSeconds => (m_volume.OwesViews ? m_tables.GpuWork.OwedAllowanceSeconds : m_tables.GpuWork.FrameAllowanceSeconds);
    // The origins one trace job holds at most: those whose measured cost fits the frame allowance.
    private int TraceOriginsPerJob => (int)m_rates[(int)JobKind.Trace].UnitsWithin(most: int.MaxValue, seconds: FrameAllowanceSeconds);
    // The predicted rays one relight job holds at most: those whose measured cost fits the frame allowance,
    // within SdfMeshIrradiance.RelightRaysCeiling, and within SdfMeshIrradiance.RelightRaysPerJob until a relight's
    // timestamps have measured the rate (on a queue without timestamps, always). A probe predicted past it goes alone.
    private long RelightRaysWithinShare {
        get {
            var rate = m_rates[(int)JobKind.Relight];

            return rate.UnitsWithin(most: (rate.Measured ? SdfMeshIrradiance.RelightRaysCeiling : SdfMeshIrradiance.RelightRaysPerJob), seconds: FrameAllowanceSeconds);
        }
    }
    // Relights a batch's probes and commits each probe's published record into the feedback volume at its destination
    // slot (none where it is negative). `rays` holds each probe's predicted rays, which cut the batch into jobs within
    // RelightRaysWithinShare. Every job of the batch reads the feedback volume as it stood before the batch, since only
    // the batch's last job commits, after every relight of the batch, so a probe's bounce does not depend on how the batch
    // was cut. That job is cancelled, and the call throws, when an earlier job of the batch did not run or the publication
    // (`geometry`, `window`) no longer stands as it is taken. `updates` reach the feedback volume with the first job.
    // Returns each probe's SdfMeshProbeRelight.ResultWords words, the probes in order.
    internal uint[] Relight(SdfMeshIrradianceScene scene, Memory<Vector4> origins, ReadOnlyMemory<uint> input, Vector4[] lighting,
        int volumeWords, Deltas updates, uint hitCapacity, ReadOnlyMemory<long> rays, ReadOnlyMemory<int> destinations, int geometry, int window,
        uint[]? reuse = null) {
        var batch = new RelightBatch(Probes: origins.Length);

        return Run(count: origins.Length, rowWords: SdfMeshProbeRelight.ResultWords, reuse: reuse, next: start => {
            var predicted = rays.Span;
            var bound = RelightRaysWithinShare;
            var end = start + 1;
            var taken = predicted[start];

            for (; (end < origins.Length) && ((taken + predicted[end]) <= bound); end++) { taken += predicted[end]; }

            var last = (end == origins.Length);

            return new Job(scene.Acquire(), origins[start..end]) {
                Input = input.Slice(start: (start * SdfMeshProbeRelight.InputWords), length: ((end - start) * SdfMeshProbeRelight.InputWords)),
                Lighting = lighting, VolumeWords = volumeWords, Updates = ((start == 0) ? updates : default), HitCapacity = hitCapacity, Rays = taken,
                Batch = batch, Commits = last, Destinations = (last ? destinations : default), Geometry = geometry, Window = window,
            };
        });
    }
    internal IGpuBuffer FeedbackBuffer => m_feedback!.Buffer(0);

    // A selection answers each probe from its own row alone (sdf-probe-select), so it is cut like a trace: jobs of at
    // most the probes whose tests (SelectTestsOf) fit the live frame allowance at the measured rate. A
    // selection over a settled scene is one job of a test a probe; one after the scene changed becomes several, never one
    // job a frame waits on. The rate is read again for every job, so a job measured costlier than its estimate shortens
    // the ones submitted after it, and the tests left are shared evenly among the jobs left, since a short last job would
    // measure its fixed cost as a high rate.
    internal uint[] Select(SdfMeshIrradianceScene scene, Memory<Vector4> centers, ReadOnlyMemory<uint> metadata, Vector4[] changes, uint hitCapacity, uint[]? reuse = null) {
        // Each remembered change, oldest first, as SdfMeshIrradiance.SelectionRows packs it: its geometry and the rays'
        // walks of it a probe older than it pays, one a hierarchy level, summed with every later change's.
        var count = (int)changes[1].X;
        m_selectNewer.Clear();
        var walks = 0;
        for (var change = (count - 1); (change >= 0); change--) {
            var head = changes[SdfMeshProbeRelight.SelectionHeaderRows + change];
            var flags = BitConverter.SingleToUInt32Bits(value: head.W);
            walks += (((flags & 1u) != 0) || (BitConverter.SingleToUInt32Bits(value: head.Z) == 0)) ? 0 : (int)((flags >> 8) + 1u);
            m_selectNewer.Insert(index: 0, item: (BitConverter.SingleToUInt32Bits(value: head.X), walks));
        }
        var left = 0L;
        for (var probe = 0; (probe < centers.Length); probe++) {
            left += SelectTestsOf(row: metadata.Span.Slice(start: (probe * SdfMeshProbeRelight.SelectionInputWords), length: SdfMeshProbeRelight.SelectionInputWords), changes: changes);
        }
        return Run(count: centers.Length, rowWords: 1, reuse: reuse, next: start => {
            var most = m_rates[(int)JobKind.Select].UnitsWithin(most: long.MaxValue, seconds: FrameAllowanceSeconds);
            var share = left.CeilingDivide(divisor: Math.Max(val1: 1L, val2: left.CeilingDivide(divisor: most)));
            var rows = metadata.Span;
            var count = 0;
            var tests = 0L;

            while ((start + count) < centers.Length) {
                var probe = SelectTestsOf(row: rows.Slice(start: ((start + count) * SdfMeshProbeRelight.SelectionInputWords), length: SdfMeshProbeRelight.SelectionInputWords), changes: changes);
                if ((count != 0) && ((tests + probe) > share)) { break; }
                tests += probe;
                count++;
            }
            left -= tests;
            return new Job(scene.Acquire(), centers.Slice(start: start, length: count)) {
                Input = metadata.Slice(start: (start * SdfMeshProbeRelight.SelectionInputWords), length: (count * SdfMeshProbeRelight.SelectionInputWords)),
                Lighting = changes, Selection = true, HitCapacity = hitCapacity, Tests = tests,
            };
        });
    }
    // The tests one probe of a selection is expected to cost (sdf-probe-select): its own row, and for a probe traced
    // against an earlier geometry, its rays' walk down the hierarchy of each later change. The units are the rate's
    // only, which the jobs' timestamps measure: a walk that visits more of a change near the probe costs more than this
    // counts, and the measured rate follows.
    private long SelectTestsOf(ReadOnlySpan<uint> row, Vector4[] changes) {
        if (((row[6] | row[7]) == 0) || (row[0] == BitConverter.SingleToUInt32Bits(value: changes[0].X))) { return 1L; }
        var newer = 0;
        for (var index = (m_selectNewer.Count - 1); ((index >= 0) && (m_selectNewer[index].Geometry > row[0])); index--) { newer = m_selectNewer[index].Walks; }
        return (1L + ((long)newer * SdfMeshProbeDepth.HitCount));
    }
    internal Task<uint[]> CameraSample(SdfMeshIrradianceScene scene, Memory<Vector4> origins, Vector4[] lighting, int words, int geometry, int window, uint[] result) =>
        SubmitAsync(new Job(scene.Acquire(), origins) { Input = new uint[] { 0 }, Lighting = lighting, CameraSample = true, VolumeWords = words, Geometry = geometry, Window = window, Result = result });
    // Runs one call of `count` rows, `rowWords` words each, as the jobs `next` cuts from the row it is handed, keeping up
    // to SdfGpuWork.SlotCount of them in flight, so a call of many jobs waits about one round trip per SlotCount jobs
    // rather than one a job. The coordinator's window holds a call's parts submitted and not yet read, oldest first, and
    // part k answers into ring entry k mod SlotCount, which part k + SlotCount reuses only once part k was read. Returns
    // the rows in order, in `reuse` itself or in a larger array when it is too small: the caller keeps the returned array
    // for its next call and reads only the rows it asked for, since the words beyond are an earlier call's. Every job has
    // been answered or cancelled before this returns or throws, so the caller may then overwrite the arrays its jobs'
    // memories view. One coordinator calls this at a time.
    private uint[] Run(int count, int rowWords, uint[]? reuse, Func<int, Job> next) {
        var words = (count * rowWords) + 1;
        var answer = ((reuse is { } array) && (array.Length >= words)) ? array : new uint[words];

        try {
            for (var start = 0; ((start < count) || (m_window.Count > 0));) {
                while ((start < count) && (m_window.Count < SdfGpuWork.SlotCount)) {
                    var job = next(start);
                    var ring = (int)(m_parts++ % SdfGpuWork.SlotCount);

                    job.First = start;
                    job.Result = ArrayCapacity.EnsureScratch(ref m_partAnswers[ring], (job.Origins.Length * rowWords) + 1);
                    m_window.Enqueue(item: job);
                    _ = SubmitAsync(job: job);
                    start += job.Origins.Length;
                }

                var done = m_window.Peek();
                var part = Await(answer: done.Answer.Task);

                _ = m_window.Dequeue();
                part.AsSpan(start: 0, length: (done.Origins.Length * rowWords)).CopyTo(answer.AsSpan(start: (done.First * rowWords)));
            }
        } catch {
            // Whatever failed (a part's answer, or cutting the next part), every part already submitted is waited out, so
            // the window is empty for the next call and no job still reads the caller's arrays.
            while (m_window.TryDequeue(out var rest)) {
                try { _ = Await(answer: rest.Answer.Task); } catch (Exception) { }
            }
            throw;
        }
        return answer;
    }
    // Blocks the coordinator on a part's answer, marked as waiting for the device (AwaitsDevice) while it blocks.
    private uint[] Await(Task<uint[]> answer) {
        Volatile.Write(location: ref m_awaitsDevice, value: true);
        try { return answer.GetAwaiter().GetResult(); } finally { Volatile.Write(location: ref m_awaitsDevice, value: false); }
    }
    private bool m_awaitsDevice;
    /// <summary>Gets whether the coordinator is blocked on the answer of a job it submitted: the volume's background worker
    /// waits for the device, not for host work. Safe to read from any thread.</summary>
    internal bool AwaitsDevice => Volatile.Read(location: ref m_awaitsDevice);
    // A part submitted while the client stops is answered cancelled, as every pending job is, so its coordinator's window
    // never waits on a job that cannot be taken.
    private Task<uint[]> SubmitAsync(Job job) {
        lock (m_gate) {
            if (m_stopping || m_disposed) {
                job.Scene.Dispose();
                job.Answer.TrySetCanceled();
                return job.Answer.Task;
            }
            job.SubmittedAt = Stopwatch.GetTimestamp();
            m_pending.Enqueue(job);
        }
        m_tables.GpuWork.Wake();
        return job.Answer.Task;
    }
    // Answers a job cancelled that was never taken, or was handed back, failing its relight batch, so the batch's commit
    // never copies rows a job of it did not write. Under the gate.
    private void Cancel(Job job) {
        if (job.Batch is { } batch) { batch.Failed = true; }
        job.Scene.Dispose();
        m_volume.Counters.Add(SdfMeshIrradiance.GpuCancelled, 1);
        job.Answer.TrySetCanceled();
    }

    // Takes a scene that exports the words the published scene holds (SdfMeshIrradianceScene.SameGpuContent) as the
    // published one, releasing the claim on the scene it replaces: a scene trimmed of retained geometry needs no upload,
    // and the untrimmed scene's geometry is freed.
    internal void Adopt(SdfMeshIrradianceScene scene) {
        lock (m_gate) { AdoptLocked(scene: scene); }
    }
    private void AdoptLocked(SdfMeshIrradianceScene scene) {
        if (m_stopping || m_disposed || ReferenceEquals(m_scene, scene) || !SdfMeshIrradianceScene.SameGpuContent(m_scene, scene)) { return; }
        m_scene!.Dispose();
        m_scene = scene.Acquire();
        m_sceneAdoptions++;
    }
    private long m_sceneAdoptions;
    // What every query waits on before it can run, for the camera sample's read-back (SdfMeshIrradiance.CameraSampleWait):
    // "scene-upload" while a scene uploads, "scene" before the first stands, or null once one does.
    internal string? SceneWait {
        get { lock (m_gate) { return (m_sceneUpload is not null) ? "scene-upload" : (m_scene is null) ? "scene" : null; } }
    }

    // A scene chunk is cut at SdfGpuWork.UploadChunkBytes before any take, the least any allowance is. Queries are taken
    // in the order they were submitted, several at a time across the service's slots (m_inFlight), but a scene upload
    // starts only when none is in flight and no query is taken while it runs, and an exclusive query runs alone. Every
    // job is cut to the live frame allowance, a held frame's too: the coordinator cuts queries before any take knows
    // whose frame it serves, and a structure build held to the same span keeps a held frame's submissions as short.
    /// <inheritdoc/>
    /// <remarks>While the volume owes the views light (<see cref="SdfMeshIrradiance.OwesViews"/>) every job it has serves
    /// that light.</remarks>
    public bool OwesAll => m_volume.OwesViews;
    public bool TryTake(SdfWorldTables tables, int uploadBytes, SdfGpuWorkAllowance allowance, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? job, out double estimatedSeconds) {
        job = null;
        estimatedSeconds = 0;
        lock (m_gate) {
            if (m_exclusive || !tables.BindsBackgroundWork) { return false; }
            if (ServeSceneUpload() || (m_sceneStore?.Carrying ?? false)) { return false; }
            if (TryTakeStructures(allowance: (allowance with { Seconds = Math.Min(val1: allowance.Seconds, val2: FrameAllowanceSeconds) }), job: out job, estimatedSeconds: out estimatedSeconds, tables: tables)) { return true; }
            if (!m_pending.TryPeek(out var pending)) { return false; }
            if (!((pending.Commits || pending.CameraSample) ? m_volume.KeepsGpuPublication(pending.Scene, pending.Geometry, pending.Window) : m_volume.KeepsGpuScene(pending.Scene)) ||
                (pending.CameraSample && (m_feedback is null || m_feedbackWords != pending.VolumeWords)) ||
                (pending.Commits && pending.Batch!.Failed)) {
                m_pending.Dequeue();
                m_waiting = null;
                m_feedbackRefusedAt = null;
                Cancel(job: pending);
                if (m_pending.Count > 0) { m_tables.GpuWork.Wake(); }
                return false;
            }
            if (!tables.TryProbeScene(pending.Scene, out var placements, out var meshes, out var version)) {
                m_feedbackRefusedAt = null;
                // The scene's meshes normally land within an upload or two. A head that waits past AdmissionWait is
                // cancelled like any obsolete job, so its coordinator and the camera sample behind it are never held without
                // bound; the coordinator lists again on its next grant and asks for the scene as it then stands.
                if (!ReferenceEquals(m_waiting, pending)) { m_waiting = pending; m_waitingSince = m_volume.Time.GetTimestamp(); }
                else if (m_volume.Time.GetElapsedTime(m_waitingSince) >= AdmissionWait) {
                    m_pending.Dequeue();
                    m_waiting = null;
                    m_feedbackRefusedAt = null;
                    // A scene that stayed unbound this long names a mesh the residency dropped, not one about to land:
                    // the volume then builds the scene its draws now name rather than waiting on this one's traces.
                    m_volume.NoteScenePlacement(scene: pending.Scene, placed: false);
                    Cancel(job: pending);
                    if (m_pending.Count > 0) { m_tables.GpuWork.Wake(); }
                }
                return false;
            }
            m_waiting = null;
            m_volume.NoteScenePlacement(scene: pending.Scene, placed: true);
            AdoptLocked(scene: pending.Scene);
            // A changed mesh roster need not move any of this scene's addresses.
            if (ReferenceEquals(m_scene, pending.Scene) && m_placementVersion != version && pending.Scene.MatchesGpuPlacements(placements, m_scenePlacements)) {
                m_placementVersion = version;
            }
            // A scene is replaced only with no query in flight, and none is taken while it uploads, so no slot
            // descriptor or region is changed while a query reads it.
            if (!ReferenceEquals(m_scene, pending.Scene) || m_placementVersion != version) {
                if (m_inFlight == 0) { BeginSceneUpload(scene: pending.Scene, placements: placements, version: version); }
                return false;
            }
            var exclusive = Exclusive(query: pending);
            if (exclusive && (m_inFlight != 0)) { return false; }
            // The coordinator cut the query to the whole frame allowance: while work is queued it waits for room, which
            // its price asks the service to hold.
            var price = m_rates[(int)KindOf(pending)].SecondsOf(units: UnitsOf(pending));

            if (!allowance.Admits(seconds: price)) {
                estimatedSeconds = price;
                return false;
            }
            pending.Meshes = meshes;
            if (pending.Lighting is not null && !pending.Selection && !pending.CameraSample && (m_feedbackWords != pending.VolumeWords)) {
                if (m_feedbackRefusedAt is { } refused && m_volume.Time.GetElapsedTime(refused) < SdfGpuWork.DeferralRetry) {
                    return false;
                }
                // A relayout gives the old table back before it makes the new one, so the two never stand at once: the old
                // retires once no job in flight records against it, and the new is made at the first take after the
                // retirement released it (m_feedbackRetiring).
                if (m_feedback is { } old) {
                    if (m_inFlight != 0) { return false; }
                    m_feedback = null;
                    m_feedbackWords = 0;
                    m_feedbackRetiring = true;
                    tables.RetireBackgroundResource(replaced: old, released: m_feedbackReleased ??= () => Volatile.Write(location: ref m_feedbackRetiring, value: false));
                    return false;
                }
                if (Volatile.Read(location: ref m_feedbackRetiring)) { return false; }
                // A slot of the service is the region's slot its job flushes through (Record), so a ring block is
                // rewritten only once the job that last staged through it completed. The region keeps no host copy: each
                // relight job hands it its own updates (Record).
                GpuRegion replacement;
                try {
                    replacement = tables.CreateProbeRegion("probe-feedback", pending.VolumeWords * 4, SdfGpuWork.SlotCount, hostCopyBytes: 0);
                } catch (GpuMemoryRefusedException) {
                    m_feedbackRefusedAt = m_volume.Time.GetTimestamp();
                    return false;
                }
                m_feedback = replacement;
                m_feedbackWords = pending.VolumeWords;
            }
            m_feedbackRefusedAt = null;
            m_pending.Dequeue();
            m_inFlight++;
            pending.Serial = Admit();
            pending.Top = TopFor(query: pending);
            pending.Exclusive = exclusive;
            m_exclusive = exclusive;
            job = pending;
            estimatedSeconds = m_rates[(int)KindOf(pending)].SecondsOf(units: UnitsOf(pending));
            return true;
        }
    }
    // Whether a query replaces, clears or fills state every query shares as it records (FitHitCache, the camera-sample cache's
    // fill, the feedback volume's size, the batch output's size), and so runs alone (m_exclusive). Under the gate, as
    // TryTake decides; Record makes the change on the same thread before any other take.
    private bool Exclusive(Job query) {
        if ((query.HitCapacity != 0) && (m_hitCacheOwed || m_hitCache.NeedsFit(HitCacheBytes(query.HitCapacity)))) { return true; }
        if (query.CameraSample) { return (m_cameraSampleCacheOwed || m_cameraSampleCache.NeedsFit(CameraSampleCacheBytes(query))); }
        if (query.Batch is { } batch) { return ((m_feedbackWords != query.VolumeWords) || m_batchOutput.NeedsFit(BatchOutputBytes(batch.Probes))); }
        return false;
    }
    private static ulong HitCacheBytes(uint capacity) => (ulong)capacity * SdfMeshProbeRelight.CacheWords * sizeof(uint);
    private static ulong CameraSampleCacheBytes(Job query) => (ulong)query.Origins.Length * (1 + SdfMeshProbeDepth.HitWords) * 4;
    private static ulong BatchOutputBytes(int probes) => (ulong)probes * SdfMeshProbeRelight.ResultWords * sizeof(uint);

    public void Record(object job, in SdfGpuWorkRecording recording) {
        if (job is StructureJob structures) { RecordStructures(job: structures, recording: in recording); return; }
        var query = (Job)job;
        var slot = m_slots[recording.Slot];
        var compute = query.Lighting is not null;
        var relight = compute && !query.Selection && !query.CameraSample;
        var kernel = KernelOf(query: query);
        var pipeline = m_tables.Pipeline(kernel);
        var layout = compute ? SdfWorldInterfaces.ProbeRelightLayout : SdfWorldInterfaces.ProbeTraceLayout;
        var set = compute ? slot.RelightSet : slot.Set;
        // A query TryTake gave a top level dispatches its kernel's ray-query variant over a set of the variant's interface,
        // holding every buffer the software set holds and the top level; the helper kernels around it keep the software
        // set, whose layout theirs is.
        var top = query.Top;
        var raysLayout = compute ? SdfWorldInterfaces.ProbeRelightRaysLayout : SdfWorldInterfaces.ProbeTraceRaysLayout;
        var raysSet = (top is null) ? 0 : (compute ? slot.RaysRelightSet : slot.RaysSet);
        var traced = (top is null) ? pipeline : m_tables.Pipeline(SdfKernelSet.RaysOf(kernel: kernel));
        var bytes = (ulong)query.Origins.Length * (ulong)(query.CameraSample ? SdfMeshProbeRelight.CameraSampleOutputWords : query.Selection ? 1 : relight ? SdfMeshProbeRelight.ResultWords : SdfMeshProbeDepth.HitWords) * 4;
        // A relight's slices write their partial sums behind the results and the fault word, where the readback never
        // reaches (sdf-probe-relight, sdf-probe-combine).
        var partialBytes = relight ? (ulong)query.Origins.Length * SdfMeshProbeRelight.Slices * SdfMeshProbeRelight.PartialWords * 4 : 0UL;
        // The slot's buffers are the service's, which reserves them: every one the job holds, its readback included, is
        // asked for here, then the client's own caches, before anything consumes state (the feedback region's copy), so a
        // job the device has no room for is handed back whole.
        var originTable = recording.HostBuffer(part: "origins", bytes: (ulong)query.Origins.Length * 16, usage: GpuBufferUsage.Storage, sizing: GpuBufferSizing.Exact);
        var hits = recording.DeviceBuffer(part: "hits", bytes: bytes + 4 + partialBytes, usage: GpuBufferUsage.Storage, sizing: GpuBufferSizing.Fit);
        var identities = recording.DeviceBuffer(part: "identities", bytes: 8, usage: GpuBufferUsage.Storage, sizing: GpuBufferSizing.Fit);
        var input = compute ? recording.HostBuffer(part: "input", bytes: (ulong)MemoryMarshal.AsBytes(query.Input.Span).Length, usage: GpuBufferUsage.Storage, sizing: GpuBufferSizing.Fit) : null;
        var lighting = compute ? recording.HostBuffer(part: "lighting", bytes: (ulong)MemoryMarshal.AsBytes(query.Lighting.AsSpan()).Length, usage: GpuBufferUsage.Storage, sizing: GpuBufferSizing.Fit) : null;
        var work = compute ? recording.DeviceBuffer(part: "work", bytes: (ulong)(query.Origins.Length + 3) * 4, usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect, sizing: GpuBufferSizing.Fit) : null;
        recording.ReserveReadback(bytes: bytes + 4);
        var command = recording.CommandBuffer;
        if (query.CameraSample) {
            m_cameraSampleCacheOwed |= m_cameraSampleCache.Fit(CameraSampleCacheBytes(query));
            // A new cache holds nothing until a camera-sample job that fills it completes (Complete), so until then every job
            // fills it: one handed back unsubmitted leaves the fill owed.
            if (m_cameraSampleCacheOwed) {
                var origins = query.Origins.Span;
                for (var i = 0; i < origins.Length; i++) { origins[i].W = 1; }
                query.FillsCameraSampleCache = true;
            }
        }
        if (query.HitCapacity != 0) { FitHitCache(query, query.HitCapacity, command); }
        if (query.Batch is { } batch) { m_batchOutput.Fit(BatchOutputBytes(batch.Probes)); }
        originTable.Write<Vector4>(query.Origins.Span);
        // The job stages its updates of the feedback volume through its own slot, whose ring block no submission in flight
        // reads once the service hands the slot to this job. Every relight job hands the region its own updates, none
        // included, so words an earlier job was owed and never staged are dropped rather than read from data its owner may
        // since have reused: a job whose recording failed is abandoned and cancelled, which makes the volume send a full
        // snapshot with its next batch (SdfMeshIrradiance.RelightGpu).
        if (relight) {
            var updates = query.Updates;

            m_feedback!.Shadow(source: new FeedbackUpdates(updates), shadowBytes: 0, exact: true);
            for (var update = 0; update < updates.Count; update++) {
                m_feedback.OweSourced(offset: updates.Words.Span[update] * 4L, length: updates.Length * 4L);
            }
            recording.CopyRegion(m_feedback, recording.Slot, GpuStage.ComputeShader);
        }
        Bind(SdfKernelInterfaces.ProbeScene, m_sceneStore!.Buffer, 4);
        Bind(SdfWorldPackage.MeshRegion, query.Meshes!, 4);
        Bind(SdfKernelInterfaces.ProbeOrigins, originTable, 16);
        Bind(SdfKernelInterfaces.ProbeDirections, m_directions, 16);
        Bind(SdfKernelInterfaces.ProbeHitsWritten, hits, 4, written: true);
        if (compute) {
            input!.Write<byte>(MemoryMarshal.AsBytes(query.Input.Span));
            lighting!.Write<byte>(MemoryMarshal.AsBytes(query.Lighting.AsSpan()));
            Bind(SdfKernelInterfaces.ProbeInput, input, 4);
            Bind(SdfKernelInterfaces.ProbeLighting, lighting, 16);
            Bind(SdfWorldPackage.MeshIrradiance, m_feedback?.Buffer(0) ?? m_tables.DummyBuffer, 4);
            Bind(SdfKernelInterfaces.ProbeWorkWritten, work!, 4, written: true);
            Bind(SdfKernelInterfaces.ProbeWork, work!, 4);
            Bind(SdfKernelInterfaces.ProbeCacheWritten, query.CameraSample ? m_cameraSampleCache.Buffer! : m_hitCache.Buffer!, 4, written: true);
            Bind(SdfKernelInterfaces.ProbeCache, query.CameraSample ? m_cameraSampleCache.Buffer! : m_hitCache.Buffer!, 4);
        } else { Bind(SdfKernelInterfaces.ProbeIdentitiesWritten, identities, 8, written: true); }
        if (top is not null) {
            m_structures!.WriteDescriptor(binding: SdfKernelInterfaces.ResourceOf(raysLayout, SdfKernelInterfaces.ProbeStructure).Binding, descriptorSetHandle: raysSet, structure: top.Structure);
        }
        var recorder = recording.Recorder;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: hits.BufferHandle,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
        recorder.CopyBuffer(commandBufferHandle: command, sourceBufferHandle: m_zero.BufferHandle,
            destinationBufferHandle: hits.BufferHandle, sizeBytes: 4, destinationOffsetBytes: bytes);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: hits.BufferHandle,
            sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: identities.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        if (query.CameraSample) {
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_cameraSampleCache.Buffer!.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        }
        if (relight) {
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.Buffer!.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            var cache = m_tables.Pipeline(SdfKernel.ProbeCache);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: cache.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: cache.LayoutHandle);
            recorder.Dispatch(command, (uint)query.Origins.Length, 1, 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: hits.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.Buffer!.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: work!.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.IndirectCommandRead, sourceStageMask: GpuStage.ComputeShader | GpuStage.DrawIndirect,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            var compact = m_tables.Pipeline(SdfKernel.ProbeCompact);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: compact.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: compact.LayoutHandle);
            recorder.Dispatch(command, 1, 1, 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: work.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.IndirectCommandRead, destinationStageMask: GpuStage.ComputeShader | GpuStage.DrawIndirect);
        }
        recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: traced.Handle);
        recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: ((top is null) ? set : raysSet),
            group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: traced.LayoutHandle);
        // The traced kernel reads the published scene's root words at the base it is pushed: every residency's scene
        // shares the probe scenes' buffer (sdfProbeRootWord).
        Span<byte> rootWord = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: rootWord, value: (uint)m_sceneExport!.Prefix);
        recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, data: rootWord, offset: 0,
            pipelineLayoutHandle: traced.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        if (relight) {
            recorder.DispatchIndirect(command, work!.BufferHandle, 0);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: hits.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            var combine = m_tables.Pipeline(SdfKernel.ProbeCombine);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: combine.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: combine.LayoutHandle);
            recorder.Dispatch(command, (uint)query.Origins.Length, 1, 1);
        }
        else { recorder.Dispatch(commandBufferHandle: command,
            groupCountX: (uint)(query.CameraSample ? query.Origins.Length : query.Selection ? query.Origins.Length.CeilingDivide(divisor: SdfMeshProbeRelight.SelectGroupSize) : (query.Origins.Length * SdfMeshProbeDepth.HitCount).CeilingDivide(divisor: SdfMeshProbeTrace.GroupSize)), groupCountY: 1, groupCountZ: 1); }
        recording.CopyToReadback(hits, bytes + 4);
        if (!query.CacheSlots.IsEmpty) {
            // The readback left the hits a transfer source; each origin's hits land behind its cache slot's two token
            // words, which the adopting relight's cache pass stamps.
            var cache = m_hitCache.Buffer!;
            var cacheSlots = query.CacheSlots.Span;
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: cache.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
            const ulong HitBytes = (ulong)SdfMeshProbeDepth.HitWords * sizeof(uint);
            for (var i = 0; i < cacheSlots.Length; i++) {
                recorder.CopyBuffer(command, hits.BufferHandle, cache.BufferHandle, HitBytes, (ulong)i * HitBytes,
                    ((ulong)cacheSlots[i] * SdfMeshProbeRelight.CacheWords + 2) * sizeof(uint));
            }
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: cache.BufferHandle,
                sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
        }
        if (query.Batch is not null) {
            // The readback left the results a transfer source too. The batch output rests as a transfer source between
            // jobs, so the batch's last job reads every earlier job's rows after the barrier that ended each copy.
            var output = m_batchOutput.Buffer!;
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: output.BufferHandle,
                sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(command, hits.BufferHandle, output.BufferHandle, bytes, 0UL, (ulong)query.First * SdfMeshProbeRelight.ResultWords * sizeof(uint));
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: output.BufferHandle,
                sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            if (query.Commits) { RecordCommit(query: query, recorder: recorder, command: command); }
        }

        void Bind(string name, IGpuBuffer buffer, uint stride, bool written = false) {
            var resource = SdfKernelInterfaces.ResourceOf(layout, name);
            m_gpu.Bindings.WriteBuffer(binding: resource.Binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                descriptorSetHandle: set, elementStride: stride, kind: written ? GpuBindingKind.ReadWriteBuffer : GpuBindingKind.ReadOnlyBuffer);
            if (top is not null) {
                m_gpu.Bindings.WriteBuffer(binding: SdfKernelInterfaces.ResourceOf(raysLayout, name).Binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                    descriptorSetHandle: raysSet, elementStride: stride, kind: written ? GpuBindingKind.ReadWriteBuffer : GpuBindingKind.ReadOnlyBuffer);
            }
        }
    }

    // Sizes the hit cache to the volume's tier (SdfMeshIrradiance.GpuHitCapacity). A job that replaces or clears it is
    // exclusive (TryTake), so every earlier job's fence has signaled and no recorded work reads the cache it replaces.
    // A new cache is cleared, so its
    // token words match no allocation token, and left readable by compute. The clear stays owed until a job that
    // recorded it completes (Complete): a job handed back unsubmitted discarded it, and the next job clears again.
    private void FitHitCache(Job query, uint capacity, nint command) {
        var bytes = HitCacheBytes(capacity: capacity);
        // The old cache goes before the new is made, so a relayout never holds both at once.
        m_hitCacheOwed |= m_hitCache.Refit(bytes);
        if (!m_hitCacheOwed) { return; }
        query.ClearsHitCache = true;
        var recorder = m_gpu.Recorder;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.Buffer!.BufferHandle,
            sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
            destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
        recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.Buffer!.BufferHandle, sizeBytes: bytes);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.Buffer!.BufferHandle,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
    }


    // Copies every probe's published record of the batch from the batch output into the feedback volume at its
    // destination slot: one copy a probe, after every relight of the batch, which wrote its rows in this or an earlier
    // submission.
    private void RecordCommit(Job query, IGpuRecorder recorder, nint command) {
        var source = m_batchOutput.Buffer!;
        var destination = FeedbackBuffer;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.TransferRead, sourceStageMask: GpuStage.ComputeShader | GpuStage.Transfer,
            destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
        var destinations = query.Destinations.Span;
        for (var i = 0; i < destinations.Length; i++) {
            var slot = destinations[i];
            if (slot < 0) { continue; }
            recorder.CopyBuffer(command, source.BufferHandle, destination.BufferHandle,
                (ulong)SdfMeshIrradianceProbe.ProbeWords * 4,
                (ulong)(i * SdfMeshProbeRelight.ResultWords + SdfMeshProbeRelight.OutputWords) * 4,
                (ulong)slot * SdfMeshIrradianceProbe.ProbeWords * 4);
        }
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
            sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.TransferRead, destinationStageMask: GpuStage.ComputeShader | GpuStage.Transfer);
    }

    public void Complete(object job, SdfGpuWorkReadback readback) {
        if (job is StructureJob structures) { CompleteStructures(job: structures, readback: readback); return; }
        var query = (Job)job;
        // The job ran: what it initialized stands.
        if (query.ClearsHitCache) { m_hitCacheOwed = false; }
        if (query.FillsCameraSampleCache) { m_cameraSampleCacheOwed = false; }
        var words = readback.Length / sizeof(uint);
        var result = query.Result!;
        try {
            readback.Read(MemoryMarshal.AsBytes(result.AsSpan(0, words)));
        } finally {
            readback.Release();
        }
        var faults = result[words - 1];

        m_volume.Counters.Add(SdfMeshIrradiance.TraversalFaults, faults);
        // A walk stopped at its step or stack bound is a scene the hierarchy cannot answer; each costs a ray its whole
        // bound, which is what makes one probe's thread group long enough to hang the device.
        if (faults != 0) {
            Console.Error.WriteLine(FormattableString.Invariant($"[sdf.mesh-gi] traversal faults={faults} {Describe(job: query)} gpu={((readback.GpuSeconds ?? 0.0) * 1000.0):0.0}ms"));
        }
        if (query.Lighting is null) {
            m_volume.Counters.Add(SdfMeshIrradiance.GpuRays, query.Origins.Length * SdfMeshProbeDepth.HitCount);
            if (query.Top is not null) { m_volume.Counters.Add(SdfMeshIrradiance.GpuQueryRays, query.Origins.Length * SdfMeshProbeDepth.HitCount); }
        }
        if (readback.GpuSeconds is { } seconds) {
            var kind = KindOf(query);
            m_rates[(int)kind].Observe(seconds: seconds, units: UnitsOf(query));
            m_volume.Counters.Add(kind switch {
                JobKind.CameraSample => SdfMeshIrradiance.GpuCameraSampleNanoseconds,
                JobKind.Select => SdfMeshIrradiance.GpuSelectNanoseconds,
                JobKind.Trace => SdfMeshIrradiance.GpuTraceNanoseconds,
                _ => SdfMeshIrradiance.GpuRelightNanoseconds,
            }, (long)(seconds * 1e9));
            // The share the ray hardware traced, against which the totals above read the software walk's.
            if ((query.Top is not null) && (kind is JobKind.Trace or JobKind.Relight)) {
                m_volume.Counters.Add(((kind == JobKind.Trace) ? SdfMeshIrradiance.GpuQueryTraceNanoseconds : SdfMeshIrradiance.GpuQueryRelightNanoseconds), (long)(seconds * 1e9));
            }
        }
        CountAnswered(query: query);
        if (query.CameraSample) {
            for (var i = SdfMeshProbeRelight.CameraSampleOutputWords - 1; i < words - 1; i += SdfMeshProbeRelight.CameraSampleOutputWords) {
                if ((result[i] & 4) != 0) { m_volume.Counters.Add(SdfMeshIrradiance.CameraTraces, 1); }
                if ((result[i] & 2) != 0) { m_volume.Counters.Add(SdfMeshIrradiance.CameraRelights, 1); }
            }
        }
        lock (m_gate) { Land(query: query); }
        query.Scene.Dispose();
        query.Answer.SetResult(result);
    }
    public void Abandon(object job) {
        if (job is StructureJob structures) { lock (m_gate) { AbandonStructures(job: structures); } return; }
        var query = (Job)job;
        // An abandoned relight may have handed the feedback region its updates; their owner reuses or returns that
        // data once the job is cancelled, so the region lets it go now (the volume resends a full snapshot).
        if (query.Updates.Count != 0) { m_feedback?.Shadow(source: FeedbackUpdates.None, shadowBytes: 0, exact: true); }
        lock (m_gate) {
            Land(query: query);
            if (query.Batch is { } batch) { batch.Failed = true; }
        }
        query.Scene.Dispose();
        query.Answer.TrySetCanceled();
    }
    // Ends a taken query's flight, and the exclusivity it held. Under the gate.
    private void Land(Job query) {
        m_inFlight--;
        if (query.Exclusive) { m_exclusive = false; }
        Land(serial: query.Serial);
    }
    public GpuQueueCategory Category => GpuQueueCategory.Probe;
    // Counts an answered job in its volume's counters (SdfMeshIrradiance.GpuJobs, GpuJobUnits, GpuJobWaitNanoseconds), so
    // a reader sees whether the volume's work is bound by its jobs' count and round trips or by their GPU time.
    private void CountAnswered(Job query) {
        var kind = (int)KindOf(query);

        m_volume.Counters.Add(SdfMeshIrradiance.GpuJobs[kind], 1);
        m_volume.Counters.Add(SdfMeshIrradiance.GpuJobUnits[kind], UnitsOf(query));
        if (query.SubmittedAt != 0L) { m_volume.Counters.Add(SdfMeshIrradiance.GpuJobWaitNanoseconds[kind], (long)Stopwatch.GetElapsedTime(startingTimestamp: query.SubmittedAt).TotalNanoseconds); }
    }
    // Each kind's answered jobs, units and mean wait from submission to answer, from the volume's counters.
    private string DescribeJobs() {
        var text = new System.Text.StringBuilder(capacity: 160);

        _ = text.Append(value: "trace-jobs=[");
        for (var kind = 0; (kind < SdfMeshIrradiance.GpuJobs.Length); kind++) {
            var jobs = m_volume.Counters.Read(kind: SdfMeshIrradiance.GpuJobs[kind]);
            var milliseconds = ((jobs == 0L) ? 0.0 : (m_volume.Counters.Read(kind: SdfMeshIrradiance.GpuJobWaitNanoseconds[kind]) / (1e6 * jobs)));

            _ = text.Append(provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"{((kind == 0) ? string.Empty : ",")}{((JobKind)kind).ToString().ToLowerInvariant()}:{jobs}/{m_volume.Counters.Read(kind: SdfMeshIrradiance.GpuJobUnits[kind])}u/{milliseconds:0.0}ms");
        }
        return text.Append(value: ']').ToString();
    }
    // The kinds of probe job, each with its own measured rate (m_rates, in this order).
    private enum JobKind { Trace, Relight, Select, CameraSample }
    private static JobKind KindOf(Job query) => query.CameraSample ? JobKind.CameraSample :
        query.Selection ? JobKind.Select : (query.Lighting is not null) ? JobKind.Relight : JobKind.Trace;
    // The units a job's rate is measured in: a relight's predicted rays (none for a job of invalid probes alone, which
    // measures nothing), a selection's tests (SelectTestsOf), and every other kind's origins.
    private static long UnitsOf(Job query) => KindOf(query) switch {
        JobKind.Relight => query.Rays,
        JobKind.Select => query.Tests,
        _ => query.Origins.Length,
    };
    public string Describe(object job) {
        if (job is StructureJob structures) { return FormattableString.Invariant($"structures kind={structures.Kind.ToString().ToLowerInvariant()} units={structures.Units}"); }
        var query = (Job)job;
        var kind = KindOf(query).ToString().ToLowerInvariant() + ((query.Top is null) ? string.Empty : "-rays");
        var lights = (query.Lighting is not { Length: > 0 } rows) ? string.Empty :
            query.Selection ? FormattableString.Invariant($" changes={rows[1].X} change-nodes={rows[1].Y} tests={query.Tests}") : FormattableString.Invariant($" lights={rows[0].Y} points={rows[0].Z}");
        return FormattableString.Invariant($"{kind} origins={query.Origins.Length} destinations={query.Destinations.Length} hit-capacity={query.HitCapacity}{lights}{((kind == "relight") ? DescribeRelight(query) : string.Empty)}");
    }
    // What decides a relight's cost: the probes it shades and, for each, the shadowing point lights in range of its
    // origin, each of which a hit texel traces a ray toward (sdfProbeShade).
    private static string DescribeRelight(Job query) {
        var rows = query.Lighting!;
        var input = query.Input.Span;
        var origins = query.Origins.Span;
        var lightBase = SdfMeshProbeRelight.HeaderRows + ((int)rows[0].X * SdfMeshProbeRelight.MaterialRows);
        var lightCount = (int)(rows[0].Y + rows[0].Z);
        var valid = 0;
        var reused = 0;
        var most = 0;
        var total = 0L;
        var slots = 0u;

        for (var probe = 0; (probe < origins.Length) && (((probe + 1) * SdfMeshProbeRelight.InputWords) <= input.Length); probe++) {
            var flags = input[probe * SdfMeshProbeRelight.InputWords];

            if ((flags & 1u) == 0u) { continue; }
            valid++;
            if ((flags & 2u) != 0u) { reused++; continue; }
            slots = Math.Max(slots, input[(probe * SdfMeshProbeRelight.InputWords) + 3]);

            var near = 0;

            for (var light = 0; (light < lightCount) && ((lightBase + (light * SdfMeshProbeRelight.LightRows) + 3) < rows.Length); light++) {
                var position = rows[lightBase + (light * SdfMeshProbeRelight.LightRows)];
                var mode = rows[lightBase + (light * SdfMeshProbeRelight.LightRows) + 3];

                if ((mode.W != 0f) && (mode.Z != 0f) && (Vector3.DistanceSquared(new Vector3(position.X, position.Y, position.Z), new Vector3(origins[probe].X, origins[probe].Y, origins[probe].Z)) < (position.W * position.W))) { near++; }
            }
            most = Math.Max(most, near);
            total += near;
        }
        return FormattableString.Invariant($" rays={query.Rays} valid={valid} reused={reused} shadowing-points-in-range-max={most} mean={((valid > reused) ? (total / (double)(valid - reused)) : 0.0):0.0} max-slot={slots}");
    }
    private static GpuObjectName Name(string part, int index = GpuObjectName.NoIndex) =>
        new(index: index, owner: SdfWorldTables.ObjectOwner, part: $"probe-trace {part}");
    public GpuMemoryBytes OwnedBytes {
        get {
            // The scene's words lie in the probe scenes every residency of the mesh region shares, which parking one
            // residency does not give back.
            var device = 0UL;
            var host = m_directions.SizeBytes + m_zero.SizeBytes;
            device += m_feedback?.OwnedBytes.DeviceLocal ?? 0;
            device += (m_hitCache.Buffer?.SizeBytes ?? 0) + (m_cameraSampleCache.Buffer?.SizeBytes ?? 0) + (m_batchOutput.Buffer?.SizeBytes ?? 0) + (ulong)Math.Max(val1: 0L, val2: Volatile.Read(location: ref m_structureBytes));
            host += m_feedback?.OwnedBytes.HostVisible ?? 0;
            return new GpuMemoryBytes(DeviceLocal: device, HostVisible: host);
        }
    }
    internal string DescribeMemory() {
        var feedback = m_feedback?.OwnedBytes ?? default;
        return FormattableString.Invariant($"trace-scene-root={m_sceneExport?.Prefix ?? -1} trace-scene-prefix-words={((m_sceneExport is { } export) ? (export.Changed[^1].Count + SdfMeshProbeTrace.RootWords) : 0)} trace-scene-changes={m_sceneUploads} trace-scene-adoptions={m_sceneAdoptions} trace-scene-written-bytes={m_sceneWrittenBytes} trace-scene-last-change-bytes={m_sceneLastBytes} trace-hits-device-bytes={m_hitCache.Buffer?.SizeBytes ?? 0} trace-batch-output-device-bytes={m_batchOutput.Buffer?.SizeBytes ?? 0} trace-feedback-device-bytes={feedback.DeviceLocal} trace-feedback-upload-bytes={feedback.HostVisible} trace-feedback-shadow-bytes={m_feedback?.ShadowBytes ?? 0} {DescribeJobs()} {DescribeStructures()}");
    }
    public void Stop() {
        lock (m_gate) {
            m_stopping = true;
            m_sceneUpload?.Cancel();
            while (m_pending.TryDequeue(out var pending)) { Cancel(job: pending); }
        }
    }
    public void Dispose() {
        if (m_disposed) { return; }
        Stop();
        m_disposed = true;
        ReleaseScene();
        m_feedback?.Dispose();
        m_hitCache.Dispose();
        m_cameraSampleCache.Dispose();
        m_batchOutput.Dispose();
        m_scene?.Dispose();
        m_directions.Dispose();
        m_zero.Dispose();
        DisposeStructures();
        m_gpu.Bindings.DestroyPool(m_pool);
    }
    /// <summary>The slot-sized updates a relight job applies to the feedback volume before it runs: the first word each
    /// update writes, then every update's words back to back, each <see cref="Length"/> words long.</summary>
    internal readonly record struct Deltas(ReadOnlyMemory<int> Words, ReadOnlyMemory<uint> Data, int Count, int Length);

    // A relight job's updates as the feedback region's exact source: it answers the words the job owed and nothing else,
    // since the region joins owed runs only where updates touch. The updates may come in any order; a flush asks for a
    // run that can span several adjacent ones.
    private sealed class FeedbackUpdates : IGpuRegionSource {
        // No updates: what the region holds between a cancelled job and the next relight.
        public static FeedbackUpdates None { get; } = new(updates: default);

        private readonly Deltas m_updates;
        // The updates' first words in increasing order, and each one's update.
        private readonly int[] m_starts;
        private readonly int[] m_order;

        public FeedbackUpdates(Deltas updates) {
            m_updates = updates;
            m_starts = updates.Words.Span[..updates.Count].ToArray();
            m_order = new int[updates.Count];
            for (var index = 0; index < m_order.Length; index++) { m_order[index] = index; }
            Array.Sort(keys: m_starts, items: m_order);
        }

        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) {
            var data = m_updates.Data.Span;
            var length = m_updates.Length;
            var at = PartitionPoint.Of(span: (ReadOnlySpan<int>)m_starts, state: word, isBefore: static (word, start) => (start <= word)) - 1;

            while (count > 0) {
                if ((at < 0) || (at >= m_order.Length)) { throw new InvalidOperationException(message: $"The feedback region asked for word {word}, which no update of the job holds."); }
                var update = m_order[at];
                var skip = word - m_starts[at];
                if ((skip < 0) || (skip >= length)) { throw new InvalidOperationException(message: $"The feedback region asked for word {word}, which no update of the job holds."); }
                var take = Math.Min(val1: count, val2: length - skip);

                buffer.Write<uint>(data.Slice(start: (update * length) + skip, length: take), destinationOffsetBytes);
                destinationOffsetBytes += (ulong)take * sizeof(uint);
                word += take;
                count -= take;
                at++;
            }
        }
    }

    private sealed class Job(SdfMeshIrradianceScene scene, Memory<Vector4> origins) {
        public readonly SdfMeshIrradianceScene Scene = scene;
        public readonly Memory<Vector4> Origins = origins;
        public readonly TaskCompletionSource<uint[]> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IGpuBuffer? Meshes;
        public ReadOnlyMemory<uint> Input;
        public Vector4[]? Lighting;
        public int VolumeWords;
        public Deltas Updates;
        public bool Selection;
        public bool CameraSample;
        // A relight's batch, the batch's probe its first origin is, and whether it is the batch's last job, which commits
        // the batch's Destinations. A trace's or selection's First is its call's origin its first origin is.
        public RelightBatch? Batch;
        public int First;
        public bool Commits;
        public ReadOnlyMemory<int> Destinations;
        public ReadOnlyMemory<uint> CacheSlots;
        // The requester reserves the answer, including the fault word, before the job takes a device slot.
        public uint[]? Result;
        public long Rays;
        public long Tests;
        public uint HitCapacity;
        // Whether the job recorded the camera-sample cache's fill or the hit cache's clear, which its completion discharges.
        public bool FillsCameraSampleCache;
        public bool ClearsHitCache;
        // Whether TryTake admitted the job alone (Exclusive).
        public bool Exclusive;
        // When a worker submitted the job (SubmitAsync), in Stopwatch ticks; zero for one never submitted.
        public long SubmittedAt;
        public int Geometry;
        public int Window;
        // The serial TryTake admitted the job under (zero before), and the top level its kernel's ray-query variant traces,
        // or null for the software walk.
        public long Serial;
        public Top? Top;
    }
    // One relight batch's jobs share it: its probes, and whether a job of it was cancelled or handed back, which cancels
    // the batch's commit (TryTake), so the commit never copies a row no job of the batch wrote.
    private sealed record RelightBatch(int Probes) {
        public volatile bool Failed;
    }
    // A slot's two pass sets; the buffers a job binds in them are the service's (SdfGpuWorkRecording.HostBuffer).
    private sealed class Slot(nint set, nint relightSet) {
        public readonly nint Set = set;
        public readonly nint RelightSet = relightSet;
        // The ray-query interfaces' sets, zero on a device that runs no variant.
        public nint RaysSet;
        public nint RaysRelightSet;
    }
}
