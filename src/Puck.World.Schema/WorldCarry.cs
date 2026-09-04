using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>A kit's carry facet: the seam a body picks up and puts down a rigid body through
/// (<c>body.carry</c>/<c>body.release</c>). Presence is the whole switch: a body wearing this kit may hold at most
/// one other rigid body at a time, at <see cref="Offset"/> from its own root and orientation. Absent, a kit's body
/// always refuses <c>body.carry</c> by name — the same "presence is the switch" convention <see cref="WorldRigid"/>
/// carries for the rigid facet.</summary>
/// <param name="Offset">The full-scale carry point, in the carrier's own body-local axes: a carried body's world
/// pose tracks <c>root + orientation·(Offset × Scale)</c> every tick it stays attached.</param>
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
/// <param name="Offset">The compiled full-scale carry point, in the carrier's own body-local axes.</param>
/// <param name="MaxCarryMass">The compiled ceiling <see cref="WorldCarry.MassEquivalent"/> ×
/// <see cref="WorldCarry.MaxCarryFraction"/> resolves to, at the carrier's Scale == 1 — <c>WorldBody.TryBeginCarry</c>
/// scales this by the carrier's own live Scale³ before comparing it to a candidate's scaled
/// <see cref="FixedWorldRigid.Mass"/>, the same law <see cref="FixedWorldRigid"/> itself scales mass under.</param>
/// <param name="MaxReach">The compiled reach ceiling.</param>
public readonly record struct FixedWorldCarry(FixedVector3 Offset, FixedQ4816 MaxCarryMass, FixedQ4816 MaxReach) {
    /// <summary>Compiles an authored <see cref="WorldCarry"/> facet. Validation
    /// (<see cref="WorldDefinitionValidator"/>) has already proved that the offset, reach, and derived mass ceiling
    /// fit the fixed-point representation by the time this runs.</summary>
    /// <exception cref="InvalidOperationException">The caller bypassed document validation and the facet is not
    /// representable.</exception>
    public static FixedWorldCarry Compile(WorldCarry carry) {
        if (!TryCompile(carry: carry, compiled: out var compiled, reason: out var reason)) {
            throw new InvalidOperationException(message: reason);
        }

        return compiled;
    }

    /// <summary>Attempts the fixed-point derivation without throwing, for strict document-boundary validation.</summary>
    /// <param name="carry">The authored carry facet.</param>
    /// <param name="compiled">The compiled facet on success; otherwise the default value.</param>
    /// <param name="reason">The named representation failure; empty on success.</param>
    /// <returns><see langword="true"/> exactly when every authored and derived value is representable.</returns>
    public static bool TryCompile(WorldCarry carry, out FixedWorldCarry compiled, out string reason) {
        compiled = default;
        var fixedMaximum = (double)FixedQ4816.MaxValue;
        var offset = carry.Offset.Value;

        if (
            !float.IsFinite(f: offset.X) ||
            !float.IsFinite(f: offset.Y) ||
            !float.IsFinite(f: offset.Z) ||
            (Math.Abs(value: offset.X) > fixedMaximum) ||
            (Math.Abs(value: offset.Y) > fixedMaximum) ||
            (Math.Abs(value: offset.Z) > fixedMaximum)
        ) {
            reason = "A carry offset leaves the engine's fixed-point representation.";
            return false;
        }

        if (
            !float.IsFinite(f: carry.MassEquivalent) ||
            !float.IsFinite(f: carry.MaxCarryFraction) ||
            !float.IsFinite(f: carry.MaxReach) ||
            (Math.Abs(value: carry.MassEquivalent) > fixedMaximum) ||
            (Math.Abs(value: carry.MaxCarryFraction) > fixedMaximum) ||
            (Math.Abs(value: carry.MaxReach) > fixedMaximum)
        ) {
            reason = "A carry mass-equivalent, fraction, or reach leaves the engine's positive fixed-point representation.";
            return false;
        }

        var massEquivalent = FixedQ4816.FromDouble(value: carry.MassEquivalent);
        var fraction = FixedQ4816.FromDouble(value: carry.MaxCarryFraction);
        var reach = FixedQ4816.FromDouble(value: carry.MaxReach);

        if (
            (massEquivalent <= FixedQ4816.Zero) ||
            (fraction < FixedQ4816.Zero) ||
            (reach <= FixedQ4816.Zero)
        ) {
            reason = "A carry mass-equivalent, fraction, or reach leaves the engine's positive fixed-point representation.";
            return false;
        }

        if (!FusedArithmetic.TryMixedScaleProduct(
            a: massEquivalent.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: fraction.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var maxCarryMassRaw
        )) {
            reason = "A carry mass-equivalent times max-carry fraction leaves the engine's fixed-point representation.";
            return false;
        }

        compiled = new FixedWorldCarry(
            Offset: FixedVector3.FromVector3(value: offset),
            MaxCarryMass: FixedQ4816.FromRawBits(value: maxCarryMassRaw),
            MaxReach: reach
        );
        reason = "";
        return true;
    }
}
