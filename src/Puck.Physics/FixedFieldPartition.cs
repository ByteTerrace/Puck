using Puck.Maths;

namespace Puck.Physics;

/// <summary>What a field collider's solid is within a world box, as its <see cref="FixedFieldPartition"/> proves it.</summary>
public enum FixedFieldRegion {
    /// <summary>Nothing narrower is proved: the box meets a carve, or more than one piece, or a piece whose analytic
    /// collider is not its exact surface. A query reads the composed field.</summary>
    Field = 0,
    /// <summary>The solid within the box is exactly one piece's, whose analytic collider is that piece's surface.</summary>
    Base = 1,
    /// <summary>The box holds no point of the solid.</summary>
    Empty = 2,
}
/// <summary>An oriented box outside which one operand of a composed field reads above zero, with its world-axis box: the
/// operand's reach.</summary>
/// <param name="Box">The world-axis box about the oriented box, or a box without bounds for an operand with none (a
/// half-space).</param>
/// <param name="Frame">The oriented box's world axes and half-extent along each, or a frame that separates nothing for an
/// operand without bounds.</param>
public readonly record struct FixedFieldReach(FixedColliderBox Box, FixedColliderFrame Frame) {
    /// <summary>Creates the reach of an operand bounded by an oriented box.</summary>
    /// <param name="center">The box's world centre.</param>
    /// <param name="rotation">The box-to-world rotation, unit.</param>
    /// <param name="halfExtent">The box's half-extent along its own axes, in metres, at least zero.</param>
    /// <returns>The reach.</returns>
    public static FixedFieldReach Oriented(FixedVector3 center, in FixedQuaternion rotation, FixedVector3 halfExtent) {
        var axisX = rotation.Rotate(vector: FixedVector3.UnitX);
        var axisY = rotation.Rotate(vector: FixedVector3.UnitY);
        var axisZ = rotation.Rotate(vector: FixedVector3.UnitZ);
        var padding = new FixedVector3(X: FixedStaticCollider.BoundsPadding, Y: FixedStaticCollider.BoundsPadding, Z: FixedStaticCollider.BoundsPadding);
        var world = ((((FixedVector3.Abs(value: axisX) * halfExtent.X) + (FixedVector3.Abs(value: axisY) * halfExtent.Y)) + (FixedVector3.Abs(value: axisZ) * halfExtent.Z)) + padding);

        return new FixedFieldReach(Box: new FixedColliderBox(Center: center, Extent: world),
            Frame: new FixedColliderFrame(AxisX: axisX, AxisY: axisY, AxisZ: axisZ, Reach: (halfExtent + padding)));
    }
    /// <summary>Creates the reach of an operand without bounds, which every box meets.</summary>
    /// <param name="center">A world point of the operand.</param>
    /// <returns>The reach.</returns>
    public static FixedFieldReach Unbounded(FixedVector3 center) {
        var none = new FixedVector3(X: -FixedQ4816.One, Y: -FixedQ4816.One, Z: -FixedQ4816.One);

        return new FixedFieldReach(Box: new FixedColliderBox(Center: center, Extent: none),
            Frame: new FixedColliderFrame(AxisX: default, AxisY: default, AxisZ: default, Reach: none));
    }

    /// <summary>Returns whether a world box may meet the reach: neither its world-axis box nor its oriented box separates
    /// them. Conservative: a box this answers false for holds no point where the operand reads at or below zero.</summary>
    /// <param name="center">The box's world centre.</param>
    /// <param name="extent">The box's world half-extent, at least zero on every axis.</param>
    /// <returns><see langword="false"/> when the box is proved apart from the reach.</returns>
    public bool Meets(in FixedVector3 center, in FixedVector3 extent) {
        if (!Box.IsBounded) {
            return true;
        }

        var offset = (center - Box.Center);
        var gap = (FixedVector3.Abs(value: offset) - (Box.Extent + extent));

        return ((gap.X <= FixedQ4816.Zero) && (gap.Y <= FixedQ4816.Zero) && (gap.Z <= FixedQ4816.Zero) &&
            !Frame.Separates(bodyExtent: in extent, offset: in offset, skin: FixedQ4816.Zero));
    }
}
/// <summary>One hard-union piece of a composed field: its reach, and the analytic collider that is exactly its surface,
/// when it has one.</summary>
/// <param name="Reach">Where the piece's own field can read at or below zero.</param>
/// <param name="Exact">The piece's analytic collider when that collider's surface is the piece's own surface in the
/// field, margin included; <see langword="null"/> when its collider only bounds it (an oriented-box stand-in, a
/// cylinder's contained core).</param>
public readonly record struct FixedFieldPiece(FixedFieldReach Reach, FixedStaticCollider? Exact);
/// <summary>The structure of a composed field that lets a query confined to a box read an analytic collider in its place:
/// its hard-union pieces and its carves' reaches.</summary>
/// <remarks>
/// <para>The invariant: within a world box that meets no carve's reach and exactly one piece's reach, the composed
/// field's solid is that piece's solid. It holds when every piece joins by hard union, every carve is a hard
/// subtraction, and each reach encloses every point where its operand's own field reads at or below zero (with padding
/// past rounding). A hard union's sign follows its operands' signs alone, and a hard subtraction <c>max(a, −s)</c> keeps
/// the sign of <c>a</c> wherever <c>s</c> reads above zero, whatever <c>a</c>'s value, so in such a box the walk's sign
/// is the one piece's sign at every point. A box meeting no piece's reach holds none of the solid.</para>
/// <para>This type trusts its pieces: the one compiler that builds a partition from a creation's blends
/// (<c>WorldColliderSet</c>'s field collider) is what establishes the invariant, and it builds none for any other
/// blend or for relief.</para>
/// <para>Reaches and colliders are in world space at the field collider's pose when it was compiled, as
/// <see cref="FixedColliderField.Bases"/> are; a field collider that carries a partition is never moved.</para>
/// </remarks>
public sealed class FixedFieldPartition {
    private readonly FixedFieldPiece[] m_pieces;
    private readonly FixedFieldReach[] m_carves;

