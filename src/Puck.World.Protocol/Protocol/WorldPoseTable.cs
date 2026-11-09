using System.Numerics;
using Puck.Abstractions.Collections;

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
/// <para>Not thread-safe against concurrent writers: the caller serializes <see cref="Begin"/>, <see cref="TryAdmit"/>,
/// <see cref="Seed"/> and <see cref="Complete"/>. The active flag publishes with release/acquire semantics, so a
/// reader on another thread that sees an entity active sees the endpoints written before it.</para>
/// </remarks>
public sealed class WorldPoseTable {
    private readonly MarkedIndexSet m_live = new();
    private readonly MarkedIndexSet m_seen = new();
    private readonly bool[] m_active;
    private readonly int[] m_poseEpoch;
    private readonly string?[] m_placementId;
    private readonly Quaternion[] m_currentOrientation;
    private readonly Quaternion[] m_previousOrientation;
    private readonly Vector3[] m_currentPosition;
    private readonly Vector3[] m_previousPosition;

    /// <summary>Initializes an empty table.</summary>
    /// <param name="capacity">The number of entity indices tracked.</param>
    public WorldPoseTable(int capacity) {
        Capacity = capacity;
        Carries = new WorldPoseCarries(capacity: capacity);
        m_active = new bool[capacity];
        m_currentOrientation = new Quaternion[capacity];
        m_currentPosition = new Vector3[capacity];
        m_placementId = new string?[capacity];
        m_poseEpoch = new int[capacity];
        m_previousOrientation = new Quaternion[capacity];
        m_previousPosition = new Vector3[capacity];

        for (var index = 0; (index < capacity); index++) {
            m_currentOrientation[index] = Quaternion.Identity;
            m_previousOrientation[index] = Quaternion.Identity;
        }
    }

    /// <summary>Gets the number of entity indices tracked.</summary>
    public int Capacity { get; }
    /// <summary>Gets the in-place crossings each entity has made, which a consumer that latched an entity's world-space
    /// pose carries it through.</summary>
    public WorldPoseCarries Carries { get; }
    /// <summary>Gets the slots the delivery in progress has admitted, in admission order; valid until the next
    /// <see cref="Begin"/> or <see cref="TryAdmit"/>.</summary>
    public ReadOnlySpan<int> Seen => m_seen.Members;

