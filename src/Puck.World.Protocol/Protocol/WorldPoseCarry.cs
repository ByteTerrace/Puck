using System.Numerics;

namespace Puck.World.Protocol;

/// <summary>
/// The rigid motion an in-place portal crossing carries a body through, <c>x → Rotation·x + Translation</c>, in
/// presentation float: the single-precision mirror of the <see cref="WorldFrameIsometry"/> the simulation moved the body
/// by. A presentation consumer that latched a world-space pose or rate for the body (an interpolation endpoint, a
/// follower, an animation driver's last position, a planted foot) carries it through this so it continues across the
/// crossing instead of seeing a jump.
/// </summary>
/// <param name="Rotation">The unit rotation.</param>
/// <param name="Translation">The translation applied after the rotation, world units.</param>
public readonly record struct WorldPoseCarry(Quaternion Rotation, Vector3 Translation) {
    /// <summary>Gets the carry that moves nothing.</summary>
    public static WorldPoseCarry Identity => new(
        Rotation: Quaternion.Identity,
        Translation: Vector3.Zero
    );

    /// <summary>Returns the carry a mapped portal pair that maps world up onto itself
    /// (<see cref="WorldFrameIsometry.PreservesWorldUp"/>) moves a traveller by: the pair's turn about up
    /// (<see cref="WorldFrameIsometry.YawDelta"/>) and the translation that takes the source face's origin to the
    /// destination face's.</summary>
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

        return new WorldPoseCarry(
            Rotation: rotation,
            Translation: (destination.Origin.ToVector3() - Vector3.Transform(
                rotation: rotation,
                value: source.Origin.ToVector3()
            ))
        );
    }
    /// <summary>Carries a world-space attitude.</summary>
    /// <param name="attitude">The attitude.</param>
    /// <returns>The carried attitude.</returns>
    public Quaternion Attitude(Quaternion attitude) => Quaternion.Normalize(value: (Rotation * attitude));
    /// <summary>Carries a world-space direction or rate, which the translation does not move.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The carried direction.</returns>
    public Vector3 Direction(Vector3 direction) => Vector3.Transform(
        rotation: Rotation,
        value: direction
    );
    /// <summary>Carries a world-space point.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The carried point.</returns>
    public Vector3 Point(Vector3 point) => (Direction(direction: point) + Translation);
    /// <summary>Returns this carry followed by <paramref name="later"/>.</summary>
    /// <param name="later">The carry applied after this one.</param>
    /// <returns>The composed carry.</returns>
    public WorldPoseCarry Then(WorldPoseCarry later) => new(
        Rotation: Quaternion.Normalize(value: (later.Rotation * Rotation)),
        Translation: later.Point(point: Translation)
    );
}
/// <summary>
/// The in-place crossings a presentation mirror has seen, per entity: a counter that advances once per crossing and
/// the last few carries, so a consumer that latched an entity's pose at one count carries it to the current one.
/// Presentation only.
/// </summary>
/// <param name="capacity">The number of entity indices tracked.</param>
public sealed class WorldPoseCarries(int capacity) {
    // A consumer further behind than this many crossings re-seeds instead, as it would after a teleport.
    private const int Depth = 4;

    private readonly int[] m_epoch = new int[capacity];
    private readonly WorldPoseCarry[] m_ring = new WorldPoseCarry[(capacity * Depth)];

    /// <summary>Returns an entity's crossing count.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The count.</returns>
    public int Epoch(int index) => Volatile.Read(location: ref m_epoch[index]);
    /// <summary>Records one crossing of an entity.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <param name="carry">The crossing's carry.</param>
    public void Record(int index, WorldPoseCarry carry) {
        var next = (m_epoch[index] + 1);

        m_ring[((index * Depth) + (next % Depth))] = carry;
        Volatile.Write(
            location: ref m_epoch[index],
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
        var current = Epoch(index: index);

        carry = WorldPoseCarry.Identity;

        if (
            (since > current) ||
            ((current - since) > Depth)
        ) {
            return false;
        }

        for (var epoch = (since + 1); (epoch <= current); epoch++) {
            carry = carry.Then(later: m_ring[((index * Depth) + (epoch % Depth))]);
        }

        return true;
    }
}
