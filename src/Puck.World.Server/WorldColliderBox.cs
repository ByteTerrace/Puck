using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>A world-axis box grown over the padded bounds of the colliders and field lattices folded into it: the reach
/// of a change to what a body reads, so a sleeping body whose bounds miss it provably cannot meet anything that
/// changed.</summary>
internal struct WorldColliderBox {
    /// <summary>Gets a value indicating whether any collider was folded.</summary>
    public bool Any { readonly get; private set; }
    /// <summary>Gets a value indicating whether a folded collider has no bounds (a half-space), so the box reaches
    /// everywhere.</summary>
    public bool Unbounded { readonly get; private set; }
    /// <summary>Gets the box's least corner, in metres; meaningful once <see cref="Any"/> holds.</summary>
    public FixedVector3 Minimum { readonly get; private set; }
    /// <summary>Gets the box's greatest corner, in metres; meaningful once <see cref="Any"/> holds.</summary>
    public FixedVector3 Maximum { readonly get; private set; }

    /// <summary>Grows the box over one collider's padded world bounds.</summary>
    /// <param name="collider">The collider.</param>
    public void Fold(in FixedStaticCollider collider) {
        var box = FixedColliderBox.Of(collider: in collider);

        if (!box.IsBounded) {
            Any = true;
            Unbounded = true;
            return;
        }
        Fold(minimum: (box.Center - box.Extent), maximum: (box.Center + box.Extent));
    }
    /// <summary>Determines whether a sphere about a point, grown by a margin, meets the box.</summary>
    /// <param name="position">The sphere's centre, in metres.</param>
    /// <param name="reach">The sphere's radius plus the margin, in metres.</param>
    /// <returns><see langword="true"/> when the box is unbounded or the grown sphere's bounds meet it; <see langword="false"/>
    /// when nothing was folded or they miss.</returns>
    public readonly bool Meets(FixedVector3 position, FixedQ4816 reach) => (Any && (Unbounded || (
        ((position.X + reach) >= Minimum.X) && ((position.X - reach) <= Maximum.X) &&
        ((position.Y + reach) >= Minimum.Y) && ((position.Y - reach) <= Maximum.Y) &&
        ((position.Z + reach) >= Minimum.Z) && ((position.Z - reach) <= Maximum.Z))));

    /// <summary>Grows the box over a world-axis box.</summary>
    /// <param name="minimum">The least corner, in metres.</param>
    /// <param name="maximum">The greatest corner, in metres; at least <paramref name="minimum"/> on every axis.</param>
    public void Fold(FixedVector3 minimum, FixedVector3 maximum) {
        if (!Any) {
            Minimum = minimum;
            Maximum = maximum;
            Any = true;
            return;
        }
        Minimum = new FixedVector3(X: FixedQ4816.Min(x: Minimum.X, y: minimum.X), Y: FixedQ4816.Min(x: Minimum.Y, y: minimum.Y),
            Z: FixedQ4816.Min(x: Minimum.Z, y: minimum.Z));
        Maximum = FixedVector3.Max(left: Maximum, right: maximum);
    }
}
