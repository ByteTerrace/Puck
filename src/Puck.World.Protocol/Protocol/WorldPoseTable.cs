using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Collections;
using Puck.Abstractions.Memory;

namespace Puck.World.Protocol;

/// <summary>What <see cref="WorldPoseTable.TryAdmit"/> did to an entity's interpolation endpoints.</summary>
public enum WorldPoseStep : byte {
    /// <summary>The entity became active or teleported: both endpoints now hold the fresh pose, so the next frame never
    /// streaks, and the pose epoch moved.</summary>
    Seeded,

    /// <summary>The previous endpoint took the old current pose: ordinary double-buffering.</summary>
    Interpolated,

    /// <summary>An authority correction under an easing consumer: the previous endpoint holds the fresh pose, so the
    /// caller eases the old render error out itself (<see cref="WorldPoseAdmission"/> carries the pose it eases from).</summary>
    Corrected,

    /// <summary>An in-place crossing: the previous endpoint rode the crossing and
    /// <see cref="WorldPoseTable.Carries"/> recorded it.</summary>
    Carried,
}
/// <summary>The outcome of admitting one entry: the step taken and the pose the entity held before it.</summary>
/// <param name="Step">What the entry did to the interpolation endpoints.</param>
/// <param name="PriorPosition">The current position before the entry replaced it.</param>
/// <param name="PriorOrientation">The current attitude before the entry replaced it.</param>
public readonly record struct WorldPoseAdmission(WorldPoseStep Step, Vector3 PriorPosition, Quaternion PriorOrientation);
/// <summary>
/// The per-entity pose bookkeeping every snapshot consumer keeps: the previous/current interpolation endpoints, the
/// active flag, the placement an entity inhabits, the pose-discontinuity epoch and the in-place crossings, driven by a
/// snapshot's per-entry continuity hint, plus the slots one snapshot admitted and the slots the last one left live so
/// the next retires exactly those it no longer carries. <c>WorldClient</c> and <c>WorldSessionMirror</c> both
/// deliver snapshots through it, each keeping its own extras (the client's correction easers, the mirror's seeded
/// route) outside.
/// </summary>
/// <remarks>
/// <para>Invariant: a slot's active flag is set only by <see cref="TryAdmit"/> (after adding it to the admitted set) or
/// <see cref="Seed"/> (after adding it to the live set), and cleared only by <see cref="Complete"/>, which visits the
/// live set alone; every slot that may be active is therefore in the live set between deliveries. Enforced by this
/// type holding the arrays privately.</para>
/// <para>The table grows with the world: an entry naming a slot past <see cref="Capacity"/> grows every column first,
/// so storage follows the highest body a delivery names, never a fixed slot count. The columns are replaced whole and
/// published by reference, so a reader on another thread reads either the old columns or the grown ones, each
/// complete; every reader bounds its index against the columns it read and answers a slot past them as inactive.</para>
/// <para>Not thread-safe against concurrent writers: the caller serializes <see cref="Begin"/>, <see cref="TryAdmit"/>,
/// <see cref="Seed"/>, <see cref="EnsureCapacity"/> and <see cref="Complete"/>. The active flag publishes with
/// release/acquire semantics, so a reader on another thread that sees an entity active sees the endpoints written
/// before it.</para>
/// </remarks>
public sealed class WorldPoseTable {
    private readonly MarkedIndexSet m_live = new();
    private readonly MarkedIndexSet m_seen = new();
    private Columns m_columns;
    // Each inhabited placement's lowest active entity index, rebuilt by the writer whenever a slot's placement or its
    // activity as an inhabitant moved, and published whole, so a reader on another thread reads one consistent map.
    private Dictionary<string, int> m_inhabitants = new(comparer: StringComparer.Ordinal);
    private bool m_inhabitantsMoved;

