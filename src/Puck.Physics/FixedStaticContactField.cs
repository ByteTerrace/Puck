using Puck.Maths;

namespace Puck.Physics;

/// <summary>A contact field that hands out the part of itself a region can reach as a value, so a product derived from
/// that part (a navigation domain's bake) is identified by the geometry it read.</summary>
public interface IRegionalContactField : IContactField {
    /// <summary>Returns the part of this field a body inside <paramref name="region"/> can meet. Every query whose body
    /// and sweep stay inside the region answers as this field does. Two parts compare equal
    /// (<see cref="object.Equals(object)"/>) exactly when they hold the same geometry under the same tuning, so equal
    /// parts give equal answers to every query.</summary>
    /// <param name="region">The world box the queries stay inside.</param>
    /// <returns>The part, which never changes.</returns>
    IContactField Within(in FixedAabb region);
    /// <summary>Bounds where this field and another part of the same provider differ: every query whose body and sweep
    /// stay clear of the box answers alike in both.</summary>
    /// <param name="other">The other part.</param>
    /// <param name="changed">The box holding every collider one of the two holds and the other does not; empty when they
    /// hold the same colliders.</param>
    /// <returns><see langword="true"/> when the two can be compared (the same kind of field under the same tuning);
    /// otherwise the difference is unbounded.</returns>
    bool TryDifference(IContactField other, out FixedAabb changed);
}

/// <summary>The analytic contact provider over a fixed set of static colliders: the relaxation solver
/// (<see cref="FixedStaticContactSolver"/>) narrowed by the set's grid (<see cref="FixedColliderGrid"/>), with no
/// per-tick colliders. It never changes once built and may be queried from any number of threads at once.</summary>
public sealed class FixedStaticContactField : IRegionalContactField, IEquatable<FixedStaticContactField> {
    // A query's region beyond its body's swept bounds: the pushes a sweep meets on the way stay inside it.
    private static readonly FixedQ4816 RegionMargin = FixedQ4816.One;

    [ThreadStatic]
    private static List<int>? CandidateScratch;

    private readonly FixedStaticCollider[] m_colliders;
    private readonly FixedColliderGrid m_grid;
    private readonly FixedStaticContactSolver m_solver;

    /// <summary>Initializes a new instance of the <see cref="FixedStaticContactField"/> class over colliders it keeps.</summary>
    /// <param name="colliders">The colliders, in the order every query visits them; the field reads the array and the
    /// caller must not change it afterwards.</param>
    /// <param name="solver">The relaxation solver's tuning.</param>
    /// <exception cref="ArgumentNullException"><paramref name="colliders"/> is <see langword="null"/>.</exception>
    public FixedStaticContactField(FixedStaticCollider[] colliders, FixedStaticContactSolver solver) {
        ArgumentNullException.ThrowIfNull(argument: colliders);
        m_colliders = colliders;
        m_solver = solver;
        Set = new FixedStaticColliderSet(colliders: colliders);
        m_grid = new FixedColliderGrid(boxes: Set.Boxes);
    }

    /// <summary>Gets the colliders with their compiled bounds.</summary>
    public FixedStaticColliderSet Set { get; }
    /// <summary>Gets the relaxation solver's tuning.</summary>
    public FixedStaticContactSolver Solver => m_solver;

