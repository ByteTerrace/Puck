using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>
/// What a mesh bake says about itself: every <c>[sdf.mesh-bake]</c> line's text and its write to standard error, and the
/// counters those lines report. The bake hands this type values (counts, names, the groups' outcomes) and never builds,
/// formats or writes text itself, because the bake's code is the code half of its stored meshes' identity
/// (<see cref="SdfMeshBakeClosure"/>): a word changed in a log line would move that identity and bake the world again
/// from empty. The closure names this type as its boundary (<see cref="SdfMeshBakeClosure"/>'s stop types), so nothing
/// in it, its nested types included, is hashed, and an edit here, or a new counter, leaves every stored mesh as it was.
/// Nothing here may change what a bake produces: a value this reads or counts is never read back to decide a group's
/// geometry, its order or its mesh.
/// </summary>
internal static class SdfMeshBakeReport {
    /// <summary>The first groups a start line names.</summary>
    public const int NamedMost = 8;

    /// <summary>The thread seconds past which one group's stage is reported as slow.</summary>
    private const double SlowStageSeconds = 2.0;

    /// <summary>The bake's CPU stages, whose summed thread time the done line reports.</summary>
    public enum Stage {
        Answer,
        Prepare,
        Crossings,
        Contour,
        Split,
        Cut,
        Land,
        Store,
    }
    /// <summary>Where the bake's gate is taken: the frame thread's entries (its frame preparation and takes, its
    /// residency observation, the readers), and each worker stage that publishes its group's next step. The done line
    /// reports each site's entries, its waits for the gate and how long it held it, so whether the workers wait on the
    /// gate because a section holds it long, or because many sections take it, reads per site. A section the frame
    /// thread runs inside another's hold (<see cref="Gate.Begin"/>) is counted under its own site and out of the
    /// enclosing one.</summary>
    public enum Gate {
        Frame,
        Prioritize,
        Retier,
        Judge,
        Dress,
        Take,
        Observe,
        Evict,
        Begin,
        Read,
        Ticket,
        Slice,
        Refine,
        Slab,
        Prepare,
        Final,
        Crossings,
        Contour,
        Creases,
        Split,
        Points,
        Advance,
        Land,
        Load,
        Taken,
        Fail,
        Checkpoint,
    }
    /// <summary>One group a restart, resample or promotion began, as the start line names it.</summary>
    /// <param name="Selector">The group's selector.</param>
    /// <param name="Dynamic">Whether the group is a dynamic one.</param>
    /// <param name="Place">The dynamic group's slot, or a static group's instance count.</param>
    /// <param name="Loading">Whether the group began as a load from the store rather than a bake.</param>
    public readonly record struct Begun(int Selector, bool Dynamic, int Place, bool Loading);
    /// <summary>The numbers a slow stage reports beside its group; each stage reads the ones it measured.</summary>
    /// <param name="Dims">The final lattice's samples along each axis.</param>
    /// <param name="Factor">The lattice's refinement.</param>
    /// <param name="Measured">The cells its refinement measured.</param>
    /// <param name="Count">The points, quads or triangles the stage handled.</param>
    /// <param name="Kept">The triangles a landing kept.</param>
    public readonly record struct Detail((int X, int Y, int Z)? Dims = null, int? Factor = null, int? Measured = null, int? Count = null, int? Kept = null);
    /// <summary>One group of a finished bake: what the done line counts and names of it.</summary>
    /// <param name="Selector">The group's selector.</param>
    /// <param name="Mesh">The mesh it drew, or <see langword="null"/>.</param>
    /// <param name="Outcome">Why it holds no mesh, when it does not.</param>
    public readonly record struct Finished(int Selector, SdfMesh? Mesh, string? Outcome);
    /// <summary>The counters the done line reports. A bake clears them as a new bake starts; the threads that count
    /// into them each own a field, or add through <see cref="Interlocked"/> (the stage ticks).</summary>
    public sealed class Accounts {
        /// <summary>The samples answered by the lattices of each kind: coarse and refining grids, final slabs, the final
        /// lattice's fine samples, crossing rounds and gradients, the field check's vertices, material points and skin
        /// points.</summary>
        public readonly long[] KindSamples = new long[8];
        /// <summary>Stopwatch ticks each stage spent, summed over the threads that ran it.</summary>
        public readonly long[] StageTicks = new long[Enum.GetValues<Stage>().Length];
        /// <summary>Each gate site's entries, the Stopwatch ticks it waited for the gate and held it, and its longest
        /// hold; written under the gate.</summary>
        public readonly long[] GateEntries = new long[GateSites];
        public readonly long[] GateWaitTicks = new long[GateSites];
        public readonly long[] GateHoldTicks = new long[GateSites];
        public readonly long[] GateHoldMax = new long[GateSites];
        /// <summary>The Stopwatch ticks of sections counted under a site of their own inside another entry's hold
        /// (<see cref="GateNested"/>), which that entry leaves out of its own; written under the gate.</summary>
        public long GateNestedTicks;

