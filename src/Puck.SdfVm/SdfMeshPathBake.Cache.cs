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
            transforms: m_slotPoses
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
                CountRungMissed(asked: planned, drawn: rung.Tier, first: false);
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
    // The stored rung a bake that bakes what its demand asks (SdfMeshCachePolicy.Demand) takes in place of a tier the
    // library and the store lack, so a boot over a store an earlier run warmed bakes only what no rung of the shape
    // answers. For a finer tier (a retier, a first detail, an arrival, drawn being int.MaxValue for one drawing nothing)
    // it is the nearest stored rung finer still, which shows every detail the demand asks. For a coarser tier, which only
    // the region's budget asks (its spare groups and its bias), it is the nearest stored rung between the tier drawn and
    // the one asked, else the nearest coarser than the one asked: the budget gives detail up to free bytes, and any
    // stored rung coarser than the tier drawn frees them without a bake. Null for live geometry, a tier the store holds,
    // any other policy, or a shape the store holds no such rung of, which bakes.
    private Group? DemandRung(Group planned, UInt128 identity, int drawn) {
        if ((Library.Policy != SdfMeshCachePolicy.Demand) || planned.Live || (Library.Disk is not { } disk) || Stores(group: planned, identity: identity) ||
            (planned.Dynamic && disk.Holds(geometry: SdfMeshLibrary.FormGeometry(form: planned.Form), identity: identity))) { return null; }

        var finer = (planned.Tier < drawn);

        for (var tier = (planned.Tier - 1); (tier >= (finer ? planned.FinestTier : (drawn + 1))); tier--) {
            if (StoredRungAt(identity: identity, planned: planned, tier: tier) is { } rung) { return rung; }
        }
        if (finer) { return null; }
        for (var tier = (planned.Tier + 1); (tier <= planned.CoarsestTier); tier++) {
            if (StoredRungAt(identity: identity, planned: planned, tier: tier) is { } rung) { return rung; }
        }
        return null;
    }
    // A planned group planned again at another tier when the library or the store holds that rung, carrying the plan's
    // demand; null otherwise.
    private Group? StoredRungAt(Group planned, int tier, UInt128 identity) {
        if (RungOf(group: planned, tier: tier) is not { } rung) { return null; }
        if (!Stores(group: rung, identity: identity)) {
            rung.Retired = true;
            return null;
        }
        Library.Cache.Count(kind: SdfMeshCache.RungsTaken);
        rung.FirstDetailOwed = planned.FirstDetailOwed;
        rung.DemandPixel = planned.DemandPixel;
        rung.DemandedTier = planned.DemandedTier;
        return rung;
    }
    // What keeps a ladder bake's ladders from full, as its latest tier decision found it (FillLadders): the rungs in
    // flight, the ladders with a rung still to bake, the groups not yet drawing their own tier (still landing, or a
    // replacement or first detail standing in their place), and the first group that holds, by its state.
    private readonly record struct LadderHold(int InFlight, int Owed, int Waiting, string? First) {
        public bool Holds => ((InFlight > 0) || (Owed > 0) || (Waiting > 0));

        public override string ToString() => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"{InFlight} rung(s) in flight, {Owed} ladder(s) with a rung to bake, {Waiting} group(s) not yet drawing their own tier{((First is null) ? string.Empty : $"; first: {First}")}");
    }

    // The rungs a stored-only bake missed for each geometry of a sampled field's reader, with the geometry that hashed
    // the snapshots, until the field moves past them (ReclassifyReaderMisses). Under the gate.
    private readonly Dictionary<SdfMeshGeometry, (SdfMeshGeometryBuilder Geometry, int Misses)> m_readerMisses = [];

    // Counts and reports a rung a stored-only bake asked that the store lacked, which the drawn tier stands in for: the one
    // place RungsMissed is counted, so every miss of a sampled field's reader is on the record its field's move reads.
    // Under the gate.
    private void CountRungMissed(Group asked, int drawn, bool first) {
        Library.Cache.Count(kind: SdfMeshCache.RungsMissed);
        SdfMeshBakeReport.RungMissed(drawn: drawn, first: first, group: asked, residency: Residency);
        if (!asked.Geometry.ReadsSampled) { return; }
        m_readerMisses[asked.Sealed] = (asked.Geometry, (m_readerMisses.TryGetValue(key: asked.Sealed, value: out var held) ? (held.Misses + 1) : 1));
    }
    // Moves to RungsMissedLive the misses of every reader geometry whose snapshots the frame's sampled set no longer holds:
    // the world moved the field past the state they were planned at, which makes that geometry live. Under the gate, on
    // the frame's thread, when the sampled set moved (Revisit).
    private void ReclassifyReaderMisses(Puck.SignedDistance.SdfSampledSet sampled) {
        if (m_readerMisses.Count == 0) { return; }

        List<SdfMeshGeometry>? moved = null;

        foreach (var (geometry, (builder, misses)) in m_readerMisses) {
            if (builder.SampledMatches(sampled: sampled)) { continue; }
            Library.Cache.Add(amount: misses, kind: SdfMeshCache.RungsMissedLive);
            SdfMeshBakeReport.RungsMissedLive(geometry: geometry, misses: misses, residency: Residency);
            (moved ??= []).Add(item: geometry);
        }
        foreach (var geometry in (moved ?? [])) { _ = m_readerMisses.Remove(key: geometry); }
    }

    // Starts, for each geometry a done group draws, the next rung of its ladder the store lacks, coarsest first, at most
    // SdfMeshTier.MaxInFlight at once, and answers what still holds the ladders. A rung bakes in its group's place as a
    // tier's replacement does, drawing the group's mesh meanwhile, and gives the place back when it is done
    // (FinishFill); one that left the list unfinished is let go and its rung looked at again. Under the gate, on the
    // frame's thread, at a tier decision.
    private LadderHold FillLadders(int fills) {
        var identity = Identity.Value;

        if (identity != m_laddersIdentity) {
            m_ladders.Clear();
            m_laddersIdentity = identity;
        }

        var owed = 0;
        var waiting = 0;
        string? first = null;

        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            // Live geometry takes another shape at each step of its input, so its other rungs are no later boot's to find.
            // A rung being filled stands in its group's place and is counted among the fills.
            if (group.Retired || group.Failed || group.Live || group.Fill || (Selection is not null)) { continue; }
            // A group still landing, or one a replacement stands in for, is walked once it draws its own tier.
            if (!group.Done || (group.Replaces is not null) || group.Ahead || group.FirstDetailOwed) {
                waiting++;
                first ??= group.State();
                continue;
            }
            if (!m_ladders.TryGetValue(key: group.Sealed, value: out var ladder)) { m_ladders.Add(key: group.Sealed, value: ladder = new Ladder { Next = group.CoarsestTier }); }
            if (ladder.Next < group.FinestTier) { continue; }
            if (ladder.Filling is { Retired: false, Fill: true, Listed: true }) { continue; }
            ladder.Filling = null;
            if (fills >= SdfMeshTier.MaxInFlight) {
                owed++;
                first ??= group.State();
                continue;
            }
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
        return new LadderHold(First: first, InFlight: fills, Owed: owed, Waiting: waiting);
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
