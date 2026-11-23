using System.Numerics;

namespace Puck.World.Protocol;

/// <summary>
/// The motion an in-place portal crossing carries a body through, <c>x → Scale·(Rotation·x) + Translation</c>, in
/// presentation float: the single-precision mirror of the <see cref="WorldFrameIsometry"/> map the simulation moved the
/// body by. A presentation consumer that latched a world-space pose or rate for the body (an interpolation endpoint, a
/// follower, an animation driver's last position, a planted foot) carries it through this so it continues across the
/// crossing instead of seeing a jump.
/// </summary>
/// <param name="Rotation">The unit rotation.</param>
/// <param name="Translation">The translation applied after the rotation and scale, world units.</param>
/// <param name="Scale">The uniform scale applied to a point after the rotation: the pair's
/// <see cref="WorldFrameIsometry.Scale"/>, one for a pair of faces of one size.</param>
public readonly record struct WorldPoseCarry(Quaternion Rotation, Vector3 Translation, float Scale = 1f) {
    /// <summary>Gets the carry that moves nothing.</summary>
    public static WorldPoseCarry Identity => new(
        Rotation: Quaternion.Identity,
        Translation: Vector3.Zero
    );

    /// <summary>Returns the carry a mapped portal pair that maps world up onto itself
    /// (<see cref="WorldFrameIsometry.PreservesWorldUp"/>) moves a traveller by: the pair's turn about up
    /// (<see cref="WorldFrameIsometry.YawDelta"/>), its scale (<see cref="WorldFrameIsometry.Scale"/>) and the
    /// translation that takes the source face's origin to the destination face's.</summary>
    /// <param name="source">The face the traveller entered.</param>
    /// <param name="destination">Its counterpart.</param>
    /// <returns>The carry.</returns>
    public static WorldPoseCarry FromFrames(in WorldFaceFrame source, in WorldFaceFrame destination) {
        var rotation = Quaternion.CreateFromAxisAngle(
            angle: ((float)WorldFrameIsometry.YawDelta(
                destination: in destination,
                source: in source
            )),
            axis: Vector3.UnitY
        );
        var scale = ((float)WorldFrameIsometry.Scale(
            destination: in destination,
            source: in source
        ));

        return new WorldPoseCarry(
            Rotation: rotation,
            Scale: scale,
            Translation: (destination.Origin.ToVector3() - (Vector3.Transform(
                rotation: rotation,
                value: source.Origin.ToVector3()
            ) * scale))
        );
    }
    /// <summary>Carries a world-space attitude.</summary>
    /// <param name="attitude">The attitude.</param>
    /// <returns>The carried attitude.</returns>
    public Quaternion Attitude(Quaternion attitude) => Quaternion.Normalize(value: (Rotation * attitude));
    /// <summary>Carries a world-space direction, which only the rotation moves.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The carried direction.</returns>
    public Vector3 Direction(Vector3 direction) => Vector3.Transform(
        rotation: Rotation,
        value: direction
    );
    /// <summary>Carries a world-space point.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The carried point.</returns>
    public Vector3 Point(Vector3 point) => ((Direction(direction: point) * Scale) + Translation);
    /// <summary>Returns this carry followed by <paramref name="later"/>.</summary>
    /// <param name="later">The carry applied after this one.</param>
    /// <returns>The composed carry.</returns>
    public WorldPoseCarry Then(WorldPoseCarry later) => new(
        Rotation: Quaternion.Normalize(value: (later.Rotation * Rotation)),
        Scale: (later.Scale * Scale),
        Translation: later.Point(point: Translation)
    );
}
/// <summary>
/// The in-place crossings a presentation mirror has seen, per entity: a counter that advances once per crossing and
/// the last few carries, so a consumer that latched an entity's pose at one count carries it to the current one.
/// Presentation only. An entity's history is allocated at its first crossing, so the table holds one reference and one
/// count per entity slot until a body crosses.
/// </summary>
/// <remarks>The table grows with its <see cref="WorldPoseTable"/> (<see cref="EnsureCapacity"/>): the counts and
/// histories are replaced whole and published by reference, so a reader on another thread reads one complete
/// generation, and a slot past it has never crossed.</remarks>
/// <param name="capacity">The number of entity indices tracked before the table first grows.</param>
public sealed class WorldPoseCarries(int capacity) {
    // A consumer further behind than this many crossings re-seeds instead, as it would after a teleport.
    private const int Depth = 4;

    // Each entity's crossing count and its last Depth carries, by crossing count modulo Depth; a history is null until
    // its first crossing. Record writes the history before it publishes the count, so a reader that sees a count past
    // its own finds the history written.
    private sealed class Columns(int length) {
        public readonly int[] Epoch = new int[length];
        public readonly WorldPoseCarry[]?[] Rings = new WorldPoseCarry[]?[length];
    }

    private Columns m_columns = new(length: capacity);

    /// <summary>Grows the table to hold at least <paramref name="count"/> entity indices, keeping every history; a
    /// smaller count changes nothing. The caller serializes it with <see cref="Record"/>.</summary>
    /// <param name="count">The number of entity indices to hold.</param>
    public void EnsureCapacity(int count) {
        var columns = m_columns;

        if (count <= columns.Epoch.Length) { return; }

        var grown = new Columns(length: count);

        columns.Epoch.CopyTo(array: grown.Epoch, index: 0);
        columns.Rings.CopyTo(array: grown.Rings, index: 0);
        Volatile.Write(location: ref m_columns, value: grown);
    }
    /// <summary>Returns an entity's crossing count.</summary>
    /// <param name="index">The 0-based entity index; one past the table has never crossed.</param>
    /// <returns>The count.</returns>
    public int Epoch(int index) {
        var columns = Volatile.Read(location: ref m_columns);

        return ((((uint)index) < ((uint)columns.Epoch.Length)) ? Volatile.Read(location: ref columns.Epoch[index]) : 0);
    }
    /// <summary>Records one crossing of an entity.</summary>
    /// <param name="index">The 0-based entity index, within the table.</param>
    /// <param name="carry">The crossing's carry.</param>
    public void Record(int index, WorldPoseCarry carry) {
        var columns = m_columns;
        var next = (columns.Epoch[index] + 1);

        (columns.Rings[index] ??= new WorldPoseCarry[Depth])[(next % Depth)] = carry;
        Volatile.Write(
            location: ref columns.Epoch[index],
            value: next
        );
    }
    /// <summary>Composes the carries an entity crossed through after <paramref name="since"/>, up to its current
    /// count.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <param name="since">The count the caller latched its pose at.</param>
    /// <param name="carry">The composed carry, when the history still holds every crossing since.</param>
    /// <returns><see langword="true"/> when the history covers the span; <see langword="false"/> when the caller is
    /// further behind than the history, or ahead of it, and re-seeds instead.</returns>
    public bool TrySince(int index, int since, out WorldPoseCarry carry) {
        var columns = Volatile.Read(location: ref m_columns);
        var current = ((((uint)index) < ((uint)columns.Epoch.Length)) ? Volatile.Read(location: ref columns.Epoch[index]) : 0);

        carry = WorldPoseCarry.Identity;

        if (
            (since > current) ||
            ((current - since) > Depth)
        ) {
            return false;
        }

        if (current == since) {
            return true;
        }

        var ring = Volatile.Read(location: ref columns.Rings[index])!;

        for (var epoch = (since + 1); (epoch <= current); epoch++) {
            carry = carry.Then(later: ring[(epoch % Depth)]);
        }

        return true;
    }
}
