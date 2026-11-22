using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Abstractions.Memory;
using Puck.Maths;
using Puck.SignedDistance;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;
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
    /// <summary>Gets or sets what the bake calls, on a worker, when work of its pool has finished since its last take, so
    /// a lattice may wait to be taken: the residency's background GPU work's wake (<see cref="SdfGpuWork.Wake"/>), which
    /// takes it then rather than at the next frame. Called at most once between two takes; <see langword="null"/> for
    /// nothing, and a lattice is then taken at the next pump.</summary>
    public Action? Offered { get; set; }

    // Whether work of the bake finished since its last take: set by a worker, which then calls Offered, and cleared by
    // the take.
    private int m_offered;

    private void Offer() {
        if (Interlocked.Exchange(location1: ref m_offered, value: 1) == 0) { Offered?.Invoke(); }
    }

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
    // The listed groups that have landed and whose mesh has not yet stood in the mesh region, which ObserveResidency walks
    // in place of the whole list: a group joins through Group.AwaitResidency, queued by whichever thread changed it and
    // taken into the set under the gate, and leaves once it stands, is unlisted or is drawn again. The invariant, that
    // every listed done group with no ResidentAt is here or queued, rests on that one door; the groups that have not
    // landed are the take's (m_tally) and the hole count's (m_unshown).
    private readonly System.Collections.Concurrent.ConcurrentQueue<Group> m_landed = new();
    private readonly List<Group> m_unresident = [];
    // The counts of the groups still baking that each take reads.
    private readonly GroupTally m_tally = new();
    // The groups still baking that have nothing to draw, are demanded past a pixel and are not retired, as the latest
    // Prioritize measured them; the holes of a bake before its meshes land (ObserveResidency counts the rest).
    private int m_unshown;

    // The work waiting off the frame's thread (a store load, a contour stage, a landing) for every bake in the process,
    // each for its group; the lattice answers, which every later stage of their groups waits on, taken first in the
    // order they arrived; and the workers taking them. One pool serves every residency's bake, so residencies baking
    // together share the processors rather than each taking all of them. The workers are threads of the pool's own, one
    // a processor at normal priority, started with the first work queued: a bake's stages run for seconds, and held as
    // shared thread-pool items they kept the process's other pool work waiting while the runtime injected threads. An
    // idle worker blocks on an operating-system semaphore, so nothing spins.
    private static readonly Lock WorkGate = new();
    // Every change a bake's groups see happens inside a pool work item (a stage, a contour or an answer) or a batch's
    // reader (Progressed), so their completions are the producer signal an offline caller waits on (WaitForProgress). A
    // System.Threading.Lock has no Monitor.Wait, so the count has its own monitor.
    private static readonly object ProgressGate = new();

    private static long ProgressCount;

    private static readonly List<(Group Group, Action Work)> WorkQueue = [];
    private static readonly Queue<Action> AnswerQueue = new();
    // The pool's backlog as time, which paces admission: the workers time each queued stage, and a group's first
    // queued stage times its lead from admission.
    private static readonly SdfMeshBakeBacklog PoolBacklog = new();

    // The workers: one a processor.
    private static readonly int Workers = Math.Max(val1: 1, val2: Environment.ProcessorCount);
    // The workers' threads, started with the first work queued, and the semaphore an idle worker waits on. Under
    // WorkGate: the workers waiting for work, and the wakes released that no worker has taken yet, never more than the
    // queued work an idle worker could take.
    private static Thread[]? WorkerThreads;
    private static readonly Semaphore WorkWake = new(initialCount: 0, maximumCount: int.MaxValue);
    private static int IdleWorkers;
    private static int PendingWakes;
    // What the bake counts for its done line, and what that line says (SdfMeshBakeReport, outside the bake's code identity):
    // the stage ticks, the samples of each kind, the takes made while a group still owed a lattice and what ended each
    // take's batch, the groups waiting on a dispatch, in flight and on the workers, and the frame thread's waits on the
    // bake's gate, which show a worker holding it through array-sized work as frame time.
    private readonly SdfMeshBakeReport.Accounts m_accounts = new();

    // Takes the gate on the frame thread, counting the wait.
    private GateScope EnterFrameGate(SdfMeshBakeReport.Gate site) {
        var scope = EnterGate(site: site);

        m_accounts.FrameGateWaited(waited: scope.Waited);

        return scope;
    }
    // Takes the gate, timing the wait for it and the hold, which the done line reports by site. The one door to m_gate:
    // a section that publishes a group's step names where it holds the gate.
    private GateScope EnterGate(SdfMeshBakeReport.Gate site) {
        var asked = Stopwatch.GetTimestamp();
        var scope = m_gate.EnterScope();

        return new GateScope(owner: this, scope: scope, site: site, waited: (Stopwatch.GetTimestamp() - asked));
    }

    // A hold of the bake's gate, which counts its wait and hold into the bake's accounts as it leaves the gate.
    internal ref struct GateScope {
        private readonly SdfMeshPathBake m_owner;

        private SdfMeshBakeReport.Gate m_site;
        private long m_entered;
        private long m_nested;
        private Lock.Scope m_scope;

        public GateScope(SdfMeshPathBake owner, Lock.Scope scope, SdfMeshBakeReport.Gate site, long waited) {
            m_owner = owner;
            m_scope = scope;
            m_site = site;
            m_entered = Stopwatch.GetTimestamp();
            m_nested = owner.m_accounts.GateNestedTicks;
            Waited = waited;
            SdfMeshBakeReport.GateEntered();
        }

        // The Stopwatch ticks the entry waited for the gate.
        public long Waited { get; private set; }

        // Counts the hold so far under its site and the rest under another, still holding the gate: one entry that does
        // several things in turn reads each apart.
        public void Continue(SdfMeshBakeReport.Gate site) {
            var now = Stopwatch.GetTimestamp();

            m_owner.m_accounts.GateHeld(held: HeldAt(now: now), site: m_site, waited: Waited);
            (m_site, m_entered, m_nested, Waited) = (site, now, m_owner.m_accounts.GateNestedTicks, 0L);
        }
        public void Dispose() {
            m_owner.m_accounts.GateHeld(held: HeldAt(now: Stopwatch.GetTimestamp()), site: m_site, waited: Waited);
            m_scope.Dispose();
            SdfMeshBakeReport.GateLeft();
        }
        // The ticks held by `now`, less the sections counted under a site of their own meanwhile (a new bake's accounts
        // start at none, which leaves nothing to take out).
        private readonly long HeldAt(long now) => Math.Max(val1: 0L, val2: ((now - m_entered) - Math.Max(val1: 0L, val2: (m_owner.m_accounts.GateNestedTicks - m_nested))));
    }
    // Adds the time since a timestamp to a stage's account; the report names a stage that held one group's worker for
    // seconds, so the bake's long poles can be found.
    private void Account(BakeStage stage, long since, Group? group = null, SdfMeshBakeReport.Detail detail = default) {
        var ticks = m_accounts.Add(since: since, stage: stage);

        if (group is not null) {
            SdfMeshBakeReport.Slow(detail: detail, dynamic: group.Dynamic, instances: group.Instances, residency: Residency, selector: group.Selector, stage: stage, ticks: ticks);
        }
    }
    // The measure of every group still baking by its demand box, spread over SdfMeshTier.MeasureFrames frames
    // (Prioritize), and the epoch the groups' recorded holes are counted in (CountHoles).
    private GroupSweep m_prioritySweep;
    private int m_holeEpoch;

    // Measures a frame's share of the groups still owing a mesh by their demand boxes at the demand points, from the
    // group the last frame's share ended at, and every group when `whole`: a restart ranks the groups it plans before
    // its first take. With no demand point the priorities stand as they were and the plan's order decides. A priority
    // only orders work, never changes a mesh, so one up to a sweep old, or one a worker reads mid-measure, orders no mesh
    // differently. It runs on the frame's thread with the gate open: the frame's thread alone replaces the group list and
    // the copy index (Restart, Revisit, IndexCopies). Also marks each group in view (Group.InView: SdfMeshDemand.Sees,
    // which takes every box with no demand point) and keeps the count of the groups that owe the frame (OwesFrame), which
    // the bake's owed takes read (OwedGroups), and of those still bringing a first mesh (m_unshown), by the visits.
    private void Prioritize(bool whole = false) {
        // With every group done there is nothing to order: the done cursor makes this check constant once the bake ends.
        using (EnterGate(site: BakeGate.Prioritize)) {
            while ((m_doneThrough < m_groups.Count) && m_groups[m_doneThrough].Done) { m_doneThrough++; }
            if (m_doneThrough >= m_groups.Count) {
                m_holeEpoch++;
                Volatile.Write(location: ref m_owedGroups, value: 0);
                m_unshown = 0;
                return;
            }
        }

        var count = m_groups.Count;
        var measured = (Demand.Count > 0);
        var share = (whole ? long.MaxValue : m_prioritySweep.Share);
        var at = (whole ? 0 : m_prioritySweep.Start(count: count));
        var spent = 0L;

        // A whole measure counts the holes again from none: the groups' records are of an epoch that has passed.
        if (whole) {
            m_holeEpoch++;
            Volatile.Write(location: ref m_owedGroups, value: 0);
            m_unshown = 0;
        }
        for (var visited = 0; ((visited < count) && (spent < share)); visited++) {
            var boxes = Prioritized(group: m_groups[at], measured: measured);

            spent += boxes;
            at = m_prioritySweep.Visited(at: at, boxes: boxes, count: count);
        }
        if (!whole) { m_prioritySweep.Stop(at: at); }
    }
    // Measures one group, and returns the boxes it read, at least one. A group baking geometry other groups wait on ranks
    // as the most wanted of them, and is in view where any of them is: this bake's followers, then the waiters of any
    // bake holding the library, each of which publishes its own priority for its leader to read.
    private int Prioritized(Group group, bool measured) {
        var boxes = 1;

        if (!group.Done) {
            if (group.HasDemandBox) {
                boxes = Math.Max(val1: 1, val2: (CopiesOf(group: group)?.Count ?? 1));
                group.InView = MeasureSees(group: group);
                if (measured) { group.Priority = MeasureDemand(group, priority: true); }
            }
            if (group.Followers is { } followers) {
                // By index: the list only grows, and only on this thread, but a worker may hand it over (TakeFollowers).
                for (var at = 0; (at < followers.Count); at++) {
                    var follower = followers[at];

                    if (follower.Done || !ReferenceEquals(objA: follower.Leader, objB: group)) { continue; }
                    group.Priority = MathF.Max(x: group.Priority, y: follower.Priority);
                    group.InView |= follower.InView;
                }
            }
            if (measured) {
                if (group.Waiter is { } waiter) { waiter.Priority = group.Priority; }
                if ((group.Share is { HasWaiters: true } share)) { group.Priority = MathF.Max(x: group.Priority, y: Library.MostWanted(share: share)); }
            }
        }
        CountHoles(group: group, listed: true);

        return boxes;
    }
    // Moves the counts of the groups that owe the frame and of those still bringing a first mesh to what a group's state
    // is now, or to none for one leaving the list: the group records what it last added in the epoch it was counted in,
    // so a count never holds a contribution that nothing can take back. On the frame's thread.
    private void CountHoles(Group group, bool listed) {
        var before = ((group.HoleEpoch == m_holeEpoch) ? group.HoleBits : 0);
        var now = ((listed && OwesFrame(group: group)) ? 1 : 0) | ((listed && !group.Done && !group.Retired && group.HasDemandBox && (group.Shown is null) && (group.Priority > 1f)) ? 2 : 0);

        group.HoleBits = now;
        group.HoleEpoch = m_holeEpoch;
        if (now == before) { return; }
        Volatile.Write(location: ref m_owedGroups, value: (m_owedGroups + ((now & 1) - (before & 1))));
        m_unshown += ((now >> 1) - (before >> 1));
    }
    // The leaders done since Follow last ran that groups follow (Group.Done enqueues them), under the gate.
    private readonly List<Group> m_followable = [];

    // Finishes every group waiting on a group that has finished baking the same geometry (Group.Leader): it takes the
    // mesh the library now holds, renumbered for its own materials, or the leader's empty outcome; one whose mesh the
    // library no longer holds bakes it itself. It visits only the followers of the leaders done since it last ran, which
    // each leader lists as its followers name it. Runs under the gate.
    private void Follow() {
        for (var at = 0; (at < m_followable.Count); at++) {
            var leader = m_followable[at];

            foreach (var group in leader.TakeFollowers()) { FollowDone(group: group, leader: leader); }
        }
        m_followable.Clear();
    }
    // Finishes one follower of a leader that is done, unless it no longer waits on that leader.
    private void FollowDone(Group group, Group leader) {
        if (group.Done || !ReferenceEquals(objA: group.Leader, objB: leader)) { return; }
        group.Leader = null;
        if (leader.Mesh is null) {
            group.Outcome = leader.Outcome;
            group.Done = true;
            // A follower rebaking keeps drawing its previous mesh until the result lands; an empty one lands too.
            Draw(group: group, mesh: null);
            return;
        }
        // Renumbering the mesh for the follower's materials copies arrays as large as the mesh, so it runs on the
        // workers, outside the gate the frame takes.
        group.Contouring = true;
        ScheduleFollowed(group: group);
    }
    // The closure lives here so that Follow's visit of a follower allocates none unless it schedules.
    private void ScheduleFollowed(Group group) => Schedule(group: group, work: () => TakeLanded(group: group, share: null));
    // Queues the take of a waiter whose share resolved, on the pool, off the leader's gate; false for a waiter retired since.
    private bool ScheduleJoined(Group group, SdfMeshBakeShare share) => Schedule(group: group, work: () => TakeLanded(group: group, share: share));
    // Takes the mesh another group of the group's geometry landed, renumbered for the group's materials, or the empty
    // result it found: a follower's leader in this bake (no share), or the leader of the share the group waits on. A group
    // whose leader left nothing in the library (it retired or failed, or the library let its mesh go) starts its geometry
    // again (Start), waiting on another group that started it meanwhile or leading it itself. On the pool.
    private void TakeLanded(Group group, SdfMeshBakeShare? share) {
        var taken = Library.TryTake(geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: out var mesh, refused: out var refused);

        // A take that renumbers the leader's mesh copies its materials into the library under the account; one it has no
        // room for waits parked rather than baking the geometry again (Park).
        if (refused is not null) {
            Park(bytes: refused.Bytes, group: group, refused: refused, work: () => TakeLanded(group: group, share: share));
            return;
        }

        using (EnterGate(site: BakeGate.Taken)) {
            // A waiter that already took, deserted or retired is no longer this share's to finish.
            if (!GroupList.Holds(group: group) || ((share is not null) && (!ReferenceEquals(objA: group.Joined, objB: share) || group.Done || group.Retired))) { mesh?.Release(); return; }
            group.Contouring = false;
            group.Joined = null;
            group.Waiter = null;

            var empty = (!taken && Library.HoldsEmpty(geometry: group.Sealed, identity: group.Identity));

            if (taken || empty) {
                if (empty) { group.FoundNoSurface(how: ", as another group baked it"); }
                group.Done = true;
                group.Stored = taken;
                Draw(group: group, mesh: mesh);
                if (share is not null) {
                    Library.Shares.Count(kind: SdfMeshBakeShare.Landed);
                    m_accounts.JoinedLanded++;
                    if (taken) {
                        Library.Shares.Add(amount: share.Samples, kind: SdfMeshBakeShare.SamplesSaved);
                        m_accounts.SamplesSaved += share.Samples;
                    }
                }
                Finish();
                return;
            }
            if (share is not null) {
                Library.Shares.Count(kind: SdfMeshBakeShare.Abandoned);
                m_accounts.JoinedAbandoned++;
            }
            CountStarted(how: Start(group: group));
        }
    }
    // How a group the library held no mesh of began (Start): a bake, a load from the store, or a wait on another group's
    // bake of its geometry through the library (SdfMeshLibrary.Share).
    private enum Started : byte { Baking, Loading, Joined }

    // The groups waiting on a share another group leads (Group.Joined), which DesertStalled watches; an entry no longer
    // waiting is dropped there. Under the gate.
    private readonly List<Group> m_joined = [];
    // The takes asked of the bake with its program bound (TryTake), which a waiter on its share reads to tell a bake its
    // residency's frames drive from one no frame does.
    private long m_asks;

    // The takes this bake's waiter sees asked of itself, and the seconds that span at least, while its leader's bake is
    // asked none, before it deserts that leader: a few frames' pumps, past a hitch that holds every bake alike.
    private const int StallAsks = 64;
    private const double StallSeconds = 1.0;

    internal long Asks => Interlocked.Read(location: ref m_asks);

    // Begins a group the library holds no mesh of: it waits on the bake of its geometry another group of any bake holding
    // the library already runs, or begins that bake itself (Begin) and names it in the library for later groups to wait
    // on. The one door a group starts its geometry by. Under the gate.
    private Started Start(Group group) {
        group.Identity = Identity.Value;

        var share = Library.Share(bake: this, geometry: group.Sealed, group: group, identity: group.Identity, landed: out var landed, waiter: out var waiter);

        if (waiter is null) {
            group.Share = share;

            var began = Stopwatch.GetTimestamp();
            var loading = Begin(group: group);

            m_accounts.GateNested(held: (Stopwatch.GetTimestamp() - began), site: BakeGate.Begin);

            return (loading ? Started.Loading : Started.Baking);
        }
        group.Joined = share;
        group.Waiter = waiter;
        waiter.Priority = group.Priority;
        group.LeaderAsks = share.Bake.Asks;
        group.StillAsks = Asks;
        group.StillSince = Stopwatch.GetTimestamp();
        m_joined.Add(item: group);
        Library.Shares.Count(kind: SdfMeshBakeShare.Joined);
        // A share that landed between the group's library miss and its start has no leader to hand it back.
        if (landed) { _ = ScheduleJoined(group: group, share: share); }
        return Started.Joined;
    }
    // Counts a start outside a restart's, resample's or promotion's own counts (PlaceCounts) into the bake's done line.
    private void CountStarted(Started how) {
        if (how == Started.Joined) { m_accounts.Joined++; }
    }
    // Starts its geometry itself each waiter whose leader's bake has been asked no take while this bake was asked
    // StallAsks takes over StallSeconds: the leader's residency renders no view, so its background work never binds and
    // its bake never lands, and a waiter never waits on a bake no frame drives. Deserting closes the share to later
    // groups, which then wait on the deserter. Under the gate, on the frame's thread, from a take this bake was asked.
    private void DesertStalled() {
        var asks = Asks;
        var now = Stopwatch.GetTimestamp();

        for (var at = (m_joined.Count - 1); (at >= 0); at--) {
            var group = m_joined[at];

            if ((group.Joined is not { } share) || (group.Waiter is not { } waiter) || group.Done || group.Retired) {
                m_joined.RemoveAt(index: at);
                continue;
            }
            var leaderAsks = share.Bake.Asks;

            if (leaderAsks != group.LeaderAsks) {
                group.LeaderAsks = leaderAsks;
                group.StillAsks = asks;
                group.StillSince = now;
                continue;
            }
            if (((asks - group.StillAsks) < StallAsks) || (Stopwatch.GetElapsedTime(startingTimestamp: group.StillSince, endingTimestamp: now).TotalSeconds < StallSeconds)) { continue; }
            m_joined.RemoveAt(index: at);
            Library.Leave(desert: true, share: share, waiter: waiter);
            group.Joined = null;
            group.Waiter = null;
            Library.Shares.Count(kind: SdfMeshBakeShare.Stalled);
            m_accounts.JoinedStalled++;
            CountStarted(how: Start(group: group));
        }
    }
    // Queues a group's work off the frame's thread, taken by priority (TakeWork) or, urgent, before any other in the
    // order it came, and starts a worker for it if one is free. Work that takes the bake's gate waits while a caller
    // holds it, as a restart does.
    private bool Schedule(Group group, Action work) {
        if (!group.QueueWork()) { return false; }
        // The first stage queued after a group's admission ends its lead.
        if (group.TakeAdmittedAt() is var admittedAt and not 0L) { PoolBacklog.ObserveLead(seconds: Stopwatch.GetElapsedTime(startingTimestamp: admittedAt).TotalSeconds); }
        Enqueue(group: group, urgent: false, work: work);
        return true;
    }

    // Advances Progress and wakes its waiters: a pool worker finishing a work item does, and so does a batch's reader
    // (SdfMeshBakeGpuWork) once it has answered the batch, since a field slice's answer queues no work item.
    internal static void Progressed() {
        lock (ProgressGate) {
            ProgressCount++;
            Monitor.PulseAll(obj: ProgressGate);
        }
    }

    // Queues work for which the group's job count is already taken (Group.QueueWork). A stage the host memory account
    // refused starts its group's final lattice again under a larger admission (Readmit); any other stage that throws
    // finishes its group with no new mesh (Fail), so its reservation and workspace are released and the bake still
    // settles. An urgent answer runs even once its group retires, since it holds its answered samples and lets them go.
    private void Enqueue(Group group, Action work, bool urgent) {
        void Run() {
            try {
                if (urgent || !group.Retired) { work(); }
            } catch (Exception exception) {
                if (HostMemoryRefusedException.Of(exception: exception) is { } refused) {
                    Readmit(group: group, refused: refused);
                } else {
                    Fail(exception: exception, group: group);
                }
            } finally {
                group.EndWork();
                Offer();
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
        SdfMeshBakeReport.Failed(
            exception: exception,
            geometry: group.Sealed,
            high: group.High,
            lattice: ((group.Final is { } final) ? (final.Lattice, FactorOf(final: final)) : null),
            low: group.Low,
            refinements: group.Refinements,
            residency: Residency,
            selector: group.Selector
        );
        using (EnterGate(site: BakeGate.Fail)) {
            group.Contouring = false;
            group.InFlight = false;
            if (group.Done || !GroupList.Holds(group: group)) { return; }
            group.Failed = true;
            group.Outcome = $"failed: {exception.GetType().Name}";
            group.Next = null;
            group.Done = true;
            Finish();
        }
    }
    // The pool's queued stages, every residency's.
    private static int QueuedStages() {
        lock (WorkGate) { return WorkQueue.Count; }
    }
    // Whether the pool's stages, those queued and those of groups admitted and still on their way (awaiting), keep every
    // worker busy for the lead a newly admitted group takes before its first stage (SdfMeshBakeBacklog): a group
    // admitted beyond that would only hold its lattice in the queue. Both sides are wall time, so the answer is the
    // same whenever the take asks, on a fast frame or a slow one.
    private static bool Backlogged(int queued, int awaiting) => PoolBacklog.Holds(stages: (queued + awaiting), workers: Workers);
    // Wakes idle workers for the queued work no woken worker will take, starting the workers on the first call. Called
    // only with WorkGate held, after work is queued.
    private static void StartWorkers() {
        if (WorkerThreads is null) {
            WorkerThreads = new Thread[Workers];
            for (var index = 0; (index < WorkerThreads.Length); index++) {
                WorkerThreads[index] = new Thread(start: Work) {
                    IsBackground = true,
                    Name = $"Puck mesh bake worker {index}",
                };
                WorkerThreads[index].Start();
            }
        }

        var wakes = (Math.Min(val1: IdleWorkers, val2: (WorkQueue.Count + AnswerQueue.Count)) - PendingWakes);

        if (wakes <= 0) { return; }
        PendingWakes += wakes;
        _ = WorkWake.Release(releaseCount: wakes);
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
        using (EnterGate(site: BakeGate.Read)) {
            foreach (var group in m_groups.Concat(second: m_retiredGroups)) {
                group.Retired = true;
                group.InFlight = false;
            }
            foreach (var (_, _, room, _) in m_parked) { room.Dispose(); }
            m_parked.Clear();
        }
        lock (WorkGate) { _ = WorkQueue.RemoveAll(match: item => { if (!item.Group.Retired) { return false; } item.Group.EndWork(); return true; }); }
        RetireReadback();
    }

    // A worker: runs the queued work, answers first, then the highest priority, and waits for more once none is queued.
    private static void Work() {
        while (true) {
            if (TakeWork(stage: out var stage) is not { } work) {
                // Idle only with nothing queued, under the gate the queuing side wakes idle workers under, so no work
                // queued meanwhile goes unwoken.
                lock (WorkGate) {
                    if ((WorkQueue.Count + AnswerQueue.Count) > 0) { continue; }
                    IdleWorkers++;
                }
                _ = WorkWake.WaitOne();
                lock (WorkGate) {
                    IdleWorkers--;
                    PendingWakes--;
                }
                continue;
            }

            var began = Stopwatch.GetTimestamp();

            // A work item that throws has already settled what it owns (a group's stage fails its group; an answer
            // returns its batch's unanswered lattices), so the line names the fault, out of memory included, and the
            // worker carries on rather than ending silently.
            try {
                work();
            } catch (Exception exception) {
                SdfMeshBakeReport.WorkFailed(exception: exception);
            } finally {
                if (stage) { PoolBacklog.ObserveStage(seconds: Stopwatch.GetElapsedTime(startingTimestamp: began).TotalSeconds); }
                Progressed();
            }
        }
    }
    // Takes the earliest queued answer, else the queued work whose group ranks highest (RankOf: in view first, then by
    // priority), the earliest queued among equals, or null when none is queued; it says whether the work is a queued
    // stage, which the backlog times. A group's rank is read as the frame thread last left it.
    private static Action? TakeWork(out bool stage) {
        stage = false;
        lock (WorkGate) {
            if (AnswerQueue.TryDequeue(result: out var answer)) {
                if ((AnswerQueue.Count + WorkQueue.Count) > 0) { SdfMeshBakeReport.PoolSaturated(); }
                return answer;
            }

            var best = -1;

            // Work of a group its bake no longer plans (a program replaced it, or its residency was released) is dropped
            // before it runs.
            _ = WorkQueue.RemoveAll(match: static item => { if (!item.Group.Retired) { return false; } item.Group.EndWork(); return true; });
            for (var index = 0; (index < WorkQueue.Count); index++) {
                if ((best < 0) || (RankOf(group: WorkQueue[index].Group) > RankOf(group: WorkQueue[best].Group))) { best = index; }
            }
            if (best < 0) { return null; }

            var work = WorkQueue[best].Work;

            WorkQueue.RemoveAt(index: best);
            // Work still queued behind this take means every worker is busy: the tail the done line reports starts
            // after the last such take.
            if (WorkQueue.Count > 0) { SdfMeshBakeReport.PoolSaturated(); }
            stage = true;
            return work;
        }
    }
    // A group's demand box in world space: a dynamic group's carried out of its slot's frame by the slot's transform, as
    // the world-axis box about the turned one.
    private (Vector3 Center, Vector3 Half) WorldBox(Group group) {
        if (!group.Dynamic || (group.Slot >= m_demandTransforms.Count)) { return (group.DemandCenter, group.DemandHalf); }
        var box = SdfMeshBox.Carried(center: group.DemandCenter, half: group.DemandHalf, transform: m_demandTransforms[group.Slot]);

        return (box.Center, box.AxisHalf);
    }
    private static (Vector3 Center, Vector3 Half) TransformDemandBox(Group group, Matrix4x4 matrix) => TransformBox(center: group.DemandCenter, half: group.DemandHalf, matrix: matrix);
    // The world-axis box about a box carried through a matrix.
    private static (Vector3 Center, Vector3 Half) TransformBox(Vector3 center, Vector3 half, Matrix4x4 matrix) {
        return (Vector3.Transform(matrix: matrix, position: center), VectorFunctions.OrientedHalfExtent(halfExtent: half, matrix: in matrix));
    }
    // A shared geometry is wanted where its actual occurrences stand. Never union distant occurrence boxes:
    // the empty space between them is neither visible demand nor a nearby surface.
    // Whether some view may see a group: one of a shared geometry's occurrences, or its box (SdfMeshDemand.Sees).
    private bool MeasureSees(Group group) {
        if (CopiesOf(group: group) is { } copies) { return CopiesSeen(center: group.DemandCenter, copies: copies, half: group.DemandHalf); }

        var (center, half) = WorldBox(group: group);

        return Demand.Sees(center: center, half: half);
    }
    private float MeasureDemand(Group group, bool priority) {
        if (CopiesOf(group: group) is not { } copies) {
            var (center, half) = WorldBox(group: group);
            return (priority ? Demand.Priority(center: center, half: half) : Demand.DistanceFrom(center: center, half: half, index: 0));
        }
        var result = (priority ? 0f : float.PositiveInfinity);

        foreach (var index in copies) {
            var (center, half) = TransformDemandBox(group: group, matrix: m_demandCopies[index].Matrix);
            var value = (priority ? Demand.Priority(center: center, half: half) : Demand.DistanceFrom(center: center, half: half, index: 0));

            result = (priority ? MathF.Max(x: result, y: value) : MathF.Min(x: result, y: value));
        }
        return result;
    }

    /// <summary>Stamps, on the frame's thread after the frame's mesh region is staged, each group the bake has finished and
    /// each whose mesh's words the region holds, which <see cref="DescribeResidency"/> times from the bake's start,
    /// reads what the frame's draws ask of the region and the meshes it refuses, which the next frame's tiers are
    /// budgeted by (the region's limit reaches the budget through the grant, <see cref="SdfResidencyGrant.Budget"/>),
    /// names the tables the products that wait ahead of the draws (<see cref="SdfWorldTables.NameMeshesAhead"/>), and
    /// hands the draws to each whose words stand in the region (<see cref="SdfWorldTables.MeshStands"/>).</summary>
    /// <param name="tables">The residency's tables, whose mesh region owes a mesh's words or not
    /// (<see cref="SdfWorldTables.OwesMesh"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="tables"/> is <see langword="null"/>.</exception>
    public void ObserveResidency(SdfWorldTables tables) {
        ArgumentNullException.ThrowIfNull(argument: tables);

        // A mutable local, not a using variable, which reads each part of the hold under its own site (Continue).
        var scope = EnterGate(site: BakeGate.Observe);

        try {
            // The grant is the tables', read once: its budget and bias are the selector's from then on (Retier).
            if (!ReferenceEquals(objA: m_detail, objB: tables.Grant)) {
                m_detail = tables.Grant;
                Volatile.Write(location: ref m_bias, value: m_detail.Bias);
            }
            // The region's account is read every frame: the next frame's tiers are budgeted by it (Retier).
            m_regionNamedBytes = ((long)tables.MeshRegionNamedBytes);
            m_regionRefused = tables.MeshRegionRefused;
            tables.CoverRefusedBacking();
            ServeAhead(tables: tables);
            if (m_regionRefused > 0) {
                scope.Continue(site: BakeGate.Evict);
                CollectEvictions(tables: tables);
                scope.Continue(site: BakeGate.Observe);
            }
            if (m_allResident) {
                m_holes = 0;
                return;
            }
            // The groups that landed or were drawn again since the last frame join the set walked here.
            while (m_landed.TryDequeue(result: out var landed)) { m_unresident.Add(item: landed); }

            var now = Stopwatch.GetTimestamp();
            var dressed = (m_dressedRevision == m_productRevision);
            var holes = m_unshown;

            for (var at = (m_unresident.Count - 1); (at >= 0); at--) {
                var group = m_unresident[at];

                if (!group.Listed || (group.ResidentAt != 0)) {
                    DropUnresident(at: at);
                    continue;
                }
                if (group.LandedAt == 0) { group.LandedAt = now; }
                // A landed group in reach whose drawn mesh's words the region owes is a hole, as is one still bringing its
                // first mesh (m_unshown).
                if (!group.Retired && group.HasDemandBox && (group.Shown is { } shown) && AnyPiece(mesh: shown, tables: tables, test: static (region, piece) => region.OwesMesh(mesh: piece)) && (MeasureDemand(group, priority: true) > 1f)) { holes++; }
                // A mesh is resident once the frame's draws name it and the region owes none of its words; one the
                // current draws do not name (a template no copy draws) costs the region nothing and counts too.
                if ((group.Mesh is { } mesh) && (!dressed || AnyPiece(mesh: mesh, tables: tables, test: static (region, piece) => region.OwesMesh(mesh: piece)))) { continue; }
                group.ResidentAt = now;
                DropUnresident(at: at);
            }
            m_holes = holes;
            if (holes > 0) { m_holeFrames++; }
            m_allResident = ((m_unresident.Count == 0) && (DoneThrough() >= m_groups.Count));
        } finally {
            scope.Dispose();
        }
    }
    // Takes a group out of the set ObserveResidency walks, which a later change to it hands back through
    // Group.AwaitResidency. Under the gate, as every change that hands a group over is (Listed, ResidentAt, Draw).
    private void DropUnresident(int at) {
        var group = m_unresident[at];
        var last = (m_unresident.Count - 1);

        m_unresident[at] = m_unresident[last];
        m_unresident.RemoveAt(index: last);
        group.StoodOrLeft();
    }

    // Names the tables the products that wait ahead of the draws, then hands the draws to those whose every piece
    // stands in the mesh region: the one place a product that replaces a drawn mesh takes the draws (Group.ShowOwn).
    // The standing products take the draws together, once none is still landing or one has waited
    // SdfMeshTier.HandFrames: each hand-over moves the draws every view renders from, so a wave moves them once.
    // A product handed its draws stays named ahead until the next frame's draws name it, so the region keeps its
    // words between the two. A piece the region had no room for beside the mesh it replaces (RefusesAhead) cannot
    // land until that mesh gives its words up, so its product takes the draws once its other pieces stand and the
    // piece waits out of the frame as a first mesh does. Under the gate, on the frame's thread.
    // The floors each group keeps while a finer tier draws (Group.KeptFloor) are named ahead with them, so their words
    // stay where a fallen-back draw reads them; the set is named again when a kept floor or the group list changes
    // (m_floorRevision).
    private void ServeAhead(SdfWorldTables tables) {
        var floorsMoved = (m_floorsNamed != m_floorRevision);

        if ((m_ahead.Count == 0) && !floorsMoved && (m_aheadNamed == m_aheadRevision) && ReferenceEquals(objA: tables, objB: m_aheadTables)) { return; }
        for (var at = (m_ahead.Count - 1); (at >= 0); at--) {
            var group = m_ahead[at];

            // A group retired, or whose product was replaced by none, waits on nothing.
            if (group.Retired || !group.Ahead || (group.Mesh is null)) {
                m_ahead.RemoveAt(index: at);
                m_aheadRevision++;
            }
        }
        if ((m_aheadNamed != m_aheadRevision) || floorsMoved || !ReferenceEquals(objA: tables, objB: m_aheadTables)) {
            m_aheadMeshes.Clear();
            foreach (var group in m_ahead) {
                var mesh = group.Mesh!;

                for (var piece = 0; (piece < mesh.PieceCount); piece++) { m_aheadMeshes.Add(item: mesh.Piece(index: piece)); }
            }
            // Only the groups that keep a floor hold one, and the walk drops the retired ones from that list, in order.
            var kept = 0;

            for (var at = 0; (at < m_keepers.Count); at++) {
                var group = m_keepers[at];

                if (group.Retired || (group.KeptFloor is not { } floor)) {
                    group.Keeping = false;
                    continue;
                }
                m_keepers[kept++] = group;
                for (var piece = 0; (piece < floor.PieceCount); piece++) { m_aheadMeshes.Add(item: floor.Piece(index: piece)); }
            }
            m_keepers.RemoveRange(index: kept, count: (m_keepers.Count - kept));
            tables.NameMeshesAhead(meshes: m_aheadMeshes);
            m_aheadNamed = m_aheadRevision;
            m_floorsNamed = m_floorRevision;
            m_aheadTables = tables;
        }
        var landing = false;
        var waited = 0;

        m_aheadStates.Clear();
        foreach (var group in m_ahead) {
            var mesh = group.Mesh!;
            var state = AheadState.Stands;

            for (var piece = 0; ((state != AheadState.Landing) && (piece < mesh.PieceCount)); piece++) {
                var part = mesh.Piece(index: piece);

                if (tables.MeshStands(mesh: part)) { continue; }
                state = (tables.RefusesAhead(mesh: part) ? AheadState.StandsRefused : AheadState.Landing);
            }
            group.AheadFrames++;
            if (state == AheadState.Landing) { landing = true; } else { waited = Math.Max(val1: waited, val2: group.AheadFrames); }
            m_aheadStates.Add(item: state);
        }
        if (landing && (waited < SdfMeshTier.HandFrames)) { return; }
        for (var at = (m_ahead.Count - 1); (at >= 0); at--) {
            var state = m_aheadStates[at];

            if (state == AheadState.Landing) { continue; }
            m_ahead[at].ShowOwn();
            m_ahead.RemoveAt(index: at);
            m_aheadRevision++;
            m_handed++;
            if (state == AheadState.StandsRefused) { m_handedRefused++; }
        }
    }

    /// <summary>Describes where the resident triangles' detail is asked from: one line a demand point that sees some
    /// group finest (its eye, its pixels a metre at one metre, the groups and triangles whose tier it sets and the
    /// finest tier among them), then the groups holding the most triangles, each with its tier, the rungs that change
    /// its mesh, the pixel it is seen at and the point that sees it so.</summary>
    /// <param name="largest">How many of the largest groups to name.</param>
    /// <returns>The description, one part a line.</returns>
    public IReadOnlyList<string> DescribeDemand(int largest = 12) {
        using (EnterGate(site: BakeGate.Read)) {
            var count = Demand.Count;
            var groups = new int[(count + 1)];
            var triangles = new long[(count + 1)];
            var finest = new int[(count + 1)];
            var top = new List<(long Triangles, string Line)>();

            Array.Fill(array: finest, value: SdfMeshTier.Last);
            foreach (var group in m_groups) {
                if (group.Retired || (group.Mesh is not { } mesh)) { continue; }

                var point = -1;
                var pixel = (group.HasDemandBox ? MeasurePixel(group: group, point: out point) : float.PositiveInfinity);
                var slot = ((point < 0) ? count : point);
                var tier = (group.Replaces ?? group).Tier;

                groups[slot]++;
                triangles[slot] += mesh.TriangleCount;
                finest[slot] = Math.Min(val1: finest[slot], val2: tier);
                top.Add(item: (mesh.TriangleCount, string.Create(provider: CultureInfo.InvariantCulture, handler: $"demand-group={group.Selector}{(group.Dynamic ? "/dynamic" : ((group.Instances > 1) ? $"/union{group.Instances}" : "/static"))} triangles={mesh.TriangleCount} tier={tier}@{(1000f * SdfMeshTier.Cell(tier: tier)):0.###}mm rungs={group.FinestTier}..{group.CoarsestTier} pixel={(1000f * pixel):0.###}mm point={point} box={(group.High - group.Low).X:0.##}x{(group.High - group.Low).Y:0.##}x{(group.High - group.Low).Z:0.##}")));
            }

            var lines = new List<string>();

            for (var index = 0; (index <= count); index++) {
                if (groups[index] == 0) { continue; }
                if (index == count) {
                    lines.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"demand-point=none groups={groups[index]} triangles={triangles[index]}"));
                    continue;
                }

                var eye = Demand.Point(index: index);

                lines.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"demand-point={index} eye={eye.X:0.##},{eye.Y:0.##},{eye.Z:0.##} pixels-a-metre={Demand.PixelsPerMetre(index: index):0.#} within-view={(Demand.WithinView(index: index) ? 1 : 0)} groups={groups[index]} triangles={triangles[index]} finest-tier={finest[index]}"));
            }
            top.Sort(comparison: static (left, right) => right.Triangles.CompareTo(value: left.Triangles));
            for (var index = 0; ((index < top.Count) && (index < largest)); index++) { lines.Add(item: top[index].Line); }

            return lines;
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
        using (EnterGate(site: BakeGate.Read)) {
            var start = m_bakeStarted;
            var groups = m_groups.Count;
            int landed = 0, resident = 0, near = 0, nearLanded = 0, nearResident = 0;
            long lastLanded = 0, lastResident = 0, nearLastLanded = 0, nearLastResident = 0;
            var vertexBytes = 0L;
            Span<long> levelBytes = stackalloc long[3];
            Span<long> levelTriangles = stackalloc long[3];
            Span<long> tierTriangles = stackalloc long[(SdfMeshTier.Last + 1)];
            var nearStamps = new List<long>();
            Group? lastNear = null;

            foreach (var group in m_groups) {
                var isNear = (group.HasDemandBox && (Demand.Count > 0) && (MeasureDemand(group, priority: false) <= radius));

                near += (isNear ? 1 : 0);
                if (group.Mesh is { } tiered) { tierTriangles[(group.Replaces ?? group).Tier] += tiered.TriangleCount; }
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
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" frames={m_preparedFrames} restarts={m_restarts} restart-groups={m_restartGroups} resamples={m_resamples} resample-groups={m_resampleGroups} input-readers={m_inputReaders.Count} operand-readers={m_operandReaders} operand-replans={m_operandReplans} coalesced={m_coalesced} promoted={m_promoted} successors={m_successors} ahead={m_ahead.Count} handed={m_handed} handed-refused={m_handedRefused}");
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" vertex-bytes={vertexBytes} level0={levelTriangles[0]}tri/{levelBytes[0]}B level1={levelTriangles[1]}tri/{levelBytes[1]}B level2={levelTriangles[2]}tri/{levelBytes[2]}B");
            // The detail budget's debt (SdfMeshTier): the groups drawing a coarser tier than their demand asks, the worst
            // such group's cell in display pixels and the longest wait, now and since the bake started, the finer tiers
            // landed, and the resident triangles by the tier they were baked at.
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" tiers-owed={m_tiersOwed} owed-error={m_owedError:0.00}px owed-frames={m_owedFrames} worst-owed-error={m_worstOwedError:0.00}px worst-owed-frames={m_worstOwedFrames} retiers={m_retiers} retiers-reused={m_retiersReused}");
            // The residency's budget from its presentation's selector (Retier): the bytes the frame's draws name
            // against it, the meshes the region refuses, the groups drawing a finer tier than their demand asks, the
            // finer tiers the budget holds back, the coarser tiers landed to make room, and the half rungs every demand
            // is coarsened by, the selector's one bias, with its steps up and down.
            // The demand watch (SdfMeshPathBake.Demand.cs): the groups the tier decisions read, those waiting on the
            // demand points' travel, the boxes a whole reset sweep reads and the groups such a sweep still owes.
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" demand-attended={m_attention.Count} demand-due={m_demandDue.Count} demand-sweep-boxes={m_demandSweep.Boxes} demand-sweep-left={m_demandSweepLeft}");
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" region-budget={m_regionBudgetBytes}B region-named={m_regionNamedBytes}B region-refused={m_regionRefused} tiers-spare={m_tiersSpare} tiers-held-back={m_tiersHeldBack} coarsened={m_coarsened} detail-bias={m_bias} bias-raised={(m_detail?.Selector.BiasSteps.Raised ?? 0)} bias-lowered={(m_detail?.Selector.BiasSteps.Lowered ?? 0)}");
            for (var tier = 0; (tier < tierTriangles.Length); tier++) {
                if (tierTriangles[tier] > 0) { _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" tier{tier}={tierTriangles[tier]}tri@{(1000f * SdfMeshTier.Cell(tier: tier)):0.###}mm"); }
            }

            return line.ToString();
        }
    }
}
