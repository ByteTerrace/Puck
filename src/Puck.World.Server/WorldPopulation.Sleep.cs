using Puck.Maths;
using Puck.Physics;

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

    // A retained broadphase over frozen bounds, rebuilt only when island membership changes, so no sleeping body's
    // object is read on an idle tick.
    private readonly FixedSweepGrid m_sleepIndex;
    private readonly FixedSweepSphere[] m_sleepSpheres;
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
    /// <summary>Parks a body a scoped replay restored under the island root its captured clocks name, as the
    /// checkpoint restore does for a whole population (<see cref="RestoreSleepingIslands"/>): a section restore never
    /// writes island membership itself.</summary>
    /// <param name="index">The body's population index.</param>
    /// <param name="rootPlusOne">The captured island root plus one; zero leaves the body awake.</param>
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
        var found = (box.Any ? SleepIndex().Query(box: box.Box.Grow(margin: margin), destination: m_sleepQueries) : 0);

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
            if ((captured.Body.Clocks.RigidSleepRootPlusOne != 0) && (m_entries[captured.Index] is not { Active: true, Body: not null })) {
                retiredRoots.Add(item: captured.Body.Clocks.RigidSleepRootPlusOne);
            }
        }
        // Discovery eligibility affects bounded collision admission. Keep captured work and add the bodies that
        // roster reconciliation wakes below, rather than marking every reconstructed sleeper dirty.
        m_eventBodies.Clear();
        foreach (var index in checkpoint.EventBodies) { m_eventBodies.Add(item: index); }

        // Each surviving body parks under the root its captured clocks name; a section restore never writes it itself.
        foreach (var captured in checkpoint.Entries) {
            var rootPlusOne = captured.Body.Clocks.RigidSleepRootPlusOne;

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

    // Folded beside the slots' digests: the event set and each parked body's solved gravity are the population's own.
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
    /// <summary>Lists the sleepers whose broadphase bounds meet a sphere's about a body: every one whose centre lies
    /// within <paramref name="reach"/> plus its own broadphase radius on each axis.</summary>
    /// <param name="body">The body whose position centres the query.</param>
    /// <param name="reach">The query radius, in metres: the body's broadphase radius, or the radius it sweeps this
    /// step, grown by whatever its pair test adds to the sleeper's own radius.</param>
    /// <param name="destination">The array the sleepers' population indices are written to.</param>
    /// <returns>The number of indices written.</returns>
    internal int NearbySleepingBodies(WorldBody body, FixedQ4816 reach, int[] destination) =>
        SleepIndex().Query(box: FixedAabb.Around(center: body.FixedPosition, radius: reach), destination: destination);
    // The sleeping index, rebuilt from the sleepers' bounds when island membership has changed since it was last read.
    private FixedSweepGrid SleepIndex() {
        if (m_sleepIndexDirty) {
            var count = 0;

            foreach (var index in m_sleepingBodies) {
                var body = m_entries[index].Body!;

                m_sleepSpheres[count++] = new FixedSweepSphere(Index: index, Center: body.FixedPosition, Radius: body.BroadphaseRadius);
            }
            m_sleepIndex.Rebuild(spheres: m_sleepSpheres.AsSpan(start: 0, length: count));
            m_sleepIndexDirty = false;
        }
        return m_sleepIndex;
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