    /// <summary>Returns whether the delivery in progress has admitted a slot.</summary>
    /// <param name="index">The 0-based entity index; one outside <see cref="Capacity"/> was never admitted.</param>
    /// <returns><see langword="true"/> when <see cref="TryAdmit"/> admitted the slot since <see cref="Begin"/>.</returns>
    public bool Admitted(int index) => m_seen.Contains(index: index);
    /// <summary>Starts a delivery: nothing is admitted yet.</summary>
    public void Begin() => m_seen.Clear();
    /// <summary>Gets an entity's current attitude (the later interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion CurrentOrientation(int index) => m_currentOrientation[index];
    /// <summary>Gets an entity's current position (the later interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 CurrentPosition(int index) => m_currentPosition[index];
    /// <summary>Ends a delivery: every slot the last delivery left live that this one did not admit goes inactive and
    /// forgets its placement, and the admitted slots become the live set.</summary>
    /// <param name="preserved">A slot to keep live and active though this delivery did not admit it (a seeded
    /// route not yet in an ordinary snapshot), or a negative index for none.</param>
    public void Complete(int preserved = -1) {
        foreach (var index in m_live.Members) {
            if (!m_seen.Contains(index: index) && (index != preserved)) {
                Volatile.Write(
                    location: ref m_active[index],
                    value: false
                );
                m_placementId[index] = null;
            }
        }

        m_live.Clear();
        foreach (var index in m_seen.Members) { _ = m_live.Add(index: index); }
        if (preserved >= 0) { _ = m_live.Add(index: preserved); }
    }
    /// <summary>Records a discontinuity the consumer declared itself (an over-threshold correction it snapped), which
    /// a presentation-side follower reseeds across.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public void Discontinue(int index) => m_poseEpoch[index]++;
    /// <summary>Gets whether an entity was active (drawn) in the latest delivery.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => Volatile.Read(location: ref m_active[index]);
    /// <summary>Gets the placement an entity inhabits, or <see langword="null"/> for none.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public string? PlacementId(int index) => m_placementId[index];
    /// <summary>Gets an entity's pose-discontinuity counter: it moves on activation, a teleport and each
    /// <see cref="Discontinue"/>.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public int PoseEpoch(int index) => m_poseEpoch[index];
    /// <summary>Resolves the lowest active entity index that inhabits a placement.</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="index">The entity index, or <c>-1</c> when none inhabits it.</param>
    /// <returns><see langword="true"/> when an active entity inhabits the placement.</returns>
    public bool TryInhabitant(string placementId, out int index) {
        for (var candidate = 0; (candidate < Capacity); candidate++) {
            if (
                IsActive(index: candidate) &&
                string.Equals(
                    a: m_placementId[candidate],
                    b: placementId,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                index = candidate;

                return true;
            }
        }

        index = -1;

        return false;
    }
    /// <summary>Gets an entity's previous attitude (the earlier interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion PreviousOrientation(int index) => m_previousOrientation[index];
    /// <summary>Gets an entity's previous position (the earlier interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 PreviousPosition(int index) => m_previousPosition[index];
    /// <summary>Publishes an exact pose for an entity outside any snapshot (a committed route head): both endpoints
    /// hold it, the slot becomes live and active, and the epoch does not move.</summary>
    /// <param name="index">The 0-based entity index, within <see cref="Capacity"/>.</param>
    /// <param name="position">The position.</param>
    /// <param name="orientation">The attitude.</param>
    public void Seed(int index, Vector3 position, Quaternion orientation) {
        m_previousPosition[index] = position;
        m_currentPosition[index] = position;
        m_previousOrientation[index] = orientation;
        m_currentOrientation[index] = orientation;
        _ = m_live.Add(index: index);
        Volatile.Write(
            location: ref m_active[index],
            value: true
        );
    }
    /// <summary>Admits one snapshot entry: adds its slot to the admitted set and moves its endpoints, placement and
    /// active flag by its continuity hint. The active flag is written last, so the caller writes every other field it
    /// keeps for the entry before calling.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="easeCorrections">Whether the consumer eases a <see cref="EntityContinuityKind.Correction"/>'s render
    /// error out itself, in which case the previous endpoint snaps to the fresh pose and the step is
    /// <see cref="WorldPoseStep.Corrected"/>; otherwise a correction is ordinary double-buffering.</param>
    /// <param name="admission">The step taken and the pose the entity held before it.</param>
    /// <returns><see langword="false"/> when the entry's index is outside <see cref="Capacity"/> and nothing changed.</returns>
    public bool TryAdmit(in EntitySnapshot entry, bool easeCorrections, out WorldPoseAdmission admission) {
        var index = entry.Index;

        if (((uint)index) >= ((uint)Capacity)) {
            admission = default;

            return false;
        }

        var priorOrientation = m_currentOrientation[index];
        var priorPosition = m_currentPosition[index];
        var step = WorldPoseStep.Interpolated;

        _ = m_seen.Add(index: index);
        m_placementId[index] = entry.PlacementId;

        if (
            !Volatile.Read(location: ref m_active[index]) ||
            (entry.Continuity.Kind == EntityContinuityKind.Teleport)
        ) {
            // Never interpolate across a jump; the consumer drops any in-flight correction offset with it.
            m_previousPosition[index] = entry.Position;
            m_previousOrientation[index] = entry.Orientation;
            m_poseEpoch[index]++;
            step = WorldPoseStep.Seeded;
        } else if (entry.Continuity is { Kind: EntityContinuityKind.Carry, Carried: { } carried }) {
            // An in-place crossing: the previous pose rides the crossing, so the frame interpolates on from where the
            // body was, carried, and every consumer that latched the body's pose carries it the same way (Carries).
            m_previousPosition[index] = carried.Point(point: priorPosition);
            m_previousOrientation[index] = carried.Attitude(attitude: priorOrientation);
            Carries.Record(
                carry: carried,
                index: index
            );
            step = WorldPoseStep.Carried;
        } else if (easeCorrections && (entry.Continuity.Kind == EntityContinuityKind.Correction)) {
            m_previousPosition[index] = entry.Position;
            m_previousOrientation[index] = entry.Orientation;
            step = WorldPoseStep.Corrected;
        } else {
            m_previousPosition[index] = priorPosition;
            m_previousOrientation[index] = priorOrientation;
        }

        m_currentPosition[index] = entry.Position;
        m_currentOrientation[index] = entry.Orientation;
        // The release write publishes every endpoint above to a reader on another thread (a federated observer
        // delivers on its socket task); IsActive's acquire read makes the completed record visible.
        Volatile.Write(
            location: ref m_active[index],
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
