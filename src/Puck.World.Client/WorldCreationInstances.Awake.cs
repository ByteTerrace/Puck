using System.Numerics;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.World.Client;

// The awake set: the pool entries a pack visits. A pack costs the registrations something woke, never the pool.
//
// The invariant: a registration the pack does not visit would have been retained had it been visited (nothing its pose
// reads moved and it owes no pose), or holds a pose no view, shadow cascade or reader may show. What wakes an entry,
// each the one place its cause is reported:
// - a reconcile, a live emit, a frame that owes the whole table, another slot base, source or state document: every
//   entry (m_wakeAll, and the tests in PackTransforms);
// - a body's presented inputs (its pose, pose epoch, address, carries, facts, active flag): the source's journal
//   (IWorldStampSource.BodyChanges), read through this pool's cursor, wakes the registration the body roots and the
//   rows attached to it; a source with no journal wakes every body-bound entry each pack;
// - a state cell a registration's lease holds: the mirror's change log through m_stateWatch;
// - a replay cursor or a cue coming due: the deadline heap, scheduled when a visited registration goes to sleep;
// - its own continuation (restless slots, a driver still advancing, a root follower still settling): EndStep keeps it;
// - a rig that reads another body, a creation sized by a solid's sampled scale, and the bodies such rigs read: every
//   pack (m_everyPack), since what they read is not journaled;
// - a held pose (RateOwed) something may show again: the held cells' cull, below;
// - a cue fired, a reseed, a read of a held body's parts: the door that did it.
// Where it stops: the journal and the change log are the source's and the mirror's to keep complete; a new input a pose
// reads that neither carries must wake its readers here, or a sleeping registration draws a stale pose.
//
// The held cells: every registration holding a pose (RateOwed) sits in a cell of a coarse grid by its root. Each pack
// tests each occupied cell's box once against the frame's views and shadow cascades. A cell none may show keeps its
// members asleep: each is rated HeldHertz, as its own sphere inside the box would be. A cell one may show wakes its
// members, and each decides for itself as before (RateOf).
public sealed partial class WorldCreationInstances {
    // The side of a held cell, in metres: coarse enough that a place's animated creations share a few cells.
    private const float HeldCellMetres = 32f;

    private readonly MarkedIndexSet m_awake = new();
    private bool m_wakeAll = true;
    private int[] m_visits = [];
    private int m_visitCount;

    // The seconds every Tick has advanced the pool by: the clock replay cursors, cues and held seconds are counted on.
    private double m_clockSeconds;
    private IWorldStampSource? m_packedSource;

    private readonly GpuRowCursor m_bodyCursor = new();
    private readonly GpuUploadRuns m_bodyRuns = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly WorldLeaseWatch m_stateWatch = new();

    // The entries a body's journal frame wakes: the pool indices of the rows attached to each body (a body's own
    // registration is m_bodySlots), and every body-bound entry, for a source that keeps no journal.
    private readonly Dictionary<int, List<int>> m_attachedByBody = [];
    private readonly List<int> m_bodyBound = [];
    // The entries every pack visits: what they read is in no journal.
    private readonly List<int> m_everyPack = [];

    // The registrations whose replay cursor or cue next changes at a known pool clock, soonest first. An entry is live
    // only while its registration still holds the pool index and still expects that due time (Registration.DeadlineDue).
    private (double Due, int Index, Registration Live)[] m_deadlines = [];
    private int m_deadlineCount;

    private struct DeadlineOrder : IComparer<(double Due, int Index, Registration Live)> {
        public readonly int Compare((double Due, int Index, Registration Live) x, (double Due, int Index, Registration Live) y) {
            var due = x.Due.CompareTo(value: y.Due);

            return ((due != 0) ? due : x.Index.CompareTo(value: y.Index));
        }
    }

    // One occupied cell of the held grid: its members' pool indices, the box holding every member's rating sphere (it
    // only grows while the cell is occupied), and the pack that last found no view showing it.
    private sealed class HeldCell {
        public (int X, int Y, int Z) Key;
        public readonly List<int> Members = [];
        public Vector3 Low;
        public Vector3 High;
        public long UnseenPack = -1L;
    }

