namespace Puck.SdfVm;

// What the bake does about a rung of a group's tier ladder its library's store lacks (SdfMeshCachePolicy).
//
// Invariant, under SdfMeshCachePolicy.Stored: a group starts a lattice only when the store holds no rung of its shape.
// Two doors lead a group to a lattice, and each asks the store first: Place, for a group arriving with a plan
// (StoredRung puts the nearest stored rung in its place), and StartReplacement, for a group drawing one tier and
// asked another (it keeps what it draws). Begin and Load count every group that reaches a lattice past them
// (SdfMeshCache.GroupsBaked), so a path that got around the doors shows in the count rather than passing unseen.
// Live geometry (Group.Live: a group that reads an operand, or planned again as a sampled field it reads moved) is outside
// the invariant, since the world makes it as it runs: it bakes under every policy and is counted apart.
//
// Invariant, under SdfMeshCachePolicy.Ladder: the bake's detail is not settled (DetailSettled) while any geometry a
// listed group draws has a rung between its finest and coarsest tiers that the store lacks. FillLadders is the one
// place a ladder advances, and Retier, the one writer of the settle count, asks it at every decision.
public sealed partial class SdfMeshPathBake {
    // The far distance of the latest prepared frame, which a rung planned outside a frame's own call is clamped by.
    private float m_farDistance;

    // How far a ladder bake has filled the other rungs of one geometry its groups draw: the next tier to look at,
    // counting down from the coarsest, and the rung in flight. Every group drawing the geometry shares the entry, so a
    // geometry a thousand copies draw is walked once.
    private sealed class Ladder {
        public int Next;
        public Group? Filling;
    }

    private readonly Dictionary<SdfMeshGeometry, Ladder> m_ladders = [];
    // The identity the ladders were walked under: another identity's store holds none of their rungs.
    private UInt128 m_laddersIdentity;
    private long m_fillsStarted;
    // Whether the latest tier decision left a ladder unfinished, and when that decision was made.
    private int m_filling;
    private long m_decidedAt;

    /// <summary>The longest a bake's frames may pause before its unfinished ladders stop counting as being filled
    /// (<see cref="Filling"/>): a residency no view draws decides no tier and fills nothing.</summary>
    public static TimeSpan FillingPatience { get; } = TimeSpan.FromSeconds(seconds: 3);

