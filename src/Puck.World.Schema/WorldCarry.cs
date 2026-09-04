using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>A kit's carry facet: the seam a body picks up and puts down a rigid body through
/// (<c>body.carry</c>/<c>body.release</c>). Presence is the whole switch: a body wearing this kit may hold at most
/// one other rigid body at a time, at <see cref="Offset"/> from its own root and orientation. Absent, a kit's body
/// always refuses <c>body.carry</c> by name — the same "presence is the switch" convention <see cref="WorldRigid"/>
/// carries for the rigid facet.</summary>
/// <param name="Offset">The carry point, in the carrier's own body-local axes: a carried body's world pose tracks
/// <c>root + orientation·Offset</c> every tick it stays attached.</param>
/// <param name="MassEquivalent">The carrier's own notional mass, in the same units <see cref="WorldRigid.Mass"/>
/// uses. A locomotion kit authors no mass of its own (<see cref="WorldKit.Mass"/> is gravitational, not inertial),
/// so this stands in for it when deriving <see cref="MaxCarryFraction"/>'s ceiling. Must be strictly positive.</param>
/// <param name="MaxCarryFraction">The fraction of <see cref="MassEquivalent"/> — scaled by the carrier's own live
/// <c>Scale</c> under the same mass ∝ Scale³ law <see cref="WorldRigid"/> derives against — a candidate body's own
/// scaled <see cref="WorldRigid.Mass"/> may not exceed. Non-negative; 1 (the default) admits a body up to the
/// carrier's own mass-equivalent.</param>
/// <param name="MaxReach">The greatest distance, in world units, between the carrier's and the target's positions
/// <c>body.carry</c> admits. Must be strictly positive.</param>
public sealed record WorldCarry(DocumentVector3 Offset, float MassEquivalent, float MaxCarryFraction = 1f, float MaxReach = 1.5f);

/// <summary>The one-time fixed-point compilation of a kit's <see cref="WorldCarry"/> facet.</summary>
/// <param name="Offset">The compiled carry point, in the carrier's own body-local axes.</param>
/// <param name="MaxCarryMass">The compiled ceiling <see cref="WorldCarry.MassEquivalent"/> ×
/// <see cref="WorldCarry.MaxCarryFraction"/> resolves to, at the carrier's Scale == 1 — <c>WorldBody.TryBeginCarry</c>
/// scales this by the carrier's own live Scale³ before comparing it to a candidate's scaled
/// <see cref="FixedWorldRigid.Mass"/>, the same law <see cref="FixedWorldRigid"/> itself scales mass under.</param>
/// <param name="MaxReach">The compiled reach ceiling.</param>
public readonly record struct FixedWorldCarry(FixedVector3 Offset, FixedQ4816 MaxCarryMass, FixedQ4816 MaxReach) {
    /// <summary>Compiles an authored <see cref="WorldCarry"/> facet. Validation
    /// (<see cref="WorldDefinitionValidator"/>) has already refused a non-finite offset or a non-positive
    /// mass-equivalent/reach by the time this runs.</summary>
    public static FixedWorldCarry Compile(WorldCarry carry) => new(
        Offset: FixedVector3.FromVector3(value: carry.Offset),
        MaxCarryMass: (FixedQ4816.FromDouble(value: carry.MassEquivalent) * FixedQ4816.FromDouble(value: carry.MaxCarryFraction)),
        MaxReach: FixedQ4816.FromDouble(value: carry.MaxReach)
    );
}