    private readonly Dictionary<(int X, int Y, int Z), HeldCell> m_heldCells = [];
    private HeldCell?[] m_heldCell = [];
    private int[] m_heldAt = [];
    // Every held registration, by kind, and the slots they hold; and those the pack in progress visited and held again.
    // The difference is what a pack holds without visiting, which it counts as held all the same (CountUnvisitedHolds).
    private int m_heldBodies;
    private int m_heldPlacements;
    private int m_heldSlots;
    private int m_packHeldBodies;
    private int m_packHeldPlacements;
    private int m_packHeldSlots;

    /// <summary>Gets the number of pool entries the latest <see cref="PackTransforms"/> visited: every entry on a frame
    /// that owes the whole table or follows a reconcile, otherwise only those something woke.</summary>
    public int VisitCount => m_visitCount;

    // The pool indices whose registration straddles a door, in pool order: SlotClips is composed from these alone.
    private readonly SortedSet<int> m_clipped = [];
    private bool m_clipsChanged;

    // The readings' demand this pool last served, and the frames it still refreshes them for.
    private long m_readingsRead = -1L;
    private int m_readingsFrames;

    /// <summary>Wakes the registration at a pool index, so the next pack visits it.</summary>
    private void WakeEntry(int index) => _ = m_awake.Add(index: index);

