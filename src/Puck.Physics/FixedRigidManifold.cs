using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// The persistent contact between two bodies — or one body and a static surface — that <see cref="FixedRigidWorld"/>
/// solves: the two bodies' solver views, the pair's friction and restitution, and the manifold slots whose impulses
/// warm start the next step. The caller owns it for as long as the pair stays in contact, so its slots are continuation
/// state the caller checkpoints, hashes and restores with the bodies it joins.
/// </summary>
/// <remarks>The normal of every slot points from <see cref="BodyA"/> toward <see cref="BodyB"/>. A static side (zero
/// inverse mass) never receives an impulse; a static surface is <see cref="BodyA"/> with its anchors at zero.</remarks>
public sealed class FixedRigidManifold {
    /// <summary>Gets or sets the body the contact normal points away from; static for a surface.</summary>
    public FixedRigidBody? BodyA { get; set; }
    /// <summary>Gets or sets the body the contact normal points toward.</summary>
    public FixedRigidBody? BodyB { get; set; }
    /// <summary>Gets or sets the pair's Coulomb friction coefficient, dimensionless and non-negative.</summary>
    public FixedQ4816 Friction { get; set; }
    /// <summary>Gets or sets the pair's coefficient of restitution, in <c>[0, 1]</c>.</summary>
    public FixedQ4816 Restitution { get; set; }
    /// <summary>Gets or sets the pair's rolling-resistance lever, in metres and non-negative: each contact point resists
    /// the bodies' relative rolling (angular velocity about the axes tangent to its surface) with an angular impulse of
    /// at most this lever times the point's normal impulse.</summary>
    public FixedQ4816 RollingResistance { get; set; }
    /// <summary>Gets the pair's persistent manifold slots.</summary>
    public FixedPairManifoldSlotTable Slots { get; } = new();
    /// <summary>Gets the largest applied-impulse change, in raw units at <see cref="FixedRigidScales.Impulse"/>, the most
    /// recent step's solve left — a reading, never a control.</summary>
    public long LastStepMovementRaw { get; internal set; }

    /// <summary>Associates one step's candidates into the slots, putting them in canonical order first.</summary>
    /// <param name="candidates">The pair's candidates for the step, oriented from <see cref="BodyA"/> toward
    /// <see cref="BodyB"/>; reordered in place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is <see langword="null"/>.</exception>
    public void Associate(List<FixedTwoBodyContact> candidates) {
        ArgumentNullException.ThrowIfNull(argument: candidates);
        FixedTwoBodyContact.Canonicalize(candidates: candidates);
        Slots.Associate(candidates: candidates);
    }
    /// <summary>Gets the largest accumulated normal impulse raw any slot carries.</summary>
    /// <returns>The impulse raw, at <see cref="FixedRigidScales.Impulse"/>, zero when no slot is loaded.</returns>
    public long MaximumNormalImpulseRaw() {
        var maximum = 0L;

        for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
            maximum = Math.Max(
                val1: maximum,
                val2: Slots[index].NormalImpulseRaw
            );
        }

        return maximum;
    }
}