    /// <summary>Initializes a new instance of the <see cref="FixedFieldPartition"/> class.</summary>
    /// <param name="pieces">The hard-union pieces.</param>
    /// <param name="carves">The reaches of the hard subtractions' operands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pieces"/> or <paramref name="carves"/> is
    /// <see langword="null"/>.</exception>
    public FixedFieldPartition(IEnumerable<FixedFieldPiece> pieces, IEnumerable<FixedFieldReach> carves) {
        m_pieces = [.. (pieces ?? throw new ArgumentNullException(paramName: nameof(pieces)))];
        m_carves = [.. (carves ?? throw new ArgumentNullException(paramName: nameof(carves)))];
    }

    /// <summary>Narrows a query confined to a world box: the one exact piece the solid there is, or none of it.</summary>
    /// <param name="center">The box's world centre.</param>
    /// <param name="extent">The box's world half-extent, at least zero on every axis.</param>
    /// <param name="collider">The piece's exact collider when the answer is <see cref="FixedFieldRegion.Base"/>.</param>
    /// <returns>What the solid within the box is proved to be.</returns>
    public FixedFieldRegion Narrow(in FixedVector3 center, in FixedVector3 extent, out FixedStaticCollider collider) {
        collider = default;

        foreach (ref readonly var carve in m_carves.AsSpan()) {
            if (carve.Meets(center: in center, extent: in extent)) {
                return FixedFieldRegion.Field;
            }
        }

        var met = -1;

        for (var index = 0; (index < m_pieces.Length); index++) {
            if (m_pieces[index].Reach.Meets(center: in center, extent: in extent)) {
                if (met >= 0) {
                    return FixedFieldRegion.Field;
                }

                met = index;
            }
        }

        if (met < 0) {
            return FixedFieldRegion.Empty;
        }
        if (m_pieces[met].Exact is not { } exact) {
            return FixedFieldRegion.Field;
        }

        collider = exact;
        return FixedFieldRegion.Base;
    }
}