        private static readonly int GateSites = Enum.GetValues<Gate>().Length;

        // The process's managed allocation, collector pause and collections when the bake started, of which the done
        // line reports the bake's window: every thread's, the bake's workers dominating it while they run.
        private long m_allocatedAtStart;
        private long m_freshLargeAtStart;
        private long m_reusedLargeAtStart;
        private TimeSpan m_pausedAtStart;
        private int m_gen0AtStart;
        private int m_gen1AtStart;
        private int m_gen2AtStart;

        public long Batches;
        // The final lattices bounded before sampling (SdfMeshPathBake.Bounds.cs), those whose CPU field could not bound
        // them, the interval walks the bounds took and the samples they skipped.
        public long BoundGroups;
        public long BoundRefused;
        public long BoundWalks;
        public long BoundSkipped;
        // The samples of lattices handed back after the GPU sampled them (a group retired while its lattice flew, a
        // failed decode or readback), and of those handed back before it did (a deferred or abandoned job).
        public long Discarded;
        public long Returned;
        public long Dispatches;
        public long FlyingGroups;
        public long FrameGateWaitMax;
        public long FrameGateWaitsSlow;
        public long FrameGateWaitTotal;
        public long IdleTakes;
        public long Loaded;
        public long Merged;
        public long RefinedCells;
        public long Samples;
        public long SampleHits;
        public long SampleMisses;
        public long ContourHits;
        public long ContourMisses;
        public long SimplifyHits;
        public long SimplifyMisses;
        public long Softened;
        public long StopBatch;
        public long StopDrained;
        public long StopSamples;
        public long StopTaps;
        // The takes that stopped at a lattice the readback reservation had no room left for, the lattices the account had no
        // room to promise readback for, the refinements that waited at the head of the refinement queue for room for their
        // peak (SdfMeshPathBake.Refinements, written with Interlocked by whichever thread ran the queue's door), the copies
        // into the library the host memory account deferred, and the groups whose refused stage started their final lattice
        // again (SdfMeshPathBake.Refusals).
        public long StopReadback;
        public long ReadbackWaits;
        public long RefinementWaits;
        public long CopyWaits;
        public long Readmissions;
        public long Takes;
        public long Taps;
        public long WaitingGroups;
        public long WorkingGroups;
        public long CutEdges;
        /// <summary>The groups that waited through the library on another group's bake of their geometry
        /// (<see cref="SdfMeshBakeShare"/>); of them, those that took its mesh or empty result, those whose leader left
        /// nothing to take and started again, and those that deserted a leader no frame drives; and the samples the
        /// leaders of those that took a mesh dispatched for it.</summary>
        public long Joined;
        public long JoinedLanded;
        public long JoinedAbandoned;
        public long JoinedStalled;
        public long SamplesSaved;