    /// <summary>Gets whether a <see cref="SdfMeshCachePolicy.Ladder"/> bake is still filling the ladders of the
    /// geometries it draws: its latest tier decision left one unfinished, and its frames still come
    /// (<see cref="FillingPatience"/>). A wait on the bakes of a residency besides the world's own holds on it, since
    /// such a bake is between rungs, not done, whenever no rung happens to be in flight. A residency whose views stopped
    /// drawing reads <see langword="false"/> with ladders unfinished, so nothing waits on frames that never come. Read
    /// from any thread.</summary>
    public bool Filling => ((Volatile.Read(location: ref m_filling) != 0) && (System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: Volatile.Read(location: ref m_decidedAt)) < FillingPatience));

    // The takes asked of the bake as Driven last read them, and when they last moved. Read and written by Driven alone.
    private long m_drivenAsks;
    private long m_drivenAt;

    /// <summary>The longest the bake may go unasked for a lattice and still count as driven
    /// (<see cref="Driven"/>).</summary>
    public static TimeSpan DrivenPatience { get; } = TimeSpan.FromSeconds(seconds: 1);

    /// <summary>Gets whether the bake's residency is being drawn: its background GPU work asked the bake for a lattice
    /// within <see cref="DrivenPatience"/>, as it does every frame a view renders the residency. A bake that is not
    /// driven lands nothing, whatever it owes, so a wait on the bakes of a residency besides the world's own holds on
    /// one only while this is true. Read by one thread at a time (the host's pump).</summary>
    public bool Driven {
        get {
            var asks = Asks;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (asks != m_drivenAsks) {
                m_drivenAsks = asks;
                m_drivenAt = now;
            }
            return ((m_drivenAt != 0L) && (System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: m_drivenAt, endingTimestamp: now) < DrivenPatience));
        }
    }

    // The groups whose load the account had no room for, each with the time its next read is due (Reload). Under the gate.
    private readonly List<(Group Group, long Due)> m_reloads = [];

    // The longest a declined load waits before it is read again, in seconds; the wait doubles from one second.
    private const int MostReloadSeconds = 32;

    // Holds a group whose stored entry the account had no room to read, to read it again after a wait that doubles each
    // time in a row, so a short account costs a read a group every half minute at most rather than a worker spinning on
    // it. The group stays held meanwhile, offering no lattice. On the pool.
    private void Reload(Group group) {
        using (EnterGate(site: SdfMeshBakeReport.Gate.Load)) {
            if (group.Retired || !GroupList.Holds(group: group)) { return; }

            var seconds = Math.Min(val1: MostReloadSeconds, val2: (1 << Math.Min(val1: group.LoadRetries, val2: 5)));

            group.LoadRetries++;
            m_reloads.Add(item: (group, (System.Diagnostics.Stopwatch.GetTimestamp() + (seconds * System.Diagnostics.Stopwatch.Frequency))));
        }
    }
    // Ends a tier's replacement whose stored mesh the account had no room to read: it is done with nothing of its own,
    // still drawing the mesh of the group it replaces, and FinishReplacements gives that group its place back, not to
    // be asked that tier again until its demand names another. False for a group that replaces none. On the pool.
    private bool Decline(Group group) {
        using (EnterGate(site: SdfMeshBakeReport.Gate.Load)) {
            if (group.Retired || !GroupList.Holds(group: group) || (group.Replaces is null) || group.Fill) { return false; }
            group.Declined = true;
            group.Contouring = false;
            m_loadsDone++;
            group.Done = true;
            Finish();
            return true;
        }
    }
    // Gives a replacement's place back to the group it stood in for, which drew throughout, and lets the replacement
    // go with the claims it took. Under the gate, on the frame's thread.
    private void GiveBack(Group replacement, Group replaced, int index) {
        Replace(group: replaced, index: index);
        // Cleared first: a retired group retires the group it replaces.
        replacement.Replaces = null;
        m_retiredGroups.Add(item: replacement);
        replacement.Retired = true;
    }
    // Queues again each held load whose wait has passed. Under the gate, on the frame's thread.
    private void ResumeReloads() {
        if (m_reloads.Count == 0) { return; }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        for (var at = (m_reloads.Count - 1); (at >= 0); at--) {
            var (group, due) = m_reloads[at];

            if (!group.Retired && GroupList.Holds(group: group) && (now < due)) { continue; }
            m_reloads.RemoveAt(index: at);
            if (!group.Retired && GroupList.Holds(group: group)) { ScheduleLoad(group: group); }
        }
    }
    // Whether the library or its store holds a planned group's mesh: of its geometry, or for a dynamic group of its
    // form, baked at another pose.
    private bool Stores(Group group, UInt128 identity) => (Library.Holds(geometry: group.Sealed, identity: identity) || (group.Dynamic && Library.HoldsForm(form: group.Form, identity: identity)));
    // Whether the store holds a mesh of a dynamic group's form under the group's identity, which loads in place of its
    // own geometry's (Begin).
    private bool HoldsForm(Group group) => (group.Dynamic && (Library.Disk?.Holds(geometry: SdfMeshLibrary.FormGeometry(form: group.Form), identity: group.Identity) ?? false));

    // A planned group planned again at another tier, as Plan would plan it at the latest prepared frame; null when
    // nothing rides its slot any more.
    private Group? RungOf(Group group, int tier) {
        if (m_program is not { } program) { return null; }

        var plans = new List<Group> { group };

        _ = SdfMeshBakeGroup.Refresh(
            create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
            farDistance: m_farDistance,
            groups: plans,
            program: program,
            sampled: m_sampled,
            stale: static _ => true,
            tier: _ => tier,
            transforms: m_demandTransforms
        );

        return (ReferenceEquals(objA: plans[0], objB: group) ? null : plans[0]);
    }
    // The nearest rung of a planned group's ladder the store holds, coarser first, for a bake that draws stored rungs
    // alone and a group whose own rung the store lacks; null for any other bake, a group whose rung is held, or a shape
    // the store holds no rung of, which bakes. The rung answered holds the demand the plan asked, so it is asked again
    // only when the demand names another tier, except a floor planned on arrival, which still owes its detail.
    private Group? StoredRung(Group planned, UInt128 identity) {
        if ((Library.Policy != SdfMeshCachePolicy.Stored) || planned.Live || Stores(group: planned, identity: identity)) { return null; }
        for (var step = 1; (step <= SdfMeshTier.Last); step++) {
            var coarser = (planned.Tier + step);
            var finer = (planned.Tier - step);

            if ((coarser > planned.CoarsestTier) && (finer < planned.FinestTier)) { break; }
            foreach (var tier in (ReadOnlySpan<int>)[coarser, finer]) {
                if ((tier > planned.CoarsestTier) || (tier < planned.FinestTier) || (RungOf(group: planned, tier: tier) is not { } rung)) { continue; }
                if (!Stores(group: rung, identity: identity)) {
                    rung.Retired = true;
                    continue;
                }
                Library.Cache.Count(kind: SdfMeshCache.RungsMissed);
                SdfMeshBakeReport.RungMissed(asked: planned.Tier, drawn: rung.Tier, dynamic: planned.Dynamic, residency: Residency, selector: planned.Selector, shape: planned.Shape);
                rung.FirstDetailOwed = planned.FirstDetailOwed;
                rung.DemandPixel = planned.DemandPixel;
                if (!planned.FirstDetailOwed) {
                    rung.DemandedTier = planned.Tier;
                    rung.RetierRefused = true;
                }
                return rung;
            }
        }
        return null;
    }
    // Starts, for each geometry a done group draws, the next rung of its ladder the store lacks, coarsest first, at most
    // SdfMeshTier.MaxInFlight at once; true while any ladder still has a rung to look at or in flight. A rung bakes in
    // its group's place as a tier's replacement does, drawing the group's mesh meanwhile, and gives the place back when
    // it is done (FinishFill). Under the gate, on the frame's thread, at a tier decision.
    private bool FillLadders(int fills) {
        var identity = Identity.Value;

        if (identity != m_laddersIdentity) {
            m_ladders.Clear();
            m_laddersIdentity = identity;
        }

        var unfinished = (fills > 0);

        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            // Live geometry takes another shape at each step of its input, so its other rungs are no later boot's to find.
            if (group.Retired || group.Failed || group.Live || (Selection is not null)) { continue; }
            // A group still landing, or one a replacement stands in for, is walked once it draws its own tier.
            if (!group.Done || (group.Replaces is not null) || group.Ahead || group.FirstDetailOwed) {
                unfinished = true;
                continue;
            }
            if (!m_ladders.TryGetValue(key: group.Sealed, value: out var ladder)) { m_ladders.Add(key: group.Sealed, value: ladder = new Ladder { Next = group.CoarsestTier }); }
            if (ladder.Next < group.FinestTier) { continue; }
            unfinished = true;
            if (ladder.Filling is { Retired: false, Fill: true }) { continue; }
            ladder.Filling = null;
            if (fills >= SdfMeshTier.MaxInFlight) { break; }
            while (ladder.Next >= group.FinestTier) {
                var tier = ladder.Next;

                if (tier == group.Tier) {
                    ladder.Next--;
                    continue;
                }
                if (RungOf(group: group, tier: tier) is not { } rung) {
                    ladder.Next = (group.FinestTier - 1);
                    break;
                }
                if (Stores(group: rung, identity: identity)) {
                    rung.Retired = true;
                    ladder.Next--;
                    continue;
                }
                rung.Fill = true;
                ladder.Filling = rung;
                m_fillsStarted++;
                fills++;
                _ = StartReplacement(group: rung, index: index, old: group);
                break;
            }
        }
        return unfinished;
    }
    // Gives a filled rung's place back to the group it stood in for, which drew throughout, and moves that geometry's
    // ladder past the rung whatever it meshed: a surface is in the store, and nothing or a failure is not baked twice.
    // Under the gate.
    private void FinishFill(Group fill, Group replaced, int index) {
        if (m_ladders.TryGetValue(key: replaced.Sealed, value: out var ladder) && ReferenceEquals(objA: ladder.Filling, objB: fill)) {
            ladder.Filling = null;
            ladder.Next = Math.Min(val1: ladder.Next, val2: (fill.Tier - 1));
        }
        Library.Cache.Count(kind: SdfMeshCache.RungsFilled);
        GiveBack(index: index, replaced: replaced, replacement: fill);
    }
}