    // Gathers this pack's visits into m_visits, in pool order. Returns whether the pack visits every entry.
    private bool CollectVisits(IWorldStampSource client, bool everything) {
        var journal = client.BodyChanges;

        // The cursor moves every pack, so a pack that visits everything leaves no body change for the next to repeat.
        if (m_bodyCursor.Collect(into: m_bodyRuns, reader: journal, revision: ((journal is null) ? m_packCount : 0L), rows: WorldClient.EntityCapacity, table: client, through: client.BodyChangesSerial)) {
            foreach (var index in m_bodyBound) {
                WakeEntry(index: index);
            }
        } else {
            for (var run = 0; (run < m_bodyRuns.Count); run++) {
                var end = (m_bodyRuns.Start(index: run) + m_bodyRuns.Length(index: run));

                for (var body = m_bodyRuns.Start(index: run); (body < end); body++) {
                    WakeBody(body: body);
                }
            }
        }
        m_bodyCursor.Commit();

        everything |= !m_stateWatch.Collect(mirror: client.StateMirror, woken: m_awake);

        if (!everything) {
            foreach (var index in m_everyPack) {
                WakeEntry(index: index);
            }
            foreach (var body in m_bodyReaders.Keys) {
                WakeBody(body: body);
            }
            if (m_selectedBodyReaders.Count > 0) {
                ResolveSelectedReaders();
                foreach (var (body, readers) in m_selectedReadersByBody) {
                    if (readers.Count > 0) {
                        WakeBody(body: body);
                    }
                }
            }

            WakeDue();
            WakeShown();
        }

        if (m_visits.Length < m_poolCount) {
            m_visits = new int[m_pool.Length];
        }
        if (everything) {
            for (var index = 0; (index < m_poolCount); index++) {
                m_visits[index] = index;
            }

            m_visitCount = m_poolCount;
        } else {
            var members = m_awake.Members;
            var count = 0;

            // A reconcile may have shortened the pool under an index something woke before it.
            foreach (var index in members) {
                if (index < m_poolCount) {
                    m_visits[count++] = index;
                }
            }

            m_visits.AsSpan(start: 0, length: count).Sort();
            m_visitCount = count;
        }

        // What a visit wakes from here on is the next pack's.
        m_awake.Clear();

        return everything;
    }
    // Wakes what a body's change reaches: the registration it roots and the rows attached to it.
    private void WakeBody(int body) {
        if ((((uint)body) < ((uint)m_bodySlots.Length)) && (m_bodySlots[body] > 0)) {
            WakeEntry(index: (m_bodySlots[body] - 1));
        }
        if (m_attachedByBody.TryGetValue(key: body, value: out var attached)) {
            foreach (var index in attached) {
                WakeEntry(index: index);
            }
        }
    }
    // Wakes every registration whose scheduled cursor or cue change has come due.
    private void WakeDue() {
        var order = default(DeadlineOrder);

        while ((m_deadlineCount > 0) && (m_deadlines[0].Due <= m_clockSeconds)) {
            var (due, index, live) = BinaryHeap.Pop(count: ref m_deadlineCount, heap: m_deadlines.AsSpan(), order: order);

            m_deadlines[m_deadlineCount] = default;
            if ((index < m_poolCount) && ReferenceEquals(objA: m_pool[index], objB: live) && (live.DeadlineDue == due)) {
                live.DeadlineDue = double.PositiveInfinity;
                WakeEntry(index: index);
            }
        }
    }
    // Schedules a registration that goes to sleep for the pool clock its replay cursor or a cue next changes at.
    private void Schedule(int index, Registration live) {
        var wait = NextClockEvent(live: live);
        var due = (float.IsPositiveInfinity(f: wait) ? double.PositiveInfinity : (m_clockSeconds + MathF.Max(x: wait, y: 0f)));

        if (due == live.DeadlineDue) {
            return;
        }

        live.DeadlineDue = due;
        if (double.IsPositiveInfinity(d: due)) {
            return;
        }
        if (m_deadlineCount == m_deadlines.Length) {
            Array.Resize(array: ref m_deadlines, newSize: Math.Max(val1: 16, val2: (m_deadlines.Length * 2)));
        }

        BinaryHeap.Push(count: ref m_deadlineCount, heap: m_deadlines.AsSpan(), item: (due, index, live), order: default(DeadlineOrder));
    }
    // The seconds until a registration's replay cursor steps, its holding cue frame ends or a cue next fires on its own;
    // infinity for one with no timeline.
    private static float NextClockEvent(Registration live) {
        if (live.Creation.Document.Frames is not { Count: > 0 }) {
            return float.PositiveInfinity;
        }

        var wait = float.PositiveInfinity;

        if (live.Cues is { Count: > 0 } cues) {
            if (live.CueFrame > 0) {
                wait = MathF.Min(x: wait, y: (live.CueHoldUntil - live.CueClock));
            }

            for (var index = 0; (index < cues.Count); index++) {
                if (live.CueFrames[index] > 0) {
                    wait = MathF.Min(x: wait, y: (live.CueNextSeconds[index] - live.CueClock));
                }
            }
        }
        if (live.Replay) {
            wait = MathF.Min(x: wait, y: (WorldPlacementPolicy.TimelineSecondsPerFrame - live.Clock));
        }

        return wait;
    }
    // Brings a registration's replay cursor and cue clock up to the pool clock, as one step over every second since it
    // last looked: hold-style, each frame holding TimelineSecondsPerFrame and looping 1..N, whole crossed frames
    // subtracting so a long sleep lands on the right frame, and a cue firing at most once a step, as after a hitch.
    private void AdvanceClock(Registration live) {
        if (live.ClockStamp < 0d) {
            live.ClockStamp = m_clockSeconds;

            return;
        }

        var deltaSeconds = ((float)(m_clockSeconds - live.ClockStamp));

        live.ClockStamp = m_clockSeconds;
        if ((deltaSeconds <= 0f) || (live.Creation.Document.Frames is not { Count: > 0 } frames)) {
            return;
        }

        if (live.Cues is { Count: > 0 } cues) {
            live.CueClock += deltaSeconds;

            if ((live.CueFrame > 0) && (live.CueClock >= live.CueHoldUntil)) {
                live.CueFrame = 0;
            }

            for (var index = 0; (index < cues.Count); index++) {
                if ((live.CueFrames[index] > 0) && (live.CueClock >= live.CueNextSeconds[index])) {
                    FireCue(index: index, live: live);
                }
            }
        }

        if (!live.Replay) {
            return;
        }

        live.Clock += deltaSeconds;
        if (live.Clock >= WorldPlacementPolicy.TimelineSecondsPerFrame) {
            var steps = ((long)(live.Clock / WorldPlacementPolicy.TimelineSecondsPerFrame));

            live.Clock -= (steps * WorldPlacementPolicy.TimelineSecondsPerFrame);
            live.FrameCursor = (((int)(((live.FrameCursor + steps) - 1L) % frames.Count)) + 1);
        }
    }
    // Counts a visited registration's held seconds over the packs that skipped it, and rates it held where its cell was
    // out of every view's sight on one of them: its next full pose is then a continuity cut, as for a body that held
    // unseen while every pack visited it.
    private void CatchUpHold(int index, Registration live, float deltaSeconds) {
        if (live.RateOwed) {
            // The clock before this pack's own seconds, which the hold decision adds itself, less the clock the held
            // seconds were last counted to: the seconds of the packs in between.
            live.RateSeconds += ((float)Math.Max(val1: 0d, val2: ((m_clockSeconds - deltaSeconds) - live.RateStamp)));
            if ((m_heldCell[index] is { } cell) && (cell.UnseenPack > live.VisitPack)) {
                live.RateSlowest = SdfUpdateRateTiers.HeldHertz;
            }
        }

        live.RateStamp = m_clockSeconds;
    }
    // Puts a registration that holds its pose in the held cell its root stands in, growing the cell's box to its
    // rating sphere.
    private void Hold(int index, Registration live, Vector3 center, float radius) {
        var key = (((int)MathF.Floor(x: (center.X / HeldCellMetres))), ((int)MathF.Floor(x: (center.Y / HeldCellMetres))), ((int)MathF.Floor(x: (center.Z / HeldCellMetres))));

        if (!m_heldCells.TryGetValue(key: key, value: out var cell)) {
            m_heldCells.Add(key: key, value: cell = new HeldCell { High = new Vector3(value: float.NegativeInfinity), Key = key, Low = new Vector3(value: float.PositiveInfinity) });
        }
        if (!ReferenceEquals(objA: m_heldCell[index], objB: cell)) {
            Unhold(index: index);
            m_heldCell[index] = cell;
            m_heldAt[index] = cell.Members.Count;
            cell.Members.Add(item: index);
            m_heldSlots += live.Slots;
            if (live.BodyIndex is not null) { m_heldBodies++; } else { m_heldPlacements++; }
        }

        m_packHeldSlots += live.Slots;
        if (live.BodyIndex is not null) { m_packHeldBodies++; } else { m_packHeldPlacements++; }

        var reach = new Vector3(value: radius);

        cell.Low = Vector3.Min(value1: cell.Low, value2: (center - reach));
        cell.High = Vector3.Max(value1: cell.High, value2: (center + reach));
    }
    // Takes a registration out of its held cell: it poses, or it no longer draws.
    private void Unhold(int index) {
        if (m_heldCell[index] is not { } cell) {
            return;
        }

        var at = m_heldAt[index];
        var last = cell.Members[^1];

        cell.Members[at] = last;
        m_heldAt[last] = at;
        cell.Members.RemoveAt(index: (cell.Members.Count - 1));
        m_heldCell[index] = null;

        if (m_pool[index] is { } live) {
            m_heldSlots -= live.Slots;
            if (live.BodyIndex is not null) { m_heldBodies--; } else { m_heldPlacements--; }
        }
        if (cell.Members.Count == 0) {
            _ = m_heldCells.Remove(key: cell.Key);
        }
    }
    // Forgets every held cell: a reconcile gives the pool indices other registrations.
    private void ReleaseHeldCells() {
        m_heldCells.Clear();
        Array.Clear(array: m_heldCell);
        (m_heldBodies, m_heldPlacements, m_heldSlots) = (0, 0, 0);
    }
    // Counts the poses a pack held without visiting them, beside those it visited and held (BeginStep): every held
    // registration counts as held on every frame, visited or not.
    private void CountUnvisitedHolds() {
        var bodies = (m_heldBodies - m_packHeldBodies);
        var placements = (m_heldPlacements - m_packHeldPlacements);

        if ((bodies + placements) <= 0) {
            return;
        }

        m_rateCounts?.Add(amount: bodies, kind: SdfUpdateRateCounts.BodiesHeld);
        m_rateCounts?.Add(amount: placements, kind: SdfUpdateRateCounts.PlacementsHeld);
        WorldCreationWork.ProcessWork.Add(amount: (bodies + placements), kind: WorldCreationWork.RateHeld);
        WorldCreationWork.ProcessWork.Add(amount: (m_heldSlots - m_packHeldSlots), kind: WorldCreationWork.SlotsRetained);
    }
    // The held cells' cull, one box test a cell against each view and each shadow cascade: wakes the members of every
    // cell something may show and leaves the rest asleep. While the lever is off, no view or cascade is known, or the
    // presentation is unpaced, no pose holds, so every member wakes.
    private void WakeShown() {
        if (m_heldCells.Count == 0) {
            return;
        }

        var holds = (((m_rates & SdfUpdateRates.Bodies) != 0) && (m_rateViews.Count > 0) && m_rateShadowsKnown && m_presentationRate.IsPaced);

        foreach (var cell in m_heldCells.Values) {
            if (holds && !CellShown(cell: cell)) {
                cell.UnseenPack = m_packCount;

                continue;
            }

            foreach (var index in cell.Members) {
                WakeEntry(index: index);
            }
        }
    }
    // Whether a view's camera may see any of a cell's box, or a shadow cascade may draw any of it: the tests a
    // registration's own sphere takes (BodyPixelsPerMetre, Shown), over the box that holds every member's sphere.
    private bool CellShown(HeldCell cell) {
        var center = ((cell.Low + cell.High) * 0.5f);
        var half = ((cell.High - cell.Low) * 0.5f);
        var (halfX, halfY, halfZ) = ((Vector3.UnitX * half.X), (Vector3.UnitY * half.Y), (Vector3.UnitZ * half.Z));

        foreach (var (camera, _) in m_rateViews) {
            if (camera.MaySee(center: center, halfX: halfX, halfY: halfY, halfZ: halfZ, margin: WorldPortalVisibility.Margin)) {
                return true;
            }
        }

        var box = new SdfMeshBox(Center: center, HalfX: halfX, HalfY: halfY, HalfZ: halfZ);

        foreach (var (_, cull) in m_rateShadows) {
            if (cull.Keeps(box: box)) {
                return true;
            }
        }

        return false;
    }
    // Indexes what a body's change wakes and what every pack visits; called with the reader index at each reconcile.
    private void IndexWakes() {
        m_attachedByBody.Clear();
        m_bodyBound.Clear();
        m_everyPack.Clear();

        for (var index = 0; (index < m_poolCount); index++) {
            if (m_pool[index] is not { } live) {
                continue;
            }

            if (live.BodyIndex is not null) {
                m_bodyBound.Add(item: index);
            } else if (live.Row?.Attach is { } attach) {
                m_bodyBound.Add(item: index);
                if (!m_attachedByBody.TryGetValue(key: attach.BodyIndex, value: out var attached)) {
                    m_attachedByBody.Add(key: attach.BodyIndex, value: attached = []);
                }

                attached.Add(item: index);
            }
            // A solid's sampled scale is interpolated each frame beside the bodies, in no journal.
            if (live.ReadsBodies || ((live.Creation.EngineDocument.SizeBy is not null) && (live.Row is { Solid: not null }))) {
                m_everyPack.Add(item: index);
            }
        }
    }
    // Records a visited registration's door clip, or none, noting whether the clipped set changed.
    private void SetClip(int index, Registration live, SdfSlotClip? clip) {
        if (live.Clip is null) {
            if (clip is null) {
                return;
            }

            _ = m_clipped.Add(item: index);
        } else if (clip is null) {
            _ = m_clipped.Remove(item: index);
        } else if (live.Clip.Value.SameStagedContent(other: clip.Value)) {
            return;
        }

        live.Clip = clip;
        m_clipsChanged = true;
    }
    // Publishes each live registration's tier and size while something reads them (SdfUpdateRateReadings.Reads), and its
    // slots' codes while the pool writes codes: a pass over the whole pool, run only on those frames.
    private void PublishRates(IWorldStampSource client, int slotBase) {
        var listed = false;

        if (m_rateReadings is { } readings) {
            var read = readings.Reads;

            if (read != m_readingsRead) {
                m_readingsRead = read;
                m_readingsFrames = SdfUpdateRateReadings.FramesPerRead;
            }

            listed = (m_readingsFrames > 0);
            if (listed) {
                m_readingsFrames--;
            }
        }
        if (!listed && !m_writesRateCodes) {
            return;
        }

        var rates = ((m_rates & SdfUpdateRates.Bodies) != 0);

        for (var index = 0; (index < m_poolCount); index++) {
            if ((m_pool[index] is not { PackedRoot: not null } live)) {
                continue;
            }

            var (center, _, placementScale) = RootPose(client: client, live: live);
            var (hertz, pixelsPerMetre) = RateOf(center: center, live: live, placementScale: placementScale, radius: RateReach(live: live, placementScale: placementScale));

            if (listed && rates) {
                live.RateName ??= ((live.BodyIndex is { } body) ? $"body:{body}" : $"placement:{live.Row?.Id}");
                m_bodyReadings.Add(item: new SdfUpdateRateReadings.Reading(Hertz: hertz, Name: live.RateName, Size: pixelsPerMetre));
            }
            if (m_writesRateCodes) {
                m_slotRateCodes.AsSpan(length: live.Slots, start: (slotBase + RootSlotOffset(index: index))).Fill(value: ((byte)SdfUpdateRateTiers.CodeOf(hertz: hertz, updated: (live.PosedPack == m_packCount))));
            }
        }

        if (listed) {
            m_bodyReadings.PublishTo(lever: SdfUpdateRates.Bodies, readings: m_rateReadings!);
        }
    }
}