        /// <summary>Clears every counter for a new bake.</summary>
        public void Clear() {
            Array.Clear(array: StageTicks);
            Array.Clear(array: KindSamples);
            Takes = IdleTakes = Batches = StopTaps = StopSamples = StopBatch = StopDrained = StopReadback = 0;
            ReadbackWaits = RefinementWaits = CopyWaits = Readmissions = 0;
            WaitingGroups = FlyingGroups = WorkingGroups = 0;
            FrameGateWaitMax = FrameGateWaitTotal = FrameGateWaitsSlow = 0;
            Dispatches = Samples = Taps = Discarded = Returned = RefinedCells = CutEdges = Softened = Merged = Loaded = 0;
            Joined = JoinedLanded = JoinedAbandoned = JoinedStalled = SamplesSaved = 0;
            BoundGroups = BoundRefused = BoundWalks = BoundSkipped = 0;
            Array.Clear(array: GateEntries);
            Array.Clear(array: GateWaitTicks);
            Array.Clear(array: GateHoldTicks);
            Array.Clear(array: GateHoldMax);
            GateNestedTicks = 0L;
            m_allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            m_freshLargeAtStart = RentedArrays.LargeFreshBytes;
            m_reusedLargeAtStart = RentedArrays.LargeReusedBytes;
            m_pausedAtStart = GC.GetTotalPauseDuration();
            m_gen0AtStart = GC.CollectionCount(generation: 0);
            m_gen1AtStart = GC.CollectionCount(generation: 1);
            m_gen2AtStart = GC.CollectionCount(generation: 2);
            SampleHits = SampleMisses = ContourHits = ContourMisses = SimplifyHits = SimplifyMisses = 0;
        }
        /// <summary>Counts one entry of the bake's gate, under the gate as the entry leaves it.</summary>
        /// <param name="site">Where it was taken.</param>
        /// <param name="waited">The Stopwatch ticks the entry waited for the gate.</param>
        /// <param name="held">The Stopwatch ticks it held the gate.</param>
        public void GateHeld(Gate site, long waited, long held) {
            var index = ((int)site);

            GateEntries[index]++;
            GateWaitTicks[index] += waited;
            GateHoldTicks[index] += held;
            GateHoldMax[index] = Math.Max(val1: GateHoldMax[index], val2: held);
        }
        /// <summary>Counts a section run inside a hold of the gate under a site of its own, with no wait, and leaves its time
        /// out of the entry that encloses it.</summary>
        /// <param name="site">The section's site.</param>
        /// <param name="held">The Stopwatch ticks the section ran.</param>
        public void GateNested(Gate site, long held) {
            GateHeld(site: site, waited: 0L, held: held);
            GateNestedTicks += held;
        }
        /// <summary>Counts one wait for the bake's gate on the frame thread.</summary>
        /// <param name="waited">The wait, in Stopwatch ticks.</param>
        public void FrameGateWaited(long waited) {
            FrameGateWaitTotal += waited;
            FrameGateWaitMax = Math.Max(val1: FrameGateWaitMax, val2: waited);
            FrameGateWaitsSlow += ((waited > (Stopwatch.Frequency / 1000)) ? 1 : 0);
        }
        /// <summary>Adds the time since a timestamp to a stage's account.</summary>
        /// <param name="stage">The stage.</param>
        /// <param name="since">The Stopwatch timestamp the stage began at.</param>
        /// <returns>The ticks the stage took.</returns>
        public long Add(Stage stage, long since) {
            var ticks = (Stopwatch.GetTimestamp() - since);

            _ = Interlocked.Add(location1: ref StageTicks[((int)stage)], value: ticks);

            return ticks;
        }

        // The accounts as done-line fields: each stage's thread seconds, then the frames' use of their shares.
        internal string DescribeCheckpoints() => string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"checkpoints=[sampling:{SampleHits}/{SampleMisses},contouring:{ContourHits}/{ContourMisses},simplification:{SimplifyHits}/{SimplifyMisses}]");

