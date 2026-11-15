using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>A world-axis box grown over the padded bounds of the colliders and field lattices folded into it: the reach
/// of a change to what a body reads, so a sleeping body whose bounds miss it provably cannot meet anything that
/// changed. Its <see langword="default"/> value has folded nothing.</summary>
internal struct WorldColliderBox {
    /// <summary>Gets a value indicating whether any collider was folded.</summary>
    public bool Any { readonly get; private set; }
    /// <summary>Gets the box; meaningful once <see cref="Any"/> holds.</summary>
    public FixedAabb Box { readonly get; private set; }
    /// <summary>Gets a value indicating whether a folded collider has no bounds (a half-space), so the box reaches
    /// everywhere.</summary>
    public readonly bool Unbounded => (Any && Box.IsEverywhere);

    /// <summary>Grows the box over one collider's padded world bounds (<see cref="FixedStaticCollider.WorldBox"/>).</summary>
    /// <param name="collider">The collider.</param>
    public void Fold(in FixedStaticCollider collider) => Fold(box: collider.WorldBox());
    /// <summary>Grows the box over a world-axis box.</summary>
    /// <param name="box">The box, not empty.</param>
    public void Fold(in FixedAabb box) {
        Box = (Any ? Box.Enclose(other: in box) : box);
        Any = true;
    }
    /// <summary>Determines whether a sphere about a point, grown by a margin, meets the box.</summary>
    /// <param name="position">The sphere's centre, in metres.</param>
    /// <param name="reach">The sphere's radius plus the margin, in metres.</param>
    /// <returns><see langword="true"/> when the grown sphere's bounds meet the box, which an unbounded box always does;
    /// <see langword="false"/> when nothing was folded or they miss.</returns>
    public readonly bool Meets(FixedVector3 position, FixedQ4816 reach) =>
        (Any && Box.Intersects(other: FixedAabb.Around(center: position, radius: reach)));
}
