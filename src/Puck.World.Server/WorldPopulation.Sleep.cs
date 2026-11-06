using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Derived work sets. Slot order is the authority order, independent of insertion or wake order. They are
    // rebuilt during restore. Canonical frozen-component membership, gravity observations and pending event
    // eligibility are continuation state; spatial bounds are derived. The island graph is the bodies' own persistent
    // contacts (WorldPopulation.Rigid.cs): a rigid contact joins two rigid bodies' islands, and a contact with a kit
    // body is a support whose change wakes the island it holds. Nothing rebuilds it per tick.
    private readonly BodyWorkSet m_awakeBodies;
    private readonly BodyWorkSet m_sleepingBodies;
    private readonly BodyWorkSet m_eventBodies;

    private readonly Dictionary<int, FixedVector3?> m_sleepGravity = [];
    // The parked bodies by the island root they parked with (one plus the root slot), each list in slot order: the
    // index of each body's Parked(root) state (WorldBody.RigidSleepRootPlusOne), which a wake reads instead of walking
    // contacts. Written only with that state, by ParkIslandMember and WakeIsland.
    private readonly Dictionary<int, List<int>> m_islandMembers = [];
    private readonly Stack<List<int>> m_islandMemberPool = [];
    private readonly List<int> m_island = [];
    private readonly HashSet<int> m_islandVisited = [];
    private readonly HashSet<int> m_parkVisited = [];

    private readonly int[] m_stepBodies;
    private readonly int[] m_eventBodyImage;
    private readonly int[] m_sleepQueries;

    private readonly SleepingIndex m_sleepIndex = new();
    private bool m_sleepIndexDirty = true;

    private bool m_wakingIsland;
    private ulong m_sleepFieldVersion;
    // The contact-field terms the sleepers last answered for (RecordSleepFieldObservation), and whether they are known. A restore
    // whose sleepers observed an older field leaves them unknown, so its next change wakes every sleeper, as the
    // uninterrupted run's global change did.
    private (ulong Install, ulong Attached, ulong Fields) m_sleepFieldTerms;
    private bool m_sleepFieldTermsKnown;
    private readonly List<int> m_fieldWakes = [];

    /// <summary>Number of rigid bodies parked outside the tick's integration and contact work sets.</summary>
    public int SleepingRigidBodies => m_sleepingBodies.Count;
    /// <summary>Number of simulated bodies visited by the integration pass this tick.</summary>
    public int SimulatedBodyVisits { get; private set; }

    // An entry's slot changed (it took or lost its body, or became active or inactive): the work sets follow. A slot
    // that lets its body go has already passed DisturbBody (Entry's setters), while it still held the body.
    private void BodyEntryChanged(int index) {
        m_eventBodies.Add(item: index);
        if (m_entries[index] is { Active: true, Body: not null }) { m_awakeBodies.Add(item: index); } else { m_awakeBodies.Remove(item: index); }
    }
    /// <summary>Wakes a body and every parked island it touches: the body's continuous change (an input, a press, a
    /// glide, a rest-latch break) means whatever rests on it or holds it must solve again.</summary>
    /// <param name="index">The body's population index.</param>
    internal void BodyWoke(int index) {
        WakeBodyIsland(index: index);
        m_eventBodies.Add(item: index);
        if (m_entries[index] is { Active: true, Body: not null }) { m_awakeBodies.Add(item: index); }
    }
    /// <summary>THE DISCONTINUITY DOOR. A slot's body is about to change by a discontinuity (a teleport, a collider or
    /// rigid-facet change, or the slot letting its body go): wakes the body's own island and every parked island it
    /// touches, then forgets every contact the body owns and every contact another body owns with it, whose manifolds
    /// describe a body that is no longer there. It runs before the change lands, so it sees the body, its contacts and
    /// its island as they were. This is the one place the invariant holds that a parked body wakes whenever anything it
    /// rests on, or that rests on it, changes by a discontinuity: <see cref="WorldBody"/>'s teleport commit and kit
    /// recompile reach it through their population binding, and <see cref="Entry"/>'s Body and Active setters before a
    /// slot releases its body.</summary>
    /// <param name="index">The slot's population index.</param>
    internal void DisturbBody(int index) {
        WakeBodyIsland(index: index);
        ForgetRigidContacts(index: index, keepReferred: false);
    }
    // Wakes the body's own island, by the root it parked with, then every parked island one of its contacts joins it to:
    // the contacts it owns and the contacts other bodies own with it, occupied or not. Membership is read from what
    // parking recorded and from the contact lists, never from slot occupancy, so a contact cleared or a body removed a
    // moment earlier cannot hide a sleeper resting on it.
    private void WakeBodyIsland(int index) {
        if (m_wakingIsland) { return; }
        m_wakingIsland = true;
        if (m_sleepingBodies.Contains(item: index)) { WakeIsland(rootPlusOne: m_entries[index].Body!.RigidSleepRootPlusOne); }
        if (m_entries[index].Body is { } body) {
            foreach (var contact in body.RigidContacts) {
                if ((contact.Partner >= 0) && m_sleepingBodies.Contains(item: contact.Partner)) {
                    WakeIsland(rootPlusOne: m_entries[contact.Partner].Body!.RigidSleepRootPlusOne);
                }
            }
        }
        if ((index < m_rigidContactReferrers.Length) && (m_rigidContactReferrers[index] is { } referrers)) {
            foreach (var owner in referrers) {
                if (m_sleepingBodies.Contains(item: owner)) { WakeIsland(rootPlusOne: m_entries[owner].Body!.RigidSleepRootPlusOne); }
            }
        }
        m_wakingIsland = false;
    }
    // Wakes every body parked under one root, in slot order. With ParkIslandMember the only writer of island membership:
    // the body's Parked(root) state, the sleeping work set and the root index change together here and nowhere else.
    private void WakeIsland(int rootPlusOne) {
        if (!m_islandMembers.Remove(key: rootPlusOne, value: out var members)) { return; }
        foreach (var member in members) {
            if (!m_sleepingBodies.Remove(item: member)) { continue; }
            m_sleepGravity.Remove(key: member);
            if (m_entries[member].Body is { } body) {
                body.WakeRigidIsland();
                if (m_entries[member].Active) {
                    m_awakeBodies.Add(item: member);
                    m_eventBodies.Add(item: member);
                }
            }
        }
        m_sleepIndexDirty = true;
        members.Clear();
        m_islandMemberPool.Push(item: members);
    }
    // Parks one body under a root: its Parked(root) state, the sleeping work set and the root index, together. The
    // caller records the sleeper's solved gravity.
    private void ParkIslandMember(int index, int rootPlusOne, ulong contactFieldVersion) {
        m_entries[index].Body!.ParkRigidIsland(contactFieldVersion: contactFieldVersion, rootPlusOne: rootPlusOne);
        m_awakeBodies.Remove(item: index);
        m_sleepingBodies.Add(item: index);
        if (!m_islandMembers.TryGetValue(key: rootPlusOne, value: out var members)) {
            members = ((m_islandMemberPool.Count > 0) ? m_islandMemberPool.Pop() : []);
            m_islandMembers.Add(key: rootPlusOne, value: members);
        }
        var position = members.BinarySearch(item: index);

        if (position < 0) { members.Insert(index: ~position, item: index); }
        m_sleepIndexDirty = true;
    }
    private FixedVector3? SolvedSleepGravity(int index) =>
        (((m_gravityField is { } gravity) && gravity.TryAcceleration(acceleration: out var acceleration, entityIndex: index)) ? acceleration : null);
    /// <summary>Parks a body a scoped replay restored under the island root its captured residue names, as the
    /// checkpoint restore does for a whole population (<see cref="RestoreSleepingIslands"/>): the residue never writes
    /// island membership itself.</summary>
    /// <param name="index">The body's population index.</param>
    /// <param name="rootPlusOne">The captured residue's island root plus one; zero leaves the body awake.</param>
    internal void AdoptRestoredIsland(int index, int rootPlusOne) {
        if ((rootPlusOne == 0) || m_sleepingBodies.Contains(item: index) || (m_entries[index] is not { Active: true, Body: not null })) { return; }
        ParkIslandMember(contactFieldVersion: m_sleepFieldVersion, index: index, rootPlusOne: rootPlusOne);
        m_sleepGravity[index] = SolvedSleepGravity(index: index);
    }
    // Collects into m_island, in slot order, the rigid bodies the root's occupied rigid contacts join it to: the island
    // parking records. Waking never re-derives it (WakeBodyIsland).
    private void CollectRigidIsland(int root) {
        m_island.Clear();
        m_islandVisited.Clear();
        m_island.Add(item: root);
        m_islandVisited.Add(item: root);
        for (var cursor = 0; (cursor < m_island.Count); cursor++) {
            var member = m_island[cursor];

            if (m_entries[member].Body is { } body) {
                foreach (var contact in body.RigidContacts) {
                    if ((contact.Partner >= 0) && contact.Manifold.Slots.AnyOccupied &&
                        (m_entries[contact.Partner] is { Active: true, Body: { IsRigid: true } }) && m_islandVisited.Add(item: contact.Partner)) {
                        m_island.Add(item: contact.Partner);
                    }
                }
            }
            if ((member < m_rigidContactReferrers.Length) && (m_rigidContactReferrers[member] is { } referrers)) {
                foreach (var owner in referrers) {
                    if ((m_entries[owner] is { Active: true, Body: { IsRigid: true } ownerBody }) && !m_islandVisited.Contains(item: owner) &&
                        (FindRigidContact(body: ownerBody, partner: member) is { } contact) && contact.Manifold.Slots.AnyOccupied) {
                        m_islandVisited.Add(item: owner);
                        m_island.Add(item: owner);
                    }
                }
            }
        }
        m_island.Sort();
    }
    private ReadOnlySpan<int> PrepareBodyStep() {
        ObserveContactFieldChange();
        m_awakeBodies.CopyTo(array: m_stepBodies);
        foreach (var index in m_awakeBodies) { m_eventBodies.Add(item: index); }
        return m_stepBodies.AsSpan(start: 0, length: m_awakeBodies.Count);
    }
    /// <summary>Settles the parked rigid bodies on the field lattices' step, right after
    /// <see cref="WorldTick.StepFields"/>: the sleepers a changed cell can reach wake to join the next tick's
    /// integration, and the rest observe the change, so the tick ends with no sleeper behind the lattices and a
    /// checkpoint taken there restores the same continuation. Any other change still owed (an install, an attached
    /// move) is left whole to the next <see cref="PrepareBodyStep"/>, which settles every term together.</summary>
    internal void ObserveFieldSteps() {
        var version = ContactFieldVersion;

        if ((m_sleepFieldVersion == version) || !m_sleepFieldTermsKnown ||
            (m_contactFieldTerms.Install != m_sleepFieldTerms.Install) || (m_contactFieldTerms.Attached != m_sleepFieldTerms.Attached)) {
            return;
        }
        ObserveContactFieldChange();
    }
    // Wakes every sleeper a change since the sleepers last observed the field can reach, then records the observation.
    // A change bounded to boxes (attached colliders that moved, regions whose body-read cells changed) reaches only the
    // sleepers whose bounds, grown by FieldWakeMargin, meet the box; an install, an unbounded collider, an attached move
    // without a box, or sleepers whose observation is unknown (a restore that found one behind) reach every sleeper.
    private void ObserveContactFieldChange() {
        var version = ContactFieldVersion;
        var terms = m_contactFieldTerms;
        var box = ((m_baseContactField as WorldColliderSet)?.TakeAttachedChange() ?? default);

        if (m_sleepFieldVersion != version) {
            var local = (
                m_sleepFieldTermsKnown &&
                (terms.Install == m_sleepFieldTerms.Install) &&
                (box.Any || (terms.Attached == m_sleepFieldTerms.Attached))
            );

            if (local && (terms.Fields != m_sleepFieldTerms.Fields)) { FoldFieldRegionChanges(box: ref box); }
            if (local && box is { Any: true, Unbounded: false }) {
                WakeSleepersNear(box: in box, margin: FieldWakeMargin());
            } else {
                WorldBodyWork.Process.Add(amount: m_sleepingBodies.Count, kind: WorldBodyWork.FieldWakes);
                while (m_sleepingBodies.Count > 0) { WakeBodyIsland(index: m_sleepingBodies.Min); }
            }
            m_sleepFieldVersion = version;
        }
        RecordSleepFieldObservation(known: true);
    }
    // Records what the sleepers now stand observed against: the contact-field terms and each region's body-read stamp,
    // taken together so the next change names what moved since. Every place that settles the sleepers records through
    // here, so the terms and the region stamps can never describe different moments.
    private void RecordSleepFieldObservation(bool known) {
        m_sleepFieldTerms = m_contactFieldTerms;
        m_sleepFieldTermsKnown = known;
        RecordFieldRegionRevisions();
    }
    // Wakes, island by island in slot order, every sleeper whose bounds grown by the margin meet the box. The candidates
    // come from the retained sleeping index over the grown box, so a change that reaches nobody visits no sleeper; the
    // rest observe the change through the population's own observation (m_sleepFieldVersion), never a per-sleeper write.
    private void WakeSleepersNear(in WorldColliderBox box, FixedQ4816 margin) {
        if (m_sleepIndexDirty) {
            m_sleepIndex.Rebuild(population: this, sleeping: m_sleepingBodies);
            m_sleepIndexDirty = false;
        }
        var found = m_sleepIndex.QueryBox(box: in box, destination: m_sleepQueries, margin: margin);

        m_fieldWakes.Clear();
        for (var ordinal = 0; (ordinal < found); ordinal++) {
            var index = m_sleepQueries[ordinal];

            if (FieldChangeReaches(box: in box, body: m_entries[index].Body!, margin: margin)) { m_fieldWakes.Add(item: index); }
        }
        m_fieldWakes.Sort();
        var before = m_sleepingBodies.Count;

        foreach (var index in m_fieldWakes) { WakeBodyIsland(index: index); }
        WorldBodyWork.Process.Add(amount: (before - m_sleepingBodies.Count), kind: WorldBodyWork.FieldWakes);
        WorldBodyWork.Process.Add(amount: (found - m_fieldWakes.Count), kind: WorldBodyWork.FieldObserved);
    }
    // How far past a sleeper's broadphase radius a change still reaches it: the farthest a contact acts (the tuning's
    // ContactReach) plus the travel of a rigid body's downward support probe (RefreshRigidStaticSupport), so a body a
    // moved collider or a changed lattice cell could touch or support is never left asleep.
    private FixedQ4816 FieldWakeMargin() => (m_contactReach + m_rigidContactPolicy.SubstepMinimumTravel);
    private static bool FieldChangeReaches(in WorldColliderBox box, WorldBody body, FixedQ4816 margin) =>
        box.Meets(position: body.FixedPosition, reach: (body.BroadphaseRadius + margin));
    // Freeze a complete resting contact component together. A supported stack cannot freeze above a body that
    // is still moving, and changing any member wakes the component before its support graph is discarded.
    private void ParkRigidIslands() {
        m_awakeBodies.CopyTo(array: m_stepBodies);
        var count = m_awakeBodies.Count;

        m_parkVisited.Clear();
        for (var ordinal = 0; (ordinal < count); ordinal++) {
            var root = m_stepBodies[ordinal];

            if (m_entries[root].Body is not { IsRigid: true }) { continue; }
            if (!m_parkVisited.Add(item: root)) { continue; }
            CollectRigidIsland(root: root);
            var resting = true;

            foreach (var member in m_island) {
                m_parkVisited.Add(item: member);
                resting &= (m_entries[member] is { Active: true, Body: { IsRigid: true, Resting: true, CarriedBy: null, PopulationSleeping: false } });
            }
            if (!resting) { continue; }
            var rootPlusOne = (m_island[0] + 1);

            foreach (var member in m_island) {
                ParkIslandMember(contactFieldVersion: m_sleepFieldVersion, index: member, rootPlusOne: rootPlusOne);
                m_sleepGravity[member] = SolvedSleepGravity(index: member);
            }
        }
    }
    // Rebuild the index and an equivalent star graph from canonical roots; adjacency within a frozen component
    // matters only when the whole component wakes. External supports and discovery work are retained explicitly.
    private void RestoreSleepingIslands(WorldPopulationCheckpoint checkpoint) {
        m_awakeBodies.Clear();
        m_sleepingBodies.Clear();
        foreach (var members in m_islandMembers.Values) {
            members.Clear();
            m_islandMemberPool.Push(item: members);
        }
        m_islandMembers.Clear();
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is { Active: true, Body: not null }) { m_awakeBodies.Add(item: index); }
        }
        m_sleepGravity.Clear();
        RebuildRigidContactReferrers();
        m_sleepFieldVersion = ContactFieldVersion;
        var pendingFieldWake = false;
        // Restore can retire remote humans whose connection cannot survive. A surviving part of their rigid
        // island has lost support, including when the retired member was the canonical root.
        var retiredRoots = new HashSet<int>();

        foreach (var captured in checkpoint.Entries) {
            if ((captured.Residue.RigidSleepRootPlusOne != 0) && (m_entries[captured.Index] is not { Active: true, Body: not null })) {
                retiredRoots.Add(item: captured.Residue.RigidSleepRootPlusOne);
            }
        }
        // Discovery eligibility affects bounded collision admission. Keep captured work and add the bodies that
        // roster reconciliation wakes below, rather than marking every reconstructed sleeper dirty.
        m_eventBodies.Clear();
        foreach (var index in checkpoint.EventBodies) { m_eventBodies.Add(item: index); }

        // Each surviving body parks under the root its captured residue names; the residue never writes it itself.
        foreach (var captured in checkpoint.Entries) {
            var rootPlusOne = captured.Residue.RigidSleepRootPlusOne;

            if ((rootPlusOne == 0) || (m_entries[captured.Index] is not { Active: true, Body: { } body })) { continue; }
            if (retiredRoots.Contains(item: rootPlusOne)) { body.WakeRigidIsland(); continue; }
            var observed = body.LastContactFieldVersion;

            ParkIslandMember(contactFieldVersion: observed, index: captured.Index, rootPlusOne: rootPlusOne);
            pendingFieldWake |= (observed != ContactFieldVersion);
        }
        RecordSleepFieldObservation(known: !pendingFieldWake);
        if (pendingFieldWake) { m_sleepFieldVersion = unchecked((ContactFieldVersion - 1UL)); }
        foreach (var row in checkpoint.SleepingGravity) {
            if (m_sleepingBodies.Contains(item: row.Body)) { m_sleepGravity.Add(key: row.Body, value: row.Acceleration); }
        }
        // A body whose contact partner did not survive the restore (a retired remote human) has lost that contact: the
        // contact is released, and a sleeper holding it wakes.
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is not { Active: true, Body: { } owner }) { continue; }
            var lost = false;

            for (var position = (owner.RigidContacts.Count - 1); (position >= 0); position--) {
                var partner = owner.RigidContacts[position].Partner;

                if ((partner >= 0) && (m_entries[partner] is not { Active: true, Body: not null })) {
                    owner.ReleaseRigidContactAt(position: position);
                    lost = true;
                }
            }
            if (lost && m_sleepingBodies.Contains(item: index)) { WakeBodyIsland(index: index); }
        }
        m_sleepIndexDirty = true;
    }

    // This is folded separately from body-entry bytes, which deliberately remain the entry codec's exact image.
    internal void AppendSleepStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_eventBodies.Count));
        foreach (var index in m_eventBodies) { hash.Add(value: index); }
        hash.Add(value: ((uint)m_sleepingBodies.Count));
        foreach (var index in m_sleepingBodies) {
            hash.Add(value: index);
            var acceleration = m_sleepGravity[index];

            hash.Add(value: ((byte)(acceleration.HasValue ? 1 : 0)));
            if (acceleration is { } vector) { hash.Add(value: vector.X.Value); hash.Add(value: vector.Y.Value); hash.Add(value: vector.Z.Value); }
        }
    }
    internal ReadOnlySpan<int> EventBodies() {
        m_eventBodies.CopyTo(array: m_eventBodyImage);
        return m_eventBodyImage.AsSpan(start: 0, length: m_eventBodies.Count);
    }
    internal void CompleteBodyEvents() {
        // Keep awake entries between ticks. Only the final dirty delivery of a newly frozen/deactivated slot leaves
        // the set; bodies that stay awake need no per-tick membership changes.
        foreach (var index in EventBodies()) {
            if (!m_awakeBodies.Contains(item: index)) { m_eventBodies.Remove(item: index); }
        }
    }
    /// <summary>Lists the sleepers whose broadphase bounds may meet a sphere about a body: every one whose centre lies
    /// within <paramref name="reach"/> plus its own broadphase radius on each axis, and perhaps a few more.</summary>
    /// <param name="body">The body whose position centres the query.</param>
    /// <param name="reach">The query radius, in metres: the body's broadphase radius, or the radius it sweeps this
    /// step, grown by whatever its pair test adds to the sleeper's own radius.</param>
    /// <param name="destination">The array the sleepers' population indices are written to.</param>
    /// <returns>The number of indices written.</returns>
    internal int NearbySleepingBodies(WorldBody body, FixedQ4816 reach, int[] destination) {
        if (m_sleepIndexDirty) {
            m_sleepIndex.Rebuild(population: this, sleeping: m_sleepingBodies);
            m_sleepIndexDirty = false;
        }
        return m_sleepIndex.Query(position: body.FixedPosition, radius: reach, destination: destination);
    }

    // A retained broadphase over frozen bounds. Only island membership changes rebuild it. Queries visit cached
    // bounds in neighbouring Y/Z cells, using binary X ranges; no sleeping body's object is read on an idle tick.
    private sealed class SleepingIndex {
        private readonly Dictionary<(long Y, long Z), List<(WorldSweepBody Bounds, FixedVector3 Position)>> m_cells = [];

        private long m_width = 2;
        private long m_maximumRadius;

        internal void Rebuild(WorldPopulation population, BodyWorkSet sleeping) {
            m_cells.Clear();
            m_maximumRadius = 0;
            foreach (var index in sleeping) { m_maximumRadius = Math.Max(val1: m_maximumRadius, val2: population.m_entries[index].Body!.BroadphaseRadius.Value); }
            m_width = Math.Max(val1: 2, val2: m_maximumRadius.AddSaturating(right: m_maximumRadius));
            foreach (var index in sleeping) {
                var body = population.m_entries[index].Body!;
                var position = body.FixedPosition;
                var radius = body.BroadphaseRadius;
                var key = (position.Y.Value.FloorDivide(divisor: m_width), position.Z.Value.FloorDivide(divisor: m_width));

                if (!m_cells.TryGetValue(key: key, value: out var cell)) { m_cells[key] = cell = []; }
                cell.Add(item: (new WorldSweepBody(Index: index, MinimumX: (position.X - radius), MaximumX: (position.X + radius), Radius: radius), position));
            }
            foreach (var cell in m_cells.Values) { cell.Sort(comparison: static (a, b) => a.Bounds.CompareTo(other: b.Bounds)); }
        }
        // The sleepers whose bounds, grown by the margin, may meet the box: every one that does, and perhaps a few that do
        // not, which the caller's exact test rejects. Cells are visited in key order and lists by X, so the candidates
        // are not in slot order.
        internal int QueryBox(in WorldColliderBox box, FixedQ4816 margin, int[] destination) {
            if (m_cells.Count == 0) { return 0; }
            var count = 0;

            if (box.Unbounded) {
                foreach (var pair in m_cells) {
                    foreach (var candidate in pair.Value) { destination[count++] = candidate.Bounds.Index; }
                }
                return count;
            }
            var reach = margin.Value.AddSaturating(right: m_maximumRadius);
            var firstY = box.Minimum.Y.Value.SubtractSaturating(right: reach).FloorDivide(divisor: m_width);
            var lastY = box.Maximum.Y.Value.AddSaturating(right: reach).FloorDivide(divisor: m_width);
            var firstZ = box.Minimum.Z.Value.SubtractSaturating(right: reach).FloorDivide(divisor: m_width);
            var lastZ = box.Maximum.Z.Value.AddSaturating(right: reach).FloorDivide(divisor: m_width);
            var minimum = box.Minimum.X.Value.SubtractSaturating(right: reach);
            var maximum = box.Maximum.X.Value.AddSaturating(right: reach);
            var range = (((((Int128)lastY) - firstY) + 1) * ((((Int128)lastZ) - firstZ) + 1));

            if (range > m_cells.Count) {
                foreach (var pair in m_cells) {
                    if ((pair.Key.Y >= firstY) && (pair.Key.Y <= lastY) && (pair.Key.Z >= firstZ) && (pair.Key.Z <= lastZ)) { Visit(cell: pair.Value); }
                }
            } else {
                for (var y = firstY; (y <= lastY); y++) {
                    for (var z = firstZ; (z <= lastZ); z++) {
                        if (m_cells.TryGetValue(key: (y, z), value: out var cell)) { Visit(cell: cell); }
                    }
                }
            }
            return count;

            void Visit(List<(WorldSweepBody Bounds, FixedVector3 Position)> cell) {
                var low = 0;
                var high = cell.Count;
                // A list is sorted by its bounds' least X; one whose least X lies past the box's grown reach misses it.
                var lowest = minimum.SubtractSaturating(right: m_width);

                while (low < high) {
                    var middle = (low + ((high - low) / 2));

                    if (cell[middle].Bounds.MinimumX.Value < lowest) { low = (middle + 1); } else { high = middle; }
                }
                for (var ordinal = low; ((ordinal < cell.Count) && (cell[ordinal].Bounds.MinimumX.Value <= maximum)); ordinal++) {
                    if (cell[ordinal].Bounds.MaximumX.Value >= minimum) { destination[count++] = cell[ordinal].Bounds.Index; }
                }
            }
        }
        internal int Query(FixedVector3 position, FixedQ4816 radius, int[] destination) {
            if (m_cells.Count == 0) { return 0; }
            var reach = radius.Value.AddSaturating(right: m_maximumRadius);
            var firstY = position.Y.Value.SubtractSaturating(right: reach).FloorDivide(divisor: m_width);
            var lastY = position.Y.Value.AddSaturating(right: reach).FloorDivide(divisor: m_width);
            var firstZ = position.Z.Value.SubtractSaturating(right: reach).FloorDivide(divisor: m_width);
            var lastZ = position.Z.Value.AddSaturating(right: reach).FloorDivide(divisor: m_width);
            var minimum = (position.X.Value.SubtractSaturating(right: radius.Value).SubtractSaturating(right: m_width));
            var maximum = position.X.Value.AddSaturating(right: radius.Value);
            var count = 0;
            // A large live body must not enumerate an unbounded empty lattice. The occupied-cell fallback has
            // the same exact candidates and subsequent slot/X sorting removes dictionary enumeration order.
            var range = (((((Int128)lastY) - firstY) + 1) * ((((Int128)lastZ) - firstZ) + 1));

            if (range > m_cells.Count) {
                foreach (var pair in m_cells) {
                    if ((pair.Key.Y >= firstY) && (pair.Key.Y <= lastY) && (pair.Key.Z >= firstZ) && (pair.Key.Z <= lastZ)) { Visit(cell: pair.Value); }
                }
            } else {
                for (var y = firstY; (y <= lastY); y++) {
                    for (var z = firstZ; (z <= lastZ); z++) {
                        if (m_cells.TryGetValue(key: (y, z), value: out var cell)) { Visit(cell: cell); }
                    }
                }
            }
            return count;

            void Visit(List<(WorldSweepBody Bounds, FixedVector3 Position)> cell) {
                var low = 0;
                var high = cell.Count;

                while (low < high) {
                    var middle = (low + ((high - low) / 2));

                    if (cell[middle].Bounds.MinimumX.Value < minimum) { low = (middle + 1); } else { high = middle; }
                }
                for (var ordinal = low; ((ordinal < cell.Count) && (cell[ordinal].Bounds.MinimumX.Value <= maximum)); ordinal++) {
                    var candidate = cell[ordinal];
                    var sum = (radius + candidate.Bounds.Radius);

                    if ((candidate.Bounds.MaximumX.Value >= position.X.Value.SubtractSaturating(right: radius.Value)) &&
                        (FixedQ4816.Abs(value: (candidate.Position.Y - position.Y)) <= sum) && (FixedQ4816.Abs(value: (candidate.Position.Z - position.Z)) <= sum)) {
                        destination[count++] = candidate.Bounds.Index;
                    }
                }
            }
        }
    }
    // Membership mutates only on admission, wake and sleep. Pay for ordered insertion there, rather than a tree
    // enumerator's traversal stack on every tick. Iteration borrows the array; callers that can wake bodies copy it.
    private sealed class BodyWorkSet(int capacity) {
        private readonly int[] m_indices = new int[capacity];
        private readonly bool[] m_present = new bool[capacity];

        internal int Count { get; private set; }
        internal int Min => m_indices[0];

        internal void Clear() { Array.Clear(array: m_present); Count = 0; }
        internal bool Contains(int item) => m_present[item];
        internal bool Add(int item) {
            if (m_present[item]) { return false; }
            var position = ~Array.BinarySearch(array: m_indices, index: 0, length: Count, value: item);

            Array.Copy(sourceArray: m_indices, sourceIndex: position, destinationArray: m_indices, destinationIndex: (position + 1), length: (Count - position));
            m_indices[position] = item;
            m_present[item] = true;
            Count++;
            return true;
        }
        internal bool Remove(int item) {
            if (!m_present[item]) { return false; }
            var position = Array.BinarySearch(array: m_indices, index: 0, length: Count, value: item);

            Count--;
            Array.Copy(sourceArray: m_indices, sourceIndex: (position + 1), destinationArray: m_indices, destinationIndex: position, length: (Count - position));
            m_present[item] = false;
            return true;
        }
        internal void CopyTo(int[] array) => m_indices.AsSpan(start: 0, length: Count).CopyTo(destination: array);
        internal int[] ToArray() => m_indices.AsSpan(start: 0, length: Count).ToArray();

        public ReadOnlySpan<int>.Enumerator GetEnumerator() => ((ReadOnlySpan<int>)m_indices.AsSpan(start: 0, length: Count)).GetEnumerator();
    }
}