    /// <summary>Lists the colliders a move can reach: those near the volumes' bounds at both ends of the move, grown by
    /// a margin for the pushes met on the way.</summary>
    /// <param name="previousPosition">The foot point the move starts from.</param>
    /// <param name="position">The foot point the move ends at.</param>
    /// <param name="orientation">The body orientation.</param>
    /// <param name="volumes">The body volumes.</param>
    /// <param name="scratch">The caller's list the indices are written into; the result reads it until the next call.</param>
    /// <returns>The candidates, or the default value (every collider) for no volumes or a region too large to grid.</returns>
    private FixedColliderCandidates Candidates(in FixedVector3 previousPosition, in FixedVector3 position, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, List<int> scratch) {
        if (volumes.IsEmpty) { return default; }
        var swept = FixedAabb.Empty;

        foreach (ref readonly var volume in volumes) {
            swept = swept.Enclose(other: FixedColliderBounds.WorldBounds(orientation: in orientation, position: previousPosition, volume: in volume));
            swept = swept.Enclose(other: FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume));
        }
        return m_grid.Candidates(boxes: Set.Boxes, region: swept.Grow(margin: RegionMargin), scratch: scratch, skin: m_solver.ContactSkin);
    }

    /// <inheritdoc/>
    public IContactField Within(in FixedAabb region) {
        var boxes = Set.Boxes;
        var kept = new List<FixedStaticCollider>();

        for (var index = 0; (index < boxes.Length); index++) {
            if (boxes[index].Intersects(other: in region)) {
                kept.Add(item: m_colliders[index]);
            }
        }
        return ((kept.Count == m_colliders.Length) ? this : new FixedStaticContactField(colliders: [.. kept], solver: m_solver));
    }
    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        Resolve(dynamicColliders: FixedStaticColliderSet.Empty, orientation: in orientation, position: ref position, up: in up, velocity: ref velocity, volumes: volumes);
    /// <summary>Resolves a body's position and velocity out of these colliders and per-tick ones (<see cref="IContactField.Resolve"/>).</summary>
    /// <param name="position">The body's foot point (in/out).</param>
    /// <param name="velocity">The body's velocity (in/out).</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's compiled convex volumes.</param>
    /// <param name="up">The body's up axis.</param>
    /// <param name="dynamicColliders">The colliders the caller recomputes per tick, solved beside these.</param>
    /// <returns>The grounded verdict, the surface it grounded on, and the last resolved non-walkable contact normal.</returns>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, FixedStaticColliderSet dynamicColliders) =>
        m_solver.Resolve(colliders: Set, dynamicColliders: dynamicColliders, orientation: in orientation, position: ref position,
            up: in up, velocity: ref velocity, volumes: volumes,
            candidates: Candidates(orientation: in orientation, position: position, previousPosition: position, scratch: Scratch(), volumes: volumes));
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        ResolveSweep(dynamicColliders: FixedStaticColliderSet.Empty, orientation: in orientation, position: ref position, previousPosition: in previousPosition,
            up: in up, velocity: ref velocity, volumes: volumes);
    /// <summary>Resolves one integrated step against these colliders and per-tick ones (<see cref="IContactField.ResolveSweep"/>).</summary>
    /// <param name="previousPosition">The body's foot point before the step.</param>
    /// <param name="position">The body's foot point (in/out).</param>
    /// <param name="velocity">The body's velocity (in/out).</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's compiled convex volumes.</param>
    /// <param name="up">The body's up axis.</param>
    /// <param name="dynamicColliders">The colliders the caller recomputes per tick, solved beside these.</param>
    /// <returns>The grounded verdict, the surface it grounded on, and the last resolved non-walkable contact normal.</returns>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, FixedStaticColliderSet dynamicColliders) =>
        m_solver.ResolveSweep(colliders: Set, dynamicColliders: dynamicColliders, previousPosition: in previousPosition,
            position: ref position, velocity: ref velocity, orientation: in orientation, volumes: volumes, up: in up,
            candidates: Candidates(orientation: in orientation, position: position, previousPosition: in previousPosition, scratch: Scratch(), volumes: volumes));
    /// <inheritdoc/>
    public int CollectContacts(in FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 margin,
        Span<FixedSurfaceContact> destination) =>
        CollectContacts(destination: destination, dynamicColliders: FixedStaticColliderSet.Empty, margin: margin, orientation: in orientation, position: in position, volume: in volume);
    /// <summary>Collects every surface of these colliders and per-tick ones a body volume lies within a margin of
    /// (<see cref="IContactField.CollectContacts"/>).</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <param name="margin">The non-negative distance a surface may lie from the volume and still count.</param>
    /// <param name="destination">The destination for the surfaces.</param>
    /// <param name="dynamicColliders">The colliders the caller recomputes per tick, collected after these.</param>
    /// <returns>The number of surfaces written, or -1 when a field collider declines.</returns>
    public int CollectContacts(in FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 margin,
        Span<FixedSurfaceContact> destination, FixedStaticColliderSet dynamicColliders) {
        var reach = new FixedVector3(X: margin, Y: margin, Z: margin);

        return m_solver.CollectContacts(colliders: Set, dynamicColliders: dynamicColliders, destination: destination, margin: margin,
            orientation: in orientation, position: position, volume: in volume,
            candidates: Candidates(orientation: in orientation, position: (position + reach), previousPosition: (position - reach), scratch: Scratch(), volumes: [volume]));
    }
    /// <inheritdoc/>
    public ContactSupportWitness? SupportWitness(ReadOnlySpan<FixedSurfaceContact> contacts, in FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, in FixedVector3 up) =>
        SupportWitness(contacts: contacts, dynamicColliders: FixedStaticColliderSet.Empty, orientation: in orientation, position: in position, up: in up, volume: in volume);
    /// <summary>Builds the support witness of the deepest walkable surface among those collected from these colliders
    /// and per-tick ones (<see cref="IContactField.SupportWitness"/>).</summary>
    /// <param name="contacts">The surfaces <see cref="CollectContacts(in FixedVector3, in FixedQuaternion, in FixedBodyColliderVolume, FixedQ4816, Span{FixedSurfaceContact}, FixedStaticColliderSet)"/> wrote.</param>
    /// <param name="position">The body's world position the surfaces were collected at.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="dynamicColliders">The per-tick colliders the surfaces were collected from.</param>
    /// <returns>The witness, or <see langword="null"/> when no surface is walkable.</returns>
    public ContactSupportWitness? SupportWitness(ReadOnlySpan<FixedSurfaceContact> contacts, in FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, in FixedVector3 up, FixedStaticColliderSet dynamicColliders) =>
        m_solver.SupportWitness(colliders: Set, contacts: contacts, dynamicColliders: dynamicColliders, orientation: in orientation,
            position: position, up: in up, volume: in volume);
    /// <inheritdoc/>
    public bool TrySupportDistance(in FixedVector3 position, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes,
        in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) =>
        TrySupportDistance(descent: out descent, dynamicColliders: FixedStaticColliderSet.Empty, orientation: in orientation, position: in position,
            reach: reach, up: in up, volumes: volumes);
    /// <summary>Finds the support distance below a body over these colliders and per-tick ones
    /// (<see cref="IContactField.TrySupportDistance"/>).</summary>
    /// <param name="position">The body's foot point before the descent.</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's compiled convex volumes.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="reach">The greatest descent searched, in metres, positive.</param>
    /// <param name="descent">The least descent, in metres, at which the body first comes within the skin of a surface.</param>
    /// <param name="dynamicColliders">The colliders the caller recomputes per tick, searched beside these.</param>
    /// <returns><see langword="true"/> when a surface is met within the reach.</returns>
    public bool TrySupportDistance(in FixedVector3 position, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes,
        in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent, FixedStaticColliderSet dynamicColliders) =>
        m_solver.TrySupportDistance(colliders: Set, dynamicColliders: dynamicColliders, position: in position, orientation: in orientation,
            volumes: volumes, up: in up, reach: reach, descent: out descent,
            candidates: Candidates(orientation: in orientation, position: (position - (up * reach)), previousPosition: in position, scratch: Scratch(), volumes: volumes));
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        up = FixedVector3.UnitY;
        return true;
    }
    /// <inheritdoc/>
    public bool TryDifference(IContactField other, out FixedAabb changed) {
        changed = FixedAabb.Empty;
        if ((other is not FixedStaticContactField field) || (field.m_solver != m_solver)) {
            return false;
        }

        // A collider counted once per copy on each side: what remains unmatched on either side changed.
        var counts = new Dictionary<FixedStaticCollider, int>();

        foreach (var collider in m_colliders) {
            counts[collider] = (counts.GetValueOrDefault(key: collider) + 1);
        }
        foreach (var collider in field.m_colliders) {
            counts[collider] = (counts.GetValueOrDefault(key: collider) - 1);
        }

        var mine = Set.Boxes;
        var theirs = field.Set.Boxes;

        for (var index = 0; (index < m_colliders.Length); index++) {
            if (counts[m_colliders[index]] > 0) {
                changed = changed.Enclose(other: mine[index]);
            }
        }
        for (var index = 0; (index < field.m_colliders.Length); index++) {
            if (counts[field.m_colliders[index]] < 0) {
                changed = changed.Enclose(other: theirs[index]);
            }
        }

        return true;
    }
    /// <inheritdoc/>
    public bool Equals(FixedStaticContactField? other) =>
        ReferenceEquals(objA: this, objB: other) ||
        ((other is not null) && (m_solver == other.m_solver) && m_colliders.AsSpan().SequenceEqual(other: other.m_colliders));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as FixedStaticContactField));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(value1: m_solver, value2: m_colliders.Length);

    private static List<int> Scratch() => (CandidateScratch ??= []);
}
