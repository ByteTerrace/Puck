using System.Diagnostics;
using System.Globalization;
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

        public long Batches;
        public long Discarded;
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
        public long Softened;
        public long StopBatch;
        public long StopDrained;
        public long StopSamples;
        public long StopTaps;
        public long Takes;
        public long Taps;
        public long WaitingGroups;
        public long WorkingGroups;
        public long CutEdges;

        /// <summary>Clears every counter for a new bake.</summary>
        public void Clear() {
            Array.Clear(array: StageTicks);
            Array.Clear(array: KindSamples);
            Takes = IdleTakes = Batches = StopTaps = StopSamples = StopBatch = StopDrained = 0;
            WaitingGroups = FlyingGroups = WorkingGroups = 0;
            FrameGateWaitMax = FrameGateWaitTotal = FrameGateWaitsSlow = 0;
            Dispatches = Samples = Taps = Discarded = RefinedCells = CutEdges = Softened = Merged = Loaded = 0;
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
        internal string Describe(Func<string>? dispatches) {
            var stages = string.Join(separator: ",", values: Enum.GetValues<Stage>().Select(selector: stage => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{stage.ToString().ToLowerInvariant()}:{(Interlocked.Read(location: ref StageTicks[((int)stage)]) / ((double)Stopwatch.Frequency)):0.0}")));
            var takes = Math.Max(val1: 1L, val2: Takes);
            var kinds = string.Join(separator: ",", values: Enumerable.Range(start: 0, count: KindNames.Length).Select(selector: kind => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{KindNames[kind]}:{KindSamples[kind]}")));

            return string.Create(provider: CultureInfo.InvariantCulture, handler: $" kinds=[{kinds}] cpu=[{stages}] batches={Batches} takes={Takes} idle={IdleTakes} stop=[taps:{StopTaps},samples:{StopSamples},batch:{StopBatch},drained:{StopDrained}] per-take=[waiting:{(WaitingGroups / ((double)takes)):0.0},flying:{(FlyingGroups / ((double)takes)):0.0},working:{(WorkingGroups / ((double)takes)):0.0}]{dispatches?.Invoke()} frame-gate=[max:{((FrameGateWaitMax * 1000d) / Stopwatch.Frequency):0.00}ms,total:{((FrameGateWaitTotal * 1000d) / Stopwatch.Frequency):0.0}ms,over-1ms:{FrameGateWaitsSlow}]");
        }
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
    /// <param name="shared">The groups waiting on another group of their geometry.</param>
    /// <param name="named">The first groups begun, at most <see cref="NamedMost"/>.</param>
    /// <param name="disk">The library's store, or <see langword="null"/>.</param>
    /// <param name="identity">The identity the bake began under.</param>
    public static void Start(string residency, string residencyId, int groups, int dynamic, int instances, string cause, int baking, int loading, int shared, IReadOnlyList<Begun>? named, SdfMeshStore? disk, SdfMeshBakeIdentity identity) {
        var names = string.Join(separator: ',', values: (named ?? []).Select(selector: static group => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{group.Selector}/{(group.Dynamic ? $"slot{group.Place}" : $"union{group.Place}")}/{(group.Loading ? "load" : "bake")}")));

        Console.Error.WriteLine(value: $"[sdf.mesh-bake] start residency={residency} residency-id={residencyId} groups={groups} baking={baking} loading={loading} shared={shared} dynamic={dynamic} instances={instances} cause={cause} begun=[{names}{(((baking + loading) > NamedMost) ? ",…" : "")}]");
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

        Console.Error.WriteLine(value: (($"[sdf.mesh-bake] done residency={residency} residency-id={residencyId} groups={groups.Count} meshes={meshes} dispatches={accounts.Dispatches} samples={accounts.Samples} discarded={accounts.Discarded} taps={accounts.Taps} refined={accounts.RefinedCells} edges={accounts.CutEdges} softened={accounts.Softened} merged={accounts.Merged} loaded={accounts.Loaded} skinned={skinned} triangles={triangles} vertices={vertices} levelled={levelled} level1={first} level2={second} seconds={seconds:0.000}" + accounts.Describe(dispatches: dispatches)) + ((empty.Length > 0) ? $" empty=[{empty}]" : string.Empty)));
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

        Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.mesh-bake] slow residency={residency} group={selector} {(dynamic ? "dynamic" : $"instances={instances}")} stage={stage.ToString().ToLowerInvariant()} seconds={(ticks / ((double)Stopwatch.Frequency)):0.00} {text}"));
    }
    /// <summary>Writes the line for a group the memory account could not admit beside the groups already admitted.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="peak">The group's peak bytes.</param>
    /// <param name="admitted">The groups admitted this take.</param>
    /// <param name="available">The machine's free bytes, when read.</param>
    public static void Deferred(string residency, int selector, long peak, int admitted, long? available) => Console.Error.WriteLine(value: $"[sdf.mesh-bake] deferred residency={residency} group={selector} peak={peak} admitted={admitted} budget={MemoryAccount.Shared.Read().Budget} available={available}");
    /// <summary>Writes the line for a group admitted alone, whatever the budget and the machine's free memory read.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="peak">The group's peak bytes.</param>
    /// <param name="available">The machine's free bytes, when read.</param>
    public static void AdmittedAlone(string residency, int selector, long peak, long? available) => Console.Error.WriteLine(value: $"[sdf.mesh-bake] admitted alone residency={residency} group={selector} peak={peak} budget={MemoryAccount.Shared.Read().Budget} available={available}");
    /// <summary>Writes the line for a group whose bake refused.</summary>
    /// <param name="selector">The group's selector.</param>
    /// <param name="refusal">Why.</param>
    public static void Refused(int selector, string refusal) => Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {selector} refused: {refusal}");
    /// <summary>Names a rest skin that fell back to rigid owner contours.</summary>
    public static void SkinSplit(int selector, SdfMeshGeometry geometry, string? reason) => Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {selector} geometry={geometry.Hash:x32} bakes as a crease: {reason}");
    /// <summary>Writes the line for a group whose store entry the store refused.</summary>
    /// <param name="selector">The group's selector.</param>
    /// <param name="refusal">Why.</param>
    public static void StoreRefused(int selector, string refusal) => Console.Error.WriteLine(value: $"[sdf.mesh-bake] group {selector} store refused: {refusal}");
    /// <summary>Writes the line for a group whose stage threw.</summary>
    /// <param name="residency">The residency's name.</param>
    /// <param name="selector">The group's selector.</param>
    /// <param name="exception">What was thrown.</param>
    public static void Failed(string residency, int selector, Exception exception) => Console.Error.WriteLine(value: $"[sdf.mesh-bake] residency={residency} group={selector} failed: {exception.GetType().Name}: {exception.Message}");
    /// <summary>Writes the line for a pool work item that threw past its own settling.</summary>
    /// <param name="exception">What was thrown.</param>
    public static void WorkFailed(Exception exception) => Console.Error.WriteLine(value: $"[sdf.mesh-bake] work failed: {exception}");
}
