using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Derived work sets. Slot order is the authority order, independent of insertion or wake order. They are
    // rebuilt during restore. Canonical frozen-component membership, external support/gravity observations and
    // pending event eligibility are continuation state; spatial bounds and internal rigid adjacency are derived.
    private readonly BodyWorkSet m_awakeBodies;
    private readonly BodyWorkSet m_sleepingBodies;
    private readonly BodyWorkSet m_eventBodies;

    private readonly Dictionary<int, SortedSet<int>> m_rigidNeighbours = [];
    // Non-rigid supports keep integrating. Their changes wake dependent rigid components without parking the
    // support itself. These edges, unlike adjacency within one rigid island, cannot be reconstructed from roots.
    private readonly Dictionary<int, SortedSet<int>> m_nonRigidSupports = [];
    private readonly Dictionary<int, FixedVector3?> m_sleepGravity = [];
    private readonly List<int> m_island = [];
    private readonly HashSet<int> m_islandVisited = [];

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

    private void BodyEntryChanged(int index) {
        WakeBodyIsland(index: index);
        m_eventBodies.Add(item: index);
        if (m_entries[index] is { Active: true, Body: not null }) { m_awakeBodies.Add(item: index); } else { m_awakeBodies.Remove(item: index); }
    }
    private void BodyWoke(int index) {
        WakeBodyIsland(index: index);
        m_eventBodies.Add(item: index);
        if (m_entries[index] is { Active: true, Body: not null }) { m_awakeBodies.Add(item: index); }
    }
    private void WakeBodyIsland(int index) {
        if (m_wakingIsland) { return; }
        if (!m_sleepingBodies.Contains(item: index)) {
            if (m_nonRigidSupports.Remove(key: index, value: out var loads)) {
                foreach (var load in loads) { WakeBodyIsland(index: load); }
            }
            return;
        }
        m_wakingIsland = true;
        m_island.Clear();
        m_islandVisited.Clear();
        m_island.Add(item: index);
        m_islandVisited.Add(item: index);
        for (var cursor = 0; (cursor < m_island.Count); cursor++) {
            var member = m_island[cursor];

            if (m_rigidNeighbours.TryGetValue(key: member, value: out var neighbours)) {
                foreach (var neighbour in neighbours) {
                    if (m_islandVisited.Add(item: neighbour)) { m_island.Add(item: neighbour); }
                }
            }
        }
        m_island.Sort();
        foreach (var member in m_island) {
            if (m_sleepingBodies.Remove(item: member)) {
                m_sleepGravity.Remove(key: member);
                m_sleepIndexDirty = true;
                if (m_entries[member] is { Active: true, Body: { } body }) {
                    body.WakeRigidIsland();
                    m_awakeBodies.Add(item: member);
                    m_eventBodies.Add(item: member);
                }
            }
        }
        // Every edge of the woken component will be derived again from actual contacts this tick.
        foreach (var member in m_island) { m_rigidNeighbours.Remove(key: member); }
        m_wakingIsland = false;
    }
    private void LinkRigidBodies(int left, int right) {
        if (!m_entries[left].Body!.IsRigid || !m_entries[right].Body!.IsRigid) {
            var support = (m_entries[left].Body!.IsRigid ? right : left);
            var load = ((support == left) ? right : left);

            if (!m_nonRigidSupports.TryGetValue(key: support, value: out var loads)) { m_nonRigidSupports[support] = loads = []; }
            loads.Add(item: load);
            return;
        }
        if (!m_rigidNeighbours.TryGetValue(key: left, value: out var a)) { m_rigidNeighbours[left] = a = []; }
        if (!m_rigidNeighbours.TryGetValue(key: right, value: out var b)) { m_rigidNeighbours[right] = b = []; }
        a.Add(item: right);
        b.Add(item: left);
    }
    private ReadOnlySpan<int> PrepareBodyStep() {
        ObserveContactFieldChange();
        m_awakeBodies.CopyTo(array: m_stepBodies);
        foreach (var index in m_awakeBodies) { m_eventBodies.Add(item: index); }
        foreach (var index in m_awakeBodies) { m_rigidNeighbours.Remove(key: index); }
        foreach (var loads in m_nonRigidSupports.Values) {
            foreach (var index in m_awakeBodies) { loads.Remove(item: index); }
        }
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
                WakeSleepersNear(box: in box, contactFieldVersion: version, margin: FieldWakeMargin());
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
    // Wakes, island by island in slot order, every sleeper whose bounds grown by the margin meet the box; every other
    // sleeper observes the change, since nothing it could reach changed.
    private void WakeSleepersNear(in WorldColliderBox box, ulong contactFieldVersion, FixedQ4816 margin) {
        var before = m_sleepingBodies.Count;

        m_fieldWakes.Clear();
        foreach (var index in m_sleepingBodies) {
            if (FieldChangeReaches(box: in box, body: m_entries[index].Body!, margin: margin)) { m_fieldWakes.Add(item: index); }
        }
        foreach (var index in m_fieldWakes) { WakeBodyIsland(index: index); }
        ObserveFieldChange(contactFieldVersion: contactFieldVersion, sleepersBefore: before);
    }
    // How far past a sleeper's broadphase radius a change still reaches it: the farthest a contact acts (the tuning's
    // ContactReach) plus the travel of a rigid body's downward support probe (RefreshRigidStaticSupport), so a body a
    // moved collider or a changed lattice cell could touch or support is never left asleep.
    private FixedQ4816 FieldWakeMargin() => (m_contactReach + m_rigidContactPolicy.SubstepMinimumTravel);
    private static bool FieldChangeReaches(in WorldColliderBox box, WorldBody body, FixedQ4816 margin) =>
        box.Meets(position: body.FixedPosition, reach: (body.BroadphaseRadius + margin));
    // Every sleeper still parked observes the change, and the counts record what it woke and what it left asleep.
    private void ObserveFieldChange(ulong contactFieldVersion, int sleepersBefore) {
        foreach (var index in m_sleepingBodies) { m_entries[index].Body!.ObserveContactField(contactFieldVersion: contactFieldVersion); }
        WorldBodyWork.Process.Add(amount: (sleepersBefore - m_sleepingBodies.Count), kind: WorldBodyWork.FieldWakes);
        WorldBodyWork.Process.Add(amount: m_sleepingBodies.Count, kind: WorldBodyWork.FieldObserved);
    }
    // Freeze a complete resting contact component together. A supported stack cannot freeze above a body that
    // is still moving, and changing any member wakes the component before its support graph is discarded.
    private void ParkRigidIslands() {
        m_islandVisited.Clear();
        m_awakeBodies.CopyTo(array: m_stepBodies);
        var count = m_awakeBodies.Count;

        for (var ordinal = 0; (ordinal < count); ordinal++) {
            var root = m_stepBodies[ordinal];

            if (m_entries[root].Body is not { IsRigid: true }) { continue; }
            if (!m_islandVisited.Add(item: root)) { continue; }
            m_island.Clear();
            m_island.Add(item: root);
            var resting = true;

            for (var cursor = 0; (cursor < m_island.Count); cursor++) {
                var member = m_island[cursor];

                resting &= (m_entries[member] is { Active: true, Body: { IsRigid: true, Resting: true, CarriedBy: null } });
                if (m_rigidNeighbours.TryGetValue(key: member, value: out var neighbours)) {
                    foreach (var neighbour in neighbours) {
                        if (m_islandVisited.Add(item: neighbour)) { m_island.Add(item: neighbour); }
                    }
                }
            }
            if (!resting) { continue; }
            m_island.Sort();
            var rootPlusOne = (m_island[0] + 1);

            foreach (var member in m_island) {
                m_entries[member].Body!.ParkRigidIsland(contactFieldVersion: m_sleepFieldVersion, rootPlusOne: rootPlusOne);
                m_awakeBodies.Remove(item: member);
                m_sleepingBodies.Add(item: member);
                m_sleepGravity[member] = (((m_gravityField is { } gravity) && gravity.TryAcceleration(acceleration: out var acceleration, entityIndex: member))
                    ? acceleration : null);
            }
            m_sleepIndexDirty = true;
        }
    }
    // Rebuild the index and an equivalent star graph from canonical roots; adjacency within a frozen component
    // matters only when the whole component wakes. External supports and discovery work are retained explicitly.
    private void RestoreSleepingIslands(WorldPopulationCheckpoint checkpoint) {
        m_awakeBodies.Clear();
        m_sleepingBodies.Clear();
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is { Active: true, Body: not null }) { m_awakeBodies.Add(item: index); }
        }
        m_rigidNeighbours.Clear();
        m_nonRigidSupports.Clear();
        m_sleepGravity.Clear();
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

        foreach (var index in m_awakeBodies.ToArray()) {
            var body = m_entries[index].Body!;
            var residue = body.CaptureIntegrationResidue();
            var root = (residue.RigidSleepRootPlusOne - 1);

            if (root < 0) { continue; }
            if (retiredRoots.Contains(item: (root + 1))) { body.WakeRigidIsland(); continue; }
            if (root != index) { LinkRigidBodies(left: root, right: index); }
            body.ParkRigidIsland(rootPlusOne: (root + 1), contactFieldVersion: residue.LastContactFieldVersion);
            pendingFieldWake |= (residue.LastContactFieldVersion != ContactFieldVersion);
            m_awakeBodies.Remove(item: index);
            m_sleepingBodies.Add(item: index);
        }
        RecordSleepFieldObservation(known: !pendingFieldWake);
        if (pendingFieldWake) { m_sleepFieldVersion = unchecked((ContactFieldVersion - 1UL)); }
        foreach (var row in checkpoint.SleepingGravity) {
            if (m_sleepingBodies.Contains(item: row.Body)) { m_sleepGravity.Add(key: row.Body, value: row.Acceleration); }
        }
        foreach (var edge in checkpoint.SleepingSupports) {
            if (!m_sleepingBodies.Contains(item: edge.Body)) { continue; }
            if (m_entries[edge.Support] is { Active: true, Body: not null }) { LinkRigidBodies(left: edge.Support, right: edge.Body); } else { WakeBodyIsland(index: edge.Body); }
        }
        m_sleepIndexDirty = true;
    }
    private (int Support, int Body)[] CaptureSleepingSupports() => [.. m_nonRigidSupports
        .OrderBy(keySelector: static pair => pair.Key)
        .SelectMany(selector: pair => pair.Value.Where(predicate: body => m_sleepingBodies.Contains(item: body))
            .Select(selector: body => (pair.Key, body)))];

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
        var supportCount = 0;

        foreach (var loads in m_nonRigidSupports.Values) {
            foreach (var load in loads) { if (m_sleepingBodies.Contains(item: load)) { supportCount++; } }
        }
        hash.Add(value: supportCount);
        foreach (var index in m_awakeBodies) {
            if (!m_nonRigidSupports.TryGetValue(key: index, value: out var loads)) { continue; }
            foreach (var load in loads) {
                if (m_sleepingBodies.Contains(item: load)) { hash.Add(value: index); hash.Add(value: load); }
            }
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
    internal int NearbySleepingBodies(WorldBody body, int[] destination) {
        if (m_sleepIndexDirty) {
            m_sleepIndex.Rebuild(population: this, sleeping: m_sleepingBodies);
            m_sleepIndexDirty = false;
        }
        return m_sleepIndex.Query(body: body, destination: destination);
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
        internal int Query(WorldBody body, int[] destination) {
            if (m_cells.Count == 0) { return 0; }
            var position = body.FixedPosition;
            var radius = body.BroadphaseRadius;
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
