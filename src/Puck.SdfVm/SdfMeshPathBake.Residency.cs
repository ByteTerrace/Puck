using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.SignedDistance;
using BakeStage = Puck.SdfVm.SdfMeshBakeReport.Stage;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    /// <summary>Gets the demand points the bake measures its groups by: the residency's, which it observes from each frame
    /// before dressing it. A bake no residency observes for has none, and keeps its plan's order.</summary>
    public SdfMeshDemand Demand { get; init; } = new();

    /// <summary>Gets or sets what the done line reports of the dispatches that answered the bake (the residency's
    /// background GPU work, <see cref="SdfGpuWork.Describe"/>), or <see langword="null"/> for nothing. It only reports:
    /// no mesh depends on it.</summary>
    public Func<string>? DispatchReport { get; set; }

    // The latest dressed frame's dynamic transforms, which carry a dynamic group's demand box into world space.
    private IReadOnlyList<DynamicTransform> m_demandTransforms = [];

    // When the latest bake that baked or loaded a group started (a Stopwatch timestamp; zero before one).
    private long m_bakeStarted;
    // The store loads the latest bake queued and finished, and when its first began and last ended.
    private int m_loadsQueued;
    private int m_loadsDone;
    private long m_loadFirst;
    private long m_loadLast;
    // How long the latest plan made on a build's thread, and the latest restart on the frame's, took.
    private double m_planMilliseconds;
    private double m_restartMilliseconds;
    // Whether the frame thread has seen every group resident since the groups or a mesh last changed.
    private bool m_allResident;

    // The work waiting off the frame's thread (a store load, a contour stage, a landing) for every bake in the process,
    // each for its group; the lattice answers, which every later stage of their groups waits on, taken first in the
    // order they arrived; and the workers taking them, at most Workers at once. One pool serves every residency's bake,
    // so residencies baking together share the processors rather than each taking all of them.
    private static readonly Lock WorkGate = new();
    // Every change a bake's groups see happens inside a pool work item (a stage, a contour or an answer), so the pool's
    // completions are the producer signal an offline caller waits on (WaitForProgress). A System.Threading.Lock has no
    // Monitor.Wait, so the count has its own monitor.
    private static readonly object ProgressGate = new();
    private static long ProgressCount;
    private static readonly List<(Group Group, Action Work)> WorkQueue = [];
    private static readonly Queue<Action> AnswerQueue = new();

    private static int RunningWorkers;

    // The workers that run at once: one a processor.
    private static int Workers => Math.Max(val1: 1, val2: Environment.ProcessorCount);

    // A slot is handed to exactly one running task under WorkGate; workspaces belong to the groups they serve.
    private sealed class WorkerSlot {
        public bool Running;
    }
    private static readonly WorkerSlot[] WorkerSlots = [.. Enumerable.Range(0, Workers).Select(selector: static _ => new WorkerSlot())];

    // What the bake counts for its done line, and what that line says (SdfMeshBakeReport, outside the bake's code identity):
    // the stage ticks, the samples of each kind, the takes made while a group still owed a lattice and what ended each
    // take's batch, the groups waiting on a dispatch, in flight and on the workers, and the frame thread's waits on the
    // bake's gate, which show a worker holding it through array-sized work as frame time.
    private readonly SdfMeshBakeReport.Accounts m_accounts = new();

    // Takes the gate on the frame thread, counting the wait.
    private Lock.Scope EnterFrameGate() {
        var started = Stopwatch.GetTimestamp();
        var scope = m_gate.EnterScope();

        m_accounts.FrameGateWaited(waited: (Stopwatch.GetTimestamp() - started));

        return scope;
    }

    // Adds the time since a timestamp to a stage's account; the report names a stage that held one group's worker for
    // seconds, so the bake's long poles can be found.
    private void Account(BakeStage stage, long since, Group? group = null, SdfMeshBakeReport.Detail detail = default) {
        var ticks = m_accounts.Add(stage: stage, since: since);

        if (group is not null) {
            SdfMeshBakeReport.Slow(detail: detail, dynamic: group.Dynamic, instances: group.Instances, residency: Residency, selector: group.Selector, stage: stage, ticks: ticks);
        }
    }
    // Measures every group still owing a mesh by its demand box at the demand points; with none, the priorities stand as
    // they were and the plan's order decides. It runs on the frame's thread with the gate open: the frame's thread alone
    // replaces the group list and the copy index (Restart, Settle, IndexCopies), and a priority only orders work, never
    // changes a mesh, so a worker reading one mid-measure, or finishing a group this measures, orders no mesh differently.
    private void Prioritize() {
        if (Demand.Count == 0) { return; }
        // With every group done there is nothing to order: the done cursor makes this check constant once the bake ends.
        lock (m_gate) {
            while ((m_doneThrough < m_groups.Count) && m_groups[m_doneThrough].Done) { m_doneThrough++; }
            if (m_doneThrough >= m_groups.Count) { return; }
        }
        foreach (var group in m_groups) {
            if (group.Done || !group.HasDemandBox) { continue; }

            group.Priority = MeasureDemand(group, priority: true);
        }
        // A group baking geometry other groups wait on ranks as the most wanted of them.
        foreach (var group in m_groups) {
            if (!group.Done && (group.Leader is { } leader)) { leader.Priority = MathF.Max(x: leader.Priority, y: group.Priority); }
        }
    }
    // Finishes every group waiting on a group that has finished baking the same geometry (Group.Leader): it takes the
    // mesh the library now holds, renumbered for its own materials, or the leader's empty outcome; one whose mesh the
    // library no longer holds bakes it itself. Runs under the gate.
    private void Follow() {
        foreach (var group in m_groups) {
            if (group.Done || (group.Leader is not { Done: true } leader)) { continue; }
            group.Leader = null;
            if (leader.Mesh is null) {
                group.Outcome = leader.Outcome;
                group.Done = true;
                // A follower rebaking keeps drawing its previous mesh until the result lands; an empty one lands too.
                Draw(group: group, mesh: null);
                continue;
            }
            // Renumbering the mesh for the follower's materials copies arrays as large as the mesh, so it runs on the
            // workers, outside the gate the frame takes.
            group.Contouring = true;
            ScheduleFollowed(group: group);
        }
    }
    // The closure lives here so that Follow's scan of every group allocates none: a lambda capturing the loop variable
    // allocates its closure on entering each iteration, before the skip checks.
    private void ScheduleFollowed(Group group) => Schedule(group: group, work: () => TakeFollowed(group: group));
    // Takes the mesh a follower's leader landed, renumbered for the follower's materials; a follower whose mesh the library
    // no longer holds bakes it itself.
    private void TakeFollowed(Group group) {
        var taken = Library.TryTake(geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: out var mesh);

        lock (m_gate) {
            group.Contouring = false;
            if (!m_groups.Contains(item: group)) { mesh?.Release(); return; }
            if (!taken) {
                _ = Begin(group: group);
                return;
            }
            group.Done = true;
            group.Stored = true;
            Draw(group: group, mesh: mesh);
            Finish();
        }
    }
    // Queues a group's work off the frame's thread, taken by priority (TakeWork) or, urgent, before any other in the
    // order it came, and starts a worker for it if one is free. Work that takes the bake's gate waits while a caller
    // holds it, as a restart does.
    private bool Schedule(Group group, Action work) {
        if (!group.QueueWork()) { return false; }
        Enqueue(group: group, urgent: false, work: work);
        return true;
    }
    // Runs work on the bake's pool ahead of every queued stage, in the order it came, as a lattice's answer runs: a
    // completed dispatch's samples are read and decoded there, off the frame's thread.
    internal static void RunFirst(Action work) {
        lock (WorkGate) {
            AnswerQueue.Enqueue(item: work);
            StartWorkers();
        }
    }
    // Queues work for which the group's job count is already taken (Group.QueueWork). A stage that throws finishes its
    // group with no new mesh (Fail), so its reservation and workspace are released and the bake still settles. An
    // urgent answer runs even once its group retires, since it owns its decoded samples and frees them.
    private void Enqueue(Group group, Action work, bool urgent) {
        void Run() {
            try {
                if (urgent || !group.Retired) { work(); }
            } catch (Exception exception) {
                Fail(exception: exception, group: group);
            } finally {
                group.EndWork();
            }
        }
        lock (WorkGate) {
            if (urgent) {
                AnswerQueue.Enqueue(item: Run);
            } else {
                WorkQueue.Add(item: (group, Run));
            }
            StartWorkers();
        }
    }
    // Finishes a group whose stage threw: it keeps drawing the mesh it drew before, if any, and a later program plans it
    // again rather than keeping the failure (Keeps).
    private void Fail(Group group, Exception exception) {
        SdfMeshBakeReport.Failed(exception: exception, residency: Residency, selector: group.Selector);
        lock (m_gate) {
            group.Contouring = false;
            group.InFlight = false;
            if (group.Done || !m_groups.Contains(item: group)) { return; }
            group.Failed = true;
            group.Outcome = $"failed: {exception.GetType().Name}";
            group.Next = null;
            group.Done = true;
            Finish();
        }
    }
    // Whether the pool's queued stages, every residency's, fill a round of its workers. A worker starts whenever a slot is
    // free (StartWorkers), so such a queue keeps every worker busy for a round, which covers the GPU rounds a newly
    // admitted group takes before its first stage; a group admitted beyond it would only hold its lattice in the queue.
    private static bool Backlogged() {
        lock (WorkGate) { return (WorkQueue.Count >= WorkerSlots.Length); }
    }
    // Called only with WorkGate held. The task's finally returns ownership of the slot after its last borrower ends.
    private static void StartWorkers() {
        foreach (var slot in WorkerSlots) {
            if (RunningWorkers >= (WorkQueue.Count + AnswerQueue.Count)) { break; }
            if (slot.Running) { continue; }
            slot.Running = true;
            RunningWorkers++;
            _ = Task.Run(action: () => Work(slot: slot));
        }
    }

    /// <summary>Gets a count that advances each time a pool worker finishes a work item of any bake. Read it before
    /// asking the bake what it owes, then pass it to <see cref="WaitForProgress"/>, so a completion between the read and
    /// the wait is never missed.</summary>
    public static long Progress {
        get {
            lock (ProgressGate) { return ProgressCount; }
        }
    }
    /// <summary>Sleeps until a pool worker has finished a work item since <paramref name="since"/>, or the timeout
    /// elapses.</summary>
    /// <param name="since">The <see cref="Progress"/> the caller read before it last looked at the bake.</param>
    /// <param name="timeout">The longest time to wait.</param>
    /// <returns><see langword="true"/> when work finished since <paramref name="since"/>; <see langword="false"/> on
    /// timeout.</returns>
    public static bool WaitForProgress(long since, TimeSpan timeout) {
        lock (ProgressGate) {
            if (ProgressCount != since) { return true; }

            return (Monitor.Wait(obj: ProgressGate, timeout: timeout) && (ProgressCount != since));
        }
    }

    /// <summary>Retires every group of the bake, for a residency released, without waiting for any of them: their queued
    /// work is dropped before it runs, a stage running for one queues no further stage, an answer arriving for one is
    /// dropped, and each group frees its workspace when its last running job ends.</summary>
    public void Retire() {
        lock (m_gate) {
            foreach (var group in m_groups.Concat(second: m_retiredGroups)) {
                group.Retired = true;
                group.InFlight = false;
            }
        }
        lock (WorkGate) { _ = WorkQueue.RemoveAll(match: item => { if (!item.Group.Retired) { return false; } item.Group.EndWork(); return true; }); }
    }

    // A worker: runs the queued work, answers first, then the highest priority, until none is queued.
    private static void Work(WorkerSlot slot) {
        try {
            while (TakeWork() is { } work) {
                // A work item that throws has already settled what it owns (a group's stage fails its group; an answer
                // returns its batch's unanswered lattices), so the line names the fault, out of memory included, and the
                // worker carries on rather than ending silently.
                try {
                    work();
                } catch (Exception exception) {
                    SdfMeshBakeReport.WorkFailed(exception: exception);
                } finally {
                    lock (ProgressGate) {
                        ProgressCount++;
                        Monitor.PulseAll(obj: ProgressGate);
                    }
                }
            }
        } finally {
            lock (WorkGate) {
                RunningWorkers--;
                slot.Running = false;
                StartWorkers();
            }
        }
    }
    // Takes the earliest queued answer, else the queued work whose group has the highest priority, the earliest queued
    // among equals, or null when none is queued. A group's priority is read as the frame thread last left it.
    private static Action? TakeWork() {
        lock (WorkGate) {
            if (AnswerQueue.TryDequeue(result: out var answer)) { return answer; }

            var best = -1;

            // Work of a group its bake no longer plans (a program replaced it, or its residency was released) is dropped
            // before it runs.
            _ = WorkQueue.RemoveAll(match: static item => { if (!item.Group.Retired) { return false; } item.Group.EndWork(); return true; });
            for (var index = 0; (index < WorkQueue.Count); index++) {
                if ((best < 0) || (WorkQueue[index].Group.Priority > WorkQueue[best].Group.Priority)) { best = index; }
            }
            if (best < 0) { return null; }

            var work = WorkQueue[best].Work;

            WorkQueue.RemoveAt(index: best);
            return work;
        }
    }
    // A group's demand box in world space: a dynamic group's carried out of its slot's frame by the slot's transform, as
    // the world-axis box about the turned one.
    private (Vector3 Center, Vector3 Half) WorldBox(Group group) {
        if (!group.Dynamic || (group.Slot >= m_demandTransforms.Count)) { return (group.DemandCenter, group.DemandHalf); }
        var transform = m_demandTransforms[group.Slot];
        var units = SdfMeshGeometryBuilder.UnitsOf(transform: transform);
        var orientation = ((transform.Orientation == default) ? Quaternion.Identity : transform.Orientation);
        var matrix = ((Matrix4x4.CreateScale(units) * Matrix4x4.CreateFromQuaternion(orientation)) * Matrix4x4.CreateTranslation(transform.Position));
        return TransformDemandBox(group, matrix);
    }
    private static (Vector3 Center, Vector3 Half) TransformDemandBox(Group group, Matrix4x4 matrix) {
        var half = group.DemandHalf;
        return (Vector3.Transform(group.DemandCenter, matrix), new Vector3(
            ((MathF.Abs(matrix.M11) * half.X) + (MathF.Abs(matrix.M21) * half.Y) + (MathF.Abs(matrix.M31) * half.Z)),
            ((MathF.Abs(matrix.M12) * half.X) + (MathF.Abs(matrix.M22) * half.Y) + (MathF.Abs(matrix.M32) * half.Z)),
            ((MathF.Abs(matrix.M13) * half.X) + (MathF.Abs(matrix.M23) * half.Y) + (MathF.Abs(matrix.M33) * half.Z))));
    }
    // A shared geometry is wanted where its actual occurrences stand. Never union distant occurrence boxes:
    // the empty space between them is neither visible demand nor a nearby surface.
    private float MeasureDemand(Group group, bool priority) {
        if (CopiesOf(group) is not { } copies) {
            var (center, half) = WorldBox(group);
            return (priority ? Demand.Priority(center, half) : Demand.DistanceFrom(index: 0, center: center, half: half));
        }
        var result = (priority ? 0f : float.PositiveInfinity);
        foreach (var index in copies) {
            var (center, half) = TransformDemandBox(group, m_demandCopies[index].Matrix);
            var value = (priority ? Demand.Priority(center, half) : Demand.DistanceFrom(index: 0, center: center, half: half));
            result = (priority ? MathF.Max(result, value) : MathF.Min(result, value));
        }
        return result;
    }

    /// <summary>Stamps, on the frame's thread after the frame's mesh region is staged, each group the bake has finished and
    /// each whose mesh's words the region holds, which <see cref="DescribeResidency"/> times from the bake's start.</summary>
    /// <param name="tables">The residency's tables, whose mesh region owes a mesh's words or not
    /// (<see cref="SdfWorldTables.OwesMesh"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="tables"/> is <see langword="null"/>.</exception>
    public void ObserveResidency(SdfWorldTables tables) {
        ArgumentNullException.ThrowIfNull(argument: tables);

        lock (m_gate) {
            if (m_allResident) { return; }

            var now = Stopwatch.GetTimestamp();
            var all = true;

            foreach (var group in m_groups) {
                if (!group.Done) {
                    all = false;
                    continue;
                }
                if (group.LandedAt == 0) { group.LandedAt = now; }
                if (group.ResidentAt != 0) { continue; }
                // A mesh is resident once the frame's draws name it and the region owes none of its words; one the
                // current draws do not name (a template no copy draws) costs the region nothing and counts too.
                if (group.Mesh is { } mesh) {
                    var held = (m_dressedRevision == m_productRevision);

                    if (mesh.Chunks.Count == 0) {
                        held &= !tables.OwesMesh(mesh: mesh);
                    } else {
                        foreach (var chunk in mesh.Chunks) { held &= !tables.OwesMesh(mesh: chunk); }
                    }
                    if (!held) {
                        all = false;
                        continue;
                    }
                }
                group.ResidentAt = now;
            }
            m_allResident = all;
        }
    }
    /// <summary>Describes the latest bake's residency: its groups, when they landed (finished) and became resident (their
    /// words in the mesh region) counted from its start, those near the first demand point (the primary seat's eye)
    /// apart, with when half and nine tenths of those had landed and which landed last, its store loads, the time its
    /// plan and restart took, what moved programs and sampled fields cost the frame thread as counts (frames prepared,
    /// restarts and the groups they planned, resamples and the readers they walked, coalesced and promoted successors), and
    /// the resident meshes' region bytes by level of detail.</summary>
    /// <param name="radius">The distance, in metres, from the first demand point to a group's demand box within which
    /// the group counts as near.</param>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeResidency(float radius) {
        lock (m_gate) {
            var start = m_bakeStarted;
            var groups = m_groups.Count;
            int landed = 0, resident = 0, near = 0, nearLanded = 0, nearResident = 0;
            long lastLanded = 0, lastResident = 0, nearLastLanded = 0, nearLastResident = 0;
            var vertexBytes = 0L;
            Span<long> levelBytes = stackalloc long[3];
            Span<long> levelTriangles = stackalloc long[3];
            var nearStamps = new List<long>();
            Group? lastNear = null;

            foreach (var group in m_groups) {
                var isNear = (group.HasDemandBox && (Demand.Count > 0) && (MeasureDemand(group, priority: false) <= radius));

                near += (isNear ? 1 : 0);
                if (group.LandedAt != 0) {
                    landed++;
                    lastLanded = Math.Max(val1: lastLanded, val2: group.LandedAt);
                    if (isNear) {
                        nearLanded++;
                        nearStamps.Add(item: group.LandedAt);
                        if (group.LandedAt >= nearLastLanded) {
                            nearLastLanded = group.LandedAt;
                            lastNear = group;
                        }
                    }
                }
                if (group.ResidentAt != 0) {
                    resident++;
                    lastResident = Math.Max(val1: lastResident, val2: group.ResidentAt);
                    if (isNear) {
                        nearResident++;
                        nearLastResident = Math.Max(val1: nearLastResident, val2: group.ResidentAt);
                    }
                }
                if (group.Mesh is not { } mesh) { continue; }
                foreach (var piece in ((mesh.Chunks.Count == 0) ? (IReadOnlyList<SdfMesh>)[mesh] : mesh.Chunks)) {
                    var perTriangle = ((3 + (piece.TriangleMaterials.IsEmpty ? 0 : 1)) * sizeof(uint));

                    vertexBytes += ((((long)piece.Positions.Length) * SdfMeshRecord.VertexWords) * sizeof(uint));
                    levelTriangles[0] += piece.TriangleCount;
                    levelBytes[0] += (((long)piece.TriangleCount) * perTriangle);
                    for (var level = 0; ((level < piece.Levels.Count) && (level < 2)); level++) {
                        levelTriangles[(level + 1)] += piece.Levels[level].TriangleCount;
                        levelBytes[(level + 1)] += (((long)piece.Levels[level].TriangleCount) * perTriangle);
                    }
                }
            }

            string Since(long stamp, bool complete) => (!complete
                ? "pending"
                : ((start == 0) ? "-" : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Math.Max(val1: 0d, val2: Stopwatch.GetElapsedTime(endingTimestamp: stamp, startingTimestamp: start).TotalSeconds):0.000}s")));

            var line = new StringBuilder();
            var loadSpan = (((m_loadFirst != 0) && (m_loadLast != 0)) ? Stopwatch.GetElapsedTime(endingTimestamp: m_loadLast, startingTimestamp: m_loadFirst).TotalSeconds : 0d);

            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $"residency={Residency} residency-id={ResidencyId} demand-points={Demand.Count} groups={groups} landed={landed} resident={resident} all-landed={Since(complete: (landed == groups), stamp: lastLanded)} all-resident={Since(complete: (resident == groups), stamp: lastResident)}");
            // When half and nine tenths of the near groups had landed, and which landed last.
            nearStamps.Sort();
            string Share(float share) {
                var count = ((int)MathF.Ceiling(x: (share * near)));

                return Since(complete: ((count > 0) && (count <= nearStamps.Count)), stamp: (((count > 0) && (count <= nearStamps.Count)) ? nearStamps[(count - 1)] : 0));
            }
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" near-radius={radius:0.##}m near={near} near-landed={nearLanded} near-resident={nearResident} near-half-at={Share(share: 0.5f)} near-ninety-at={Share(share: 0.9f)} near-landed-at={Since(complete: (nearLanded == near), stamp: nearLastLanded)} near-resident-at={Since(complete: (nearResident == near), stamp: nearLastResident)}");
            if (lastNear is not null) {
                _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" near-last={lastNear.Selector}{(lastNear.Dynamic ? "/dynamic" : ((lastNear.Instances > 1) ? $"/union{lastNear.Instances}" : "/static"))}/{(lastNear.Mesh?.TriangleCount ?? 0)}tri");
            }
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" bake-age={((start == 0) ? 0d : Stopwatch.GetElapsedTime(startingTimestamp: start).TotalSeconds):0.000}s loads={m_loadsDone}/{m_loadsQueued} load-span={loadSpan:0.000}s plan-ms={m_planMilliseconds:0.0} restart-ms={m_restartMilliseconds:0.0}");
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" frames={m_preparedFrames} restarts={m_restarts} restart-groups={m_restartGroups} resamples={m_resamples} resample-groups={m_resampleGroups} sampled-readers={m_sampledReaders.Count} coalesced={m_coalesced} promoted={m_promoted} successors={m_successors}");
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" vertex-bytes={vertexBytes} level0={levelTriangles[0]}tri/{levelBytes[0]}B level1={levelTriangles[1]}tri/{levelBytes[1]}B level2={levelTriangles[2]}tri/{levelBytes[2]}B");

            return line.ToString();
        }
    }
}