    // Every per-entity column at one length. The writer fills a grown copy before publishing it.
    private sealed class Columns {
        public Columns(int length) {
            Active = new bool[length];
            CurrentOrientation = new Quaternion[length];
            CurrentPosition = new Vector3[length];
            PlacementId = new string?[length];
            PoseEpoch = new int[length];
            PreviousOrientation = new Quaternion[length];
            PreviousPosition = new Vector3[length];
            CurrentOrientation.AsSpan().Fill(value: Quaternion.Identity);
            PreviousOrientation.AsSpan().Fill(value: Quaternion.Identity);
        }

        public readonly bool[] Active;
        public readonly Quaternion[] CurrentOrientation;
        public readonly Vector3[] CurrentPosition;
        public readonly string?[] PlacementId;
        public readonly int[] PoseEpoch;
        public readonly Quaternion[] PreviousOrientation;
        public readonly Vector3[] PreviousPosition;

        public int Length => Active.Length;

        public Columns Grown(int length) {
            var grown = new Columns(length: length);

            Active.CopyTo(array: grown.Active, index: 0);
            CurrentOrientation.CopyTo(array: grown.CurrentOrientation, index: 0);
            CurrentPosition.CopyTo(array: grown.CurrentPosition, index: 0);
            PlacementId.CopyTo(array: grown.PlacementId, index: 0);
            PoseEpoch.CopyTo(array: grown.PoseEpoch, index: 0);
            PreviousOrientation.CopyTo(array: grown.PreviousOrientation, index: 0);
            PreviousPosition.CopyTo(array: grown.PreviousPosition, index: 0);

            return grown;
        }
    }

    /// <summary>Initializes an empty table.</summary>
    /// <param name="capacity">The number of entity indices tracked before the first delivery grows the table; zero
    /// allocates nothing until an entity arrives.</param>
    public WorldPoseTable(int capacity = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);