        internal string Describe(Func<string>? dispatches) {
            var stages = string.Join(separator: ",", values: Enum.GetValues<Stage>().Select(selector: stage => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{stage.ToString().ToLowerInvariant()}:{(Interlocked.Read(location: ref StageTicks[((int)stage)]) / ((double)Stopwatch.Frequency)):0.0}")));
            var takes = Math.Max(val1: 1L, val2: Takes);
            var kinds = string.Join(separator: ",", values: Enumerable.Range(start: 0, count: KindNames.Length).Select(selector: kind => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{KindNames[kind]}:{KindSamples[kind]}")));

            return string.Create(provider: CultureInfo.InvariantCulture, handler: $" joined=[groups:{Joined},landed:{JoinedLanded},abandoned:{JoinedAbandoned},stalled:{JoinedStalled},samples-saved:{SamplesSaved}] {DescribeCheckpoints()} kinds=[{kinds}] bound=[groups:{BoundGroups},refused:{BoundRefused},walks:{BoundWalks},skipped:{BoundSkipped}] cpu=[{stages}] batches={Batches} takes={Takes} idle={IdleTakes} stop=[taps:{StopTaps},samples:{StopSamples},batch:{StopBatch},readback:{StopReadback},drained:{StopDrained}] host-refusals=[readback-waits:{ReadbackWaits},refine-waits:{Interlocked.Read(location: ref RefinementWaits)},copy-waits:{CopyWaits},readmitted:{Readmissions}] per-take=[waiting:{(WaitingGroups / ((double)takes)):0.0},flying:{(FlyingGroups / ((double)takes)):0.0},working:{(WorkingGroups / ((double)takes)):0.0}]{dispatches?.Invoke()} frame-gate=[max:{((FrameGateWaitMax * 1000d) / Stopwatch.Frequency):0.00}ms,total:{((FrameGateWaitTotal * 1000d) / Stopwatch.Frequency):0.0}ms,over-1ms:{FrameGateWaitsSlow}] gate=[{DescribeGate()}] managed=[{DescribeManaged()}] tail={PoolTailSeconds():0.0}s");
        }
        // Each gate site entered: its entries, the milliseconds it waited and held in all, and its longest hold.
        private string DescribeGate() {
            static double Milliseconds(long ticks) => ((ticks * 1000d) / Stopwatch.Frequency);

            return string.Join(separator: ",", values: Enum.GetValues<Gate>().Where(predicate: site => (GateEntries[((int)site)] > 0)).Select(selector: site => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{site.ToString().ToLowerInvariant()}:{GateEntries[((int)site)]}/wait:{Milliseconds(GateWaitTicks[((int)site)]):0.0}ms/hold:{Milliseconds(GateHoldTicks[((int)site)]):0.0}ms/max:{Milliseconds(GateHoldMax[((int)site)]):0.00}ms")));
        }
        // The process's managed allocation, collector pause and collections by generation since the bake started.
        private string DescribeManaged() => string.Create(provider: CultureInfo.InvariantCulture, handler: $"allocated:{((GC.GetTotalAllocatedBytes(precise: false) - m_allocatedAtStart) / 1.0e9):0.00}GB,rented-large-fresh:{((RentedArrays.LargeFreshBytes - m_freshLargeAtStart) / 1.0e9):0.00}GB,rented-large-reused:{((RentedArrays.LargeReusedBytes - m_reusedLargeAtStart) / 1.0e9):0.00}GB,gc-pause:{(GC.GetTotalPauseDuration() - m_pausedAtStart).TotalSeconds:0.00}s,collections:{(GC.CollectionCount(generation: 0) - m_gen0AtStart)}/{(GC.CollectionCount(generation: 1) - m_gen1AtStart)}/{(GC.CollectionCount(generation: 2) - m_gen2AtStart)}");
    }

    // When the bake's pool last took work with more still queued behind every worker (PoolSaturated): the done line
    // reports the seconds from then to the bake's end, the serial tail in which the last groups finish alone.
    private static long PoolSaturatedAt;

    // The lines written while the calling thread holds a bake's gate, written once it lets the last one go: a write to a
    // piped standard error can block on its reader, and a frame thread blocked there under the gate holds every worker
    // waiting for it. The one door every line goes through (Say); a thread outside a gate writes at once.
    [ThreadStatic]
    private static int GatesHeld;
    [ThreadStatic]
    private static List<Func<string>>? HeldLines;

    /// <summary>Notes that the calling thread took a bake's gate, so the lines it writes meanwhile wait for the gate's
    /// release (<see cref="GateLeft"/>).</summary>
    public static void GateEntered() => GatesHeld++;
    /// <summary>Notes that the calling thread let a bake's gate go, and writes the lines held for it once it holds no
    /// other: after the lock is released, never under it.</summary>
    public static void GateLeft() {
        if ((--GatesHeld > 0) || (HeldLines is not { Count: > 0 } held)) { return; }

        var lines = held.ToArray();

        held.Clear();
        foreach (var line in lines) { Console.Error.WriteLine(value: line()); }
    }
    private static void Say(string line) => Say(line: () => line);
    // A line whose text is read where it is written, so a line that samples the process (the memory account) reads it
    // outside the gate.
    private static void Say(Func<string> line) {
        if (GatesHeld > 0) { (HeldLines ??= []).Add(item: line); return; }
        Console.Error.WriteLine(value: line());
    }

    /// <summary>Records that the bake's pool took work with more still queued, so every worker was busy.</summary>
    public static void PoolSaturated() => Volatile.Write(location: ref PoolSaturatedAt, value: Stopwatch.GetTimestamp());

    // The seconds since the pool last had work queued behind every worker.
    private static double PoolTailSeconds() {
        var at = Volatile.Read(location: ref PoolSaturatedAt);

        return ((at == 0L) ? 0d : Stopwatch.GetElapsedTime(startingTimestamp: at).TotalSeconds);
    }

    private static readonly string[] KindNames = ["refine", "slab", "fine", "crossing", "gradient", "crease", "material", "skin"];

    /// <summary>Writes the line a restart, resample or promotion that began work opens its bake with, and has the store
    /// explain, off the calling thread, why it missed or record what it now holds.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="residencyId">The bake's own identifier.</param>
    /// <param name="groups">The groups the bake holds.</param>
    /// <param name="dynamic">How many of them are dynamic.</param>
    /// <param name="instances">The program's instances.</param>
    /// <param name="cause">What began the work: program, resample or promote.</param>
    /// <param name="baking">The groups begun as a bake.</param>
    /// <param name="loading">The groups begun as a load from the store.</param>
    /// <param name="shared">The groups waiting on another group of their geometry the same restart began.</param>
    /// <param name="joined">The groups waiting through the library on a bake of their geometry another group already
    /// runs, in this residency or another (<see cref="SdfMeshBakeShare"/>).</param>
    /// <param name="named">The first groups begun, at most <see cref="NamedMost"/>.</param>
    /// <param name="disk">The library's store, or <see langword="null"/>.</param>
    /// <param name="identity">The identity the bake began under.</param>
    /// <param name="stencil">The ops of the program whose gradient samples take the stencil's taps instead of one
    /// forward-mode walk, by name, or <c>none</c>.</param>
    public static void Start(string residency, string residencyId, int groups, int dynamic, int instances, string cause, int baking, int loading, int shared, int joined, IReadOnlyList<Begun>? named, SdfMeshStore? disk, SdfMeshBakeIdentity identity, string stencil) {
        var names = string.Join(separator: ',', values: (named ?? []).Select(selector: static group => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{group.Selector}/{(group.Dynamic ? $"slot{group.Place}" : $"union{group.Place}")}/{(group.Loading ? "load" : "bake")}")));

        Say(line: $"[sdf.mesh-bake] start residency={residency} residency-id={residencyId} groups={groups} baking={baking} loading={loading} shared={shared} joined={joined} dynamic={dynamic} instances={instances} cause={cause} begun=[{names}{(((baking + loading) > NamedMost) ? ",…" : "")}] stencil-ops=[{stencil}]");
        // The store's account reads its directory, so it runs off the frame's thread.
        if (disk is not null) {
            _ = Task.Run(action: () => {
                if (baking > 0) {
                    Console.Error.WriteLine(value: $"[sdf.mesh-bake] miss residency={residency} baking={baking}: {disk.ExplainMisses(identity: identity)}");
                } else {
                    disk.Record(identity: identity);
                }
            });
        }
    }
    /// <summary>Writes the done line of a bake whose last group finished.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="residencyId">The bake's own identifier.</param>
    /// <param name="groups">Each group's mesh and outcome.</param>
    /// <param name="accounts">The bake's counters.</param>
    /// <param name="seconds">The bake's wall time.</param>
    /// <param name="dispatches">What the dispatches that answered the bake report, or <see langword="null"/>.</param>
    public static void Done(string residency, string residencyId, IReadOnlyList<Finished> groups, Accounts accounts, double seconds, Func<string>? dispatches) {
        var meshes = groups.Count(predicate: static group => (group.Mesh is not null));
        var triangles = groups.Sum(selector: static group => ((long)(group.Mesh?.TriangleCount ?? 0)));
        var vertices = groups.Sum(selector: static group => ((long)(group.Mesh?.Positions.Length ?? 0)));
        // The meshes some of whose vertices ride a member's slot rather than the group's.
        var skinned = groups.Count(predicate: static group => (group.Mesh is { VertexMembers.IsEmpty: false }));
        // The meshes with coarser levels, and the triangles every mesh draws at its first and second coarser level (its
        // coarsest where it has fewer).
        var pieces = groups.Where(predicate: static group => (group.Mesh is not null)).SelectMany(selector: static group => ((group.Mesh!.Chunks.Count == 0) ? (IReadOnlyList<SdfMesh>)[group.Mesh!] : group.Mesh.Chunks)).ToArray();
        var levelled = pieces.Count(predicate: static mesh => (mesh.Levels.Count > 0));
        var first = pieces.Sum(selector: static mesh => ((long)mesh.LevelOf(level: 1).TriangleCount));
        var second = pieces.Sum(selector: static mesh => ((long)mesh.LevelOf(level: 2).TriangleCount));
        // The groups that finished with no mesh, each with why: a group the bake drops renders nothing.
        var empty = string.Join(separator: "; ", values: groups.Where(predicate: static group => (group.Mesh is null)).Select(selector: static group => $"{group.Selector}: {group.Outcome}"));

        Say(line: (($"[sdf.mesh-bake] done residency={residency} residency-id={residencyId} groups={groups.Count} meshes={meshes} dispatches={accounts.Dispatches} samples={accounts.Samples} discarded={accounts.Discarded} returned={accounts.Returned} taps={accounts.Taps} refined={accounts.RefinedCells} edges={accounts.CutEdges} softened={accounts.Softened} merged={accounts.Merged} loaded={accounts.Loaded} skinned={skinned} triangles={triangles} vertices={vertices} levelled={levelled} level1={first} level2={second} seconds={seconds:0.000}" + accounts.Describe(dispatches: dispatches)) + ((empty.Length > 0) ? $" empty=[{empty}]" : string.Empty)));
    }
    /// <summary>Writes the line for a stage that held one group's worker past <see cref="SlowStageSeconds"/>, so the
    /// bake's long poles can be found; a faster stage writes nothing.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="dynamic">Whether the group is dynamic.</param>
    /// <param name="instances">The group's instance count.</param>
    /// <param name="stage">The stage.</param>
    /// <param name="ticks">The Stopwatch ticks the stage took.</param>
    /// <param name="detail">What the stage measured.</param>
    public static void Slow(string residency, int selector, bool dynamic, int instances, Stage stage, long ticks, in Detail detail) {
        if (ticks <= (SlowStageSeconds * Stopwatch.Frequency)) { return; }

        var text = stage switch {
            Stage.Prepare or Stage.Crossings => $"dims={detail.Dims} factor={detail.Factor} measured={detail.Measured} points={detail.Count}",
            Stage.Contour => $"factor={detail.Factor} measured={detail.Measured} quads={detail.Count}",
            Stage.Land => $"triangles={detail.Count} kept={detail.Kept}",
            _ => string.Empty,
        };

        Say(line: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.mesh-bake] slow residency={residency} group={selector} {(dynamic ? "dynamic" : $"instances={instances}")} stage={stage.ToString().ToLowerInvariant()} seconds={(ticks / ((double)Stopwatch.Frequency)):0.00} {text}"));
    }
    // The bake's own room, which its admissions are refused on: the machine's limit on play and authoring together and
    // what authoring has left of the machine's measured room past its margin (play's room in a player's build).
    private static string AuthoringRoom() {
        var host = MemoryAccount.Shared.ReadAdmission(pool: Puck.Abstractions.Memory.PhysicalMemory.Host);

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"machine-limit={host.MachineLimit} low-memory={(host.MachineLowMemory ? "yes" : "no")} authoring={(host.AuthoringOnPlayBar ? "on-play-bar" : "apart")} room={host.RoomFor(allowance: MemoryAllowance.Authoring)}");
    }
    /// <summary>Writes the line for a group the memory account could not admit beside the groups already admitted.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="peak">The group's peak bytes.</param>
    /// <param name="admitted">The groups admitted this take.</param>
    public static void Deferred(string residency, int selector, long peak, int admitted) => Say(line: () => $"[sdf.mesh-bake] deferred residency={residency} group={selector} peak={peak} admitted={admitted} {AuthoringRoom()}");
    /// <summary>Writes the line for a group admitted alone as the process's one forced job, whatever the account's host
    /// pool holds.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="peak">The group's peak bytes.</param>
    public static void AdmittedAlone(string residency, int selector, long peak) => Say(line: () => $"[sdf.mesh-bake] admitted alone residency={residency} group={selector} peak={peak} {AuthoringRoom()}");
    /// <summary>Writes the line for a bake whose take found no room to promise the readback that answers a lattice; the
    /// lattice waits for a later take.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="bytes">The readback the bake would have promised, in bytes.</param>
    public static void ReadbackDeferred(string residency, long bytes) => Say(line: () => $"[sdf.mesh-bake] readback deferred residency={residency} bytes={bytes} {AuthoringRoom()}");
    /// <summary>Writes the line for a group whose refinement waits at the head of the refinement queue for room for its
    /// peak; no refinement queued behind it starts before it.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="peak">The refinement's peak bytes.</param>
    /// <param name="waiting">The refinements queued, every bake's.</param>
    /// <param name="refining">The refinements holding their peak, every bake's.</param>
    public static void RefinementDeferred(string residency, int selector, long peak, int waiting, int refining) => Say(line: () => $"[sdf.mesh-bake] refinement deferred residency={residency} group={selector} peak={peak} waiting={waiting} refining={refining} {AuthoringRoom()}");
    /// <summary>Writes the line for a group whose copy into the mesh library (its published mesh, or its renumbered take
    /// of its leader's) the host memory account refused; it waits where it stands until the group can reserve the copy's
    /// bytes.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="owner">The memory owner the account refused.</param>
    /// <param name="bytes">The bytes the copy waits for.</param>
    public static void CopyDeferred(string residency, int selector, string owner, long bytes) => Say(line: () => $"[sdf.mesh-bake] copy deferred residency={residency} group={selector} owner={owner} bytes={bytes} room={MemoryAccount.Shared.ReadAdmission(pool: Puck.Abstractions.Memory.PhysicalMemory.Host).Room}");
    /// <summary>Writes the line for a group whose stage the host memory account refused, which starts its final lattice
    /// again under an admission of at least <paramref name="floor"/> bytes.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="owner">The memory owner the account refused.</param>
    /// <param name="bytes">The bytes refused.</param>
    /// <param name="floor">The peak the group's next admission reserves at least.</param>
    /// <param name="refusal">The refusal, whose stack names the allocation no admission covered.</param>
    public static void Readmitted(string residency, int selector, string owner, long bytes, long floor, Exception refusal) => Say(line: () => $"[sdf.mesh-bake] readmitted residency={residency} group={selector} owner={owner} refused={bytes} floor={floor}\n{refusal.StackTrace}");
    /// <summary>Writes the line for a group whose bake refused.</summary>
    /// <param name="selector">The group's selector.</param>
    /// <param name="refusal">Why.</param>
    public static void Refused(int selector, string refusal) => Say(line: $"[sdf.mesh-bake] group {selector} refused: {refusal}");
    /// <summary>Names a rest skin that fell back to rigid owner contours.</summary>
    public static void SkinSplit(int selector, SdfMeshGeometry geometry, string? reason) => Say(line: $"[sdf.mesh-bake] group {selector} geometry={geometry.Hash:x32} bakes as a crease: {reason}");
    /// <summary>Writes the line for a tier a bake that draws stored tiers alone asked and the store lacked
    /// (<see cref="SdfMeshCache.RungsMissed"/>), naming the group so the cache's producer can be asked why.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="group">The group as planned at the tier asked: its selector, shape (which every tier of it shares),
    /// geometry, the frame inputs that geometry reads, and whether the tier is the first detail of a floor planned on
    /// arrival or a plan's own tier.</param>
    /// <param name="drawn">The tier drawn in its place.</param>
    /// <param name="first">Whether the tier asked is a floor's first detail.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    public static void RungMissed(string residency, SdfMeshBakeGroup group, int drawn, bool first) {
        ArgumentNullException.ThrowIfNull(argument: group);

        var geometry = group.Geometry;
        var reads = string.Join(separator: ",", values: new[] {
            ((!group.Dynamic && geometry.ReadsSlots) ? "poses" : null),
            (geometry.ReadsOperands ? "operands" : null),
            (geometry.ReadsSampled ? "sampled" : null),
        }.Where(predicate: static read => (read is not null)));

        Say(line: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.mesh-bake] rung-missed residency={residency} group={group.Selector} {(group.Dynamic ? "dynamic" : "static")} shape={group.Shape:x32} geometry={group.Sealed.Hash:x32} reads={((reads.Length == 0) ? "none" : reads)} {(first ? "first-detail" : "plan")} asked={group.Tier} drawn={drawn} tiers={group.FinestTier}-{group.CoarsestTier}"));
    }
    /// <summary>Writes the line for a group whose store entry the store refused.</summary>
    /// <param name="selector">The group's selector.</param>
    /// <param name="refusal">Why.</param>
    public static void StoreRefused(int selector, string refusal) => Say(line: $"[sdf.mesh-bake] group {selector} store refused: {refusal}");
    /// <summary>Writes the lines for a group whose stage threw: the group's geometry, the box and lattice it had reached,
    /// and the exception with its stack, so the failure can be found again and baked alone.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <param name="low">The low corner of the group's box.</param>
    /// <param name="high">The high corner of the group's box.</param>
    /// <param name="refinements">The narrowing passes the group had taken.</param>
    /// <param name="lattice">The group's final lattice and its refinement factor, once it has one.</param>
    /// <param name="exception">What was thrown.</param>
    public static void Failed(string residency, int selector, SdfMeshGeometry geometry, Vector3 low, Vector3 high, int refinements, (SdfMeshBakeRequest Request, int Factor)? lattice, Exception exception) => Say(line: string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"[sdf.mesh-bake] residency={residency} group={selector} geometry={geometry.Hash:x32} failed: {exception.GetType().Name}: {exception.Message} box=({low.X:R}, {low.Y:R}, {low.Z:R})..({high.X:R}, {high.Y:R}, {high.Z:R}) refinements={refinements} {((lattice is { } final) ? $"dims={final.Request.Dims} cell=({final.Request.Cell.X:R}, {final.Request.Cell.Y:R}, {final.Request.Cell.Z:R}) factor={final.Factor}" : "no final lattice")}\n{exception}"
    ));
    /// <summary>Writes the line for a pool work item that threw past its own settling.</summary>
    /// <param name="exception">What was thrown.</param>
    public static void WorkFailed(Exception exception) => Say(line: $"[sdf.mesh-bake] work failed: {exception}");
}