        Carries = new WorldPoseCarries(capacity: capacity);
        m_columns = new Columns(length: capacity);
    }

    /// <summary>Gets the number of entity indices tracked: the highest index a delivery has named, rounded up as the
    /// table grows, never more than <see cref="WorldBodiesLimits.IndexLimit"/>.</summary>
    public int Capacity => Volatile.Read(location: ref m_columns).Length;
    /// <summary>Gets the bytes the table's columns retain, excluding array headers and the placement strings.</summary>
    public long RetainedBytes => ((long)Capacity * (sizeof(bool) + sizeof(int) + (2 * Unsafe.SizeOf<Quaternion>()) + (2 * Unsafe.SizeOf<Vector3>()) + IntPtr.Size));
    /// <summary>Gets the in-place crossings each entity has made, which a consumer that latched an entity's world-space
    /// pose carries it through.</summary>
    public WorldPoseCarries Carries { get; }
    /// <summary>Gets the slots the delivery in progress has admitted, in admission order; valid until the next
    /// <see cref="Begin"/> or <see cref="TryAdmit"/>.</summary>
    public ReadOnlySpan<int> Seen => m_seen.Members;
    /// <summary>Gets every slot that can be active once a delivery has completed: the slots the last
    /// <see cref="Complete"/> left live and those <see cref="Seed"/> published since. A consumer that resolves a pose
    /// for each active entity walks these and never the whole <see cref="Capacity"/>; valid until the next
    /// <see cref="Complete"/> or <see cref="Seed"/>.</summary>
    public ReadOnlySpan<int> Live => m_live.Members;
    /// <summary>Gets whether the last <see cref="Complete"/> changed which slots are active: it admitted a slot the
    /// delivery before had not left live, or it left one inactive.</summary>
    public bool ActiveChanged { get; private set; }

    /// <summary>Grows every column to hold at least <paramref name="count"/> entity indices, keeping each entity's
    /// record; a smaller count changes nothing.</summary>
    /// <param name="count">The number of entity indices to hold, at most <see cref="WorldBodiesLimits.IndexLimit"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or past the body-index format
    /// bound.</exception>
    public void EnsureCapacity(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: count, other: WorldBodiesLimits.IndexLimit);

        var columns = m_columns;

        if (count <= columns.Length) { return; }

        var length = ArrayCapacity.Grown(current: columns.Length, required: count, ceiling: WorldBodiesLimits.IndexLimit);

        Carries.EnsureCapacity(count: length);
        Volatile.Write(location: ref m_columns, value: columns.Grown(length: length));
    }
    /// <summary>Returns whether the delivery in progress has admitted a slot.</summary>
    /// <param name="index">The 0-based entity index; one outside <see cref="Capacity"/> was never admitted.</param>
    /// <returns><see langword="true"/> when <see cref="TryAdmit"/> admitted the slot since <see cref="Begin"/>.</returns>
    public bool Admitted(int index) => m_seen.Contains(index: index);
    /// <summary>Starts a delivery: nothing is admitted yet.</summary>
    public void Begin() => m_seen.Clear();
    /// <summary>Gets an entity's current attitude (the later interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index; one past <see cref="Capacity"/> reads the identity.</param>
    public Quaternion CurrentOrientation(int index) {
        var columns = Volatile.Read(location: ref m_columns);

        return ((((uint)index) < ((uint)columns.Length)) ? columns.CurrentOrientation[index] : Quaternion.Identity);
    }
    /// <summary>Gets an entity's current position (the later interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index; one past <see cref="Capacity"/> reads the origin.</param>
    public Vector3 CurrentPosition(int index) {
        var columns = Volatile.Read(location: ref m_columns);

        return ((((uint)index) < ((uint)columns.Length)) ? columns.CurrentPosition[index] : Vector3.Zero);
    }
    /// <summary>Ends a delivery: every slot the last delivery left live that this one did not admit goes inactive and
    /// forgets its placement, and the admitted slots become the live set.</summary>
    /// <param name="preserved">A slot to keep live and active though this delivery did not admit it (a seeded
    /// route not yet in an ordinary snapshot), or a negative index for none.</param>
    public void Complete(int preserved = -1) {
        var activeChanged = false;
        var columns = m_columns;

        foreach (var index in m_live.Members) {
            if (!m_seen.Contains(index: index) && (index != preserved)) {
                Volatile.Write(
                    location: ref columns.Active[index],
                    value: false
                );
                m_inhabitantsMoved |= (columns.PlacementId[index] is not null);
                columns.PlacementId[index] = null;
                activeChanged = true;
            }
        }
        foreach (var index in m_seen.Members) {
            activeChanged |= !m_live.Contains(index: index);
        }

        ActiveChanged = activeChanged;
        m_live.Clear();
        foreach (var index in m_seen.Members) { _ = m_live.Add(index: index); }
        if (preserved >= 0) { _ = m_live.Add(index: preserved); }
        if (m_inhabitantsMoved) { PublishInhabitants(); }
    }
    // Every slot that may be active is in the live set or the admitted set (the type's invariant), so walking both
    // finds each placement's lowest active inhabitant without visiting the whole capacity.
    private void PublishInhabitants() {
        var inhabitants = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var columns = m_columns;

        Collect(members: m_live.Members);
        Collect(members: m_seen.Members);
        Volatile.Write(location: ref m_inhabitants, value: inhabitants);
        m_inhabitantsMoved = false;

        void Collect(ReadOnlySpan<int> members) {
            foreach (var index in members) {
                if (
                    (columns.PlacementId[index] is { } placementId) &&
                    Volatile.Read(location: ref columns.Active[index]) &&
                    (!inhabitants.TryGetValue(key: placementId, value: out var lowest) || (index < lowest))
                ) {
                    inhabitants[placementId] = index;
                }
            }
        }
    }
    /// <summary>Records a discontinuity the consumer declared itself (an over-threshold correction it snapped), which
    /// a presentation-side follower reseeds across.</summary>
    /// <param name="index">The 0-based entity index, within <see cref="Capacity"/>.</param>
    public void Discontinue(int index) => m_columns.PoseEpoch[index]++;
    /// <summary>Gets whether an entity was active (drawn) in the latest delivery.</summary>
    /// <param name="index">The 0-based entity index; one past <see cref="Capacity"/> is inactive.</param>
    public bool IsActive(int index) {
        var columns = Volatile.Read(location: ref m_columns);

        return ((((uint)index) < ((uint)columns.Length)) && Volatile.Read(location: ref columns.Active[index]));
    }
    /// <summary>Gets the placement an entity inhabits, or <see langword="null"/> for none.</summary>
    /// <param name="index">The 0-based entity index; one past <see cref="Capacity"/> inhabits none.</param>
    public string? PlacementId(int index) {
        var columns = Volatile.Read(location: ref m_columns);

        return ((((uint)index) < ((uint)columns.Length)) ? columns.PlacementId[index] : null);
    }
    /// <summary>Gets an entity's pose-discontinuity counter: it moves on activation, a teleport and each
    /// <see cref="Discontinue"/>.</summary>
    /// <param name="index">The 0-based entity index; one past <see cref="Capacity"/> reads zero.</param>
    public int PoseEpoch(int index) {
        var columns = Volatile.Read(location: ref m_columns);

        return ((((uint)index) < ((uint)columns.Length)) ? columns.PoseEpoch[index] : 0);
    }
    /// <summary>Resolves the lowest active entity index that inhabits a placement, as of the last
    /// <see cref="Complete"/> or <see cref="Seed"/>.</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="index">The entity index, or <c>-1</c> when none inhabits it.</param>
    /// <returns><see langword="true"/> when an active entity inhabits the placement.</returns>
    /// <remarks>Reads the inhabitant map the writer last published, then confirms the slot still holds the placement
    /// and is active, so a reader racing a delivery in progress answers none rather than a slot that delivery has
    /// already moved.</remarks>
    public bool TryInhabitant(string placementId, out int index) {
        if (
            Volatile.Read(location: ref m_inhabitants).TryGetValue(key: placementId, value: out index) &&
            IsActive(index: index) &&
            string.Equals(
                a: PlacementId(index: index),
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            return true;
        }

        index = -1;

        return false;
    }
    /// <summary>Gets an entity's previous attitude (the earlier interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index; one past <see cref="Capacity"/> reads the identity.</param>
    public Quaternion PreviousOrientation(int index) {
        var columns = Volatile.Read(location: ref m_columns);

        return ((((uint)index) < ((uint)columns.Length)) ? columns.PreviousOrientation[index] : Quaternion.Identity);
    }
    /// <summary>Gets an entity's previous position (the earlier interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index; one past <see cref="Capacity"/> reads the origin.</param>
    public Vector3 PreviousPosition(int index) {
        var columns = Volatile.Read(location: ref m_columns);

        return ((((uint)index) < ((uint)columns.Length)) ? columns.PreviousPosition[index] : Vector3.Zero);
    }
    /// <summary>Publishes an exact pose for an entity outside any snapshot (a committed route head): both endpoints
    /// hold it, the slot becomes live and active, and the epoch does not move.</summary>
    /// <param name="index">The 0-based entity index, below <see cref="WorldBodiesLimits.IndexLimit"/>; the table grows
    /// to hold it.</param>
    /// <param name="position">The position.</param>
    /// <param name="orientation">The attitude.</param>
    public void Seed(int index, Vector3 position, Quaternion orientation) {
        EnsureCapacity(count: (index + 1));

        var columns = m_columns;

        columns.PreviousPosition[index] = position;
        columns.CurrentPosition[index] = position;
        columns.PreviousOrientation[index] = orientation;
        columns.CurrentOrientation[index] = orientation;
        _ = m_live.Add(index: index);
        Volatile.Write(
            location: ref columns.Active[index],
            value: true
        );
        if (m_inhabitantsMoved || (columns.PlacementId[index] is not null)) { PublishInhabitants(); }
    }
    /// <summary>Admits one snapshot entry: grows the table to hold its slot, adds the slot to the admitted set and moves
    /// its endpoints, placement and active flag by its continuity hint. The active flag is written last, so the caller
    /// writes every other field it keeps for the entry before calling.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="easeCorrections">Whether the consumer eases a <see cref="EntityContinuityKind.Correction"/>'s render
    /// error out itself, in which case the previous endpoint snaps to the fresh pose and the step is
    /// <see cref="WorldPoseStep.Corrected"/>; otherwise a correction is ordinary double-buffering.</param>
    /// <param name="admission">The step taken and the pose the entity held before it.</param>
    /// <returns><see langword="false"/> when the entry's index is not a body index
    /// (<see cref="WorldBodiesLimits.IsBodyIndex"/>) and nothing changed.</returns>
    public bool TryAdmit(in EntitySnapshot entry, bool easeCorrections, out WorldPoseAdmission admission) {
        var index = entry.Index;

        if (!WorldBodiesLimits.IsBodyIndex(index: index)) {
            admission = default;

            return false;
        }

        EnsureCapacity(count: (index + 1));

        var columns = m_columns;
        var priorOrientation = columns.CurrentOrientation[index];
        var priorPosition = columns.CurrentPosition[index];
        var step = WorldPoseStep.Interpolated;

        _ = m_seen.Add(index: index);
        m_inhabitantsMoved |= (
            !string.Equals(a: columns.PlacementId[index], b: entry.PlacementId, comparisonType: StringComparison.Ordinal) ||
            ((entry.PlacementId is not null) && (Volatile.Read(location: ref columns.Active[index]) != entry.Active))
        );
        columns.PlacementId[index] = entry.PlacementId;

        if (
            !Volatile.Read(location: ref columns.Active[index]) ||
            (entry.Continuity.Kind == EntityContinuityKind.Teleport)
        ) {
            // Never interpolate across a jump; the consumer drops any in-flight correction offset with it.
            columns.PreviousPosition[index] = entry.Position;
            columns.PreviousOrientation[index] = entry.Orientation;
            columns.PoseEpoch[index]++;
            step = WorldPoseStep.Seeded;
        } else if (entry.Continuity is { Kind: EntityContinuityKind.Carry, Carried: { } carried }) {
            // An in-place crossing: the previous pose rides the crossing, so the frame interpolates on from where the
            // body was, carried, and every consumer that latched the body's pose carries it the same way (Carries).
            columns.PreviousPosition[index] = carried.Point(point: priorPosition);
            columns.PreviousOrientation[index] = carried.Attitude(attitude: priorOrientation);
            Carries.Record(
                carry: carried,
                index: index
            );
            step = WorldPoseStep.Carried;
        } else if (easeCorrections && (entry.Continuity.Kind == EntityContinuityKind.Correction)) {
            columns.PreviousPosition[index] = entry.Position;
            columns.PreviousOrientation[index] = entry.Orientation;
            step = WorldPoseStep.Corrected;
        } else {
            columns.PreviousPosition[index] = priorPosition;
            columns.PreviousOrientation[index] = priorOrientation;
        }

        columns.CurrentPosition[index] = entry.Position;
        columns.CurrentOrientation[index] = entry.Orientation;
        // The release write publishes every endpoint above to a reader on another thread (a federated observer
        // delivers on its socket task); IsActive's acquire read makes the completed record visible.
        Volatile.Write(
            location: ref columns.Active[index],
            value: entry.Active
        );
        admission = new WorldPoseAdmission(
            PriorOrientation: priorOrientation,
            PriorPosition: priorPosition,
            Step: step
        );

        return true;
    }
}
