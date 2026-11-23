using Puck.Maths;

namespace Puck.Physics;

/// <summary>The push-resolution arithmetic shared by every contact-source solver over <see cref="IContactField"/>:
/// displace out of penetration, latch the grounded/obstruction witness by the normal's walkable alignment, and clamp
/// approach velocity through up on walkable ground and across up on walls. One home so a correction to it reaches
/// every caller in the same change.</summary>
public static class FixedContactPushMath {
    /// <summary>The band past the contact skin within which a surface still holds a body that rests on it, in metres.
    /// A push leaves a body exactly at the skin, where a query at the skin alone finds no contact, so every solver asks
    /// at the skin plus this band and reads a surface inside the band as a resting contact (<see cref="Classify"/>):
    /// walkable ground still grounds the body there, and nothing displaces it. Without it, a body standing still or
    /// walking level ground reads as airborne every tick and pays a ground snap to find the floor it never left.</summary>
    public static FixedQ4816 RestingBand { get; } = FixedQ4816.FromRawBits(value: 16L);

    /// <summary>What one contact measured at the skin plus <see cref="RestingBand"/> means for the solve.</summary>
    public enum ContactKind : byte {
        /// <summary>No contact the solve acts on: a surface in the band that is not walkable ground, which a body beside
        /// a wall at its skin is not pressed into.</summary>
        None,
        /// <summary>Walkable ground in the band: it grounds the body and clamps its approach, and displaces nothing.</summary>
        Rest,
        /// <summary>A surface inside the skin: the solve pushes the body out by the depth past the skin.</summary>
        Push,
    }

    /// <summary>Classifies a contact a solver measured at its skin plus <see cref="RestingBand"/>, and returns its depth
    /// past the skin alone: positive for a push, at most zero for a resting contact.</summary>
    /// <param name="normal">The measured contact normal.</param>
    /// <param name="banded">The contact's depth into the skin grown by the band.</param>
    /// <param name="up">The body's up axis the walkable test measures alignment against.</param>
    /// <param name="groundedThreshold">The <c>cos(maxSlope)</c> the normal's alignment must clear to be walkable.</param>
    /// <param name="depth">The depth past the skin alone.</param>
    /// <returns>What the contact means.</returns>
    public static ContactKind Classify(in FixedVector3 normal, FixedQ4816 banded, in FixedVector3 up, FixedQ4816 groundedThreshold, out FixedQ4816 depth) {
        depth = (banded - RestingBand);
        if (depth > FixedQ4816.Zero) { return ContactKind.Push; }
        var alignment = FixedVector3.Dot(left: normal, right: up);
        return (((alignment > FixedQ4816.Zero) && (alignment >= groundedThreshold)) ? ContactKind.Rest : ContactKind.None);
    }
    /// <summary>The uncommitted outcome of resolving one confirmed contact push: the position/velocity deltas a
    /// caller adds, whether the push reads as walkable, and the measured surface normal.</summary>
    public readonly record struct Trial(FixedVector3 PositionDelta, FixedVector3 VelocityDelta, bool Grounded, FixedVector3 Normal);

    /// <summary>Applies a trial: displaces position, clamps velocity, and latches the walkable/obstruction witness.
    /// <paramref name="grounded"/> is a one-way latch — never cleared here — matching every caller's per-iteration
    /// re-resolution.</summary>
    /// <param name="position">The body's position (in/out).</param>
    /// <param name="velocity">The body's velocity (in/out).</param>
    /// <param name="grounded">Whether the body is grounded this iteration (in/out; only ever set, never cleared).</param>
    /// <param name="lastNormal">The most recent non-walkable (obstruction) normal (in/out).</param>
    /// <param name="groundNormal">The most recent walkable normal (in/out).</param>
    /// <param name="trial">The trial to commit.</param>
    internal static void Commit(ref FixedVector3 position, ref FixedVector3 velocity, ref bool grounded, ref FixedVector3 lastNormal, ref FixedVector3 groundNormal, in Trial trial) {
        position += trial.PositionDelta;
        velocity += trial.VelocityDelta;

        if (trial.Grounded) {
            grounded = true;
            groundNormal = trial.Normal;
        } else {
            lastNormal = trial.Normal;
        }
    }

    /// <summary>Computes the trial for an ordinary push against a MEASURED surface normal: displacement along the
    /// separating direction, the walkable test against <paramref name="up"/>, and the approach-velocity clamp.</summary>
    /// <param name="normal">The measured contact normal.</param>
    /// <param name="penetration">The confirmed penetration depth along <paramref name="normal"/>.</param>
    /// <param name="velocity">The body's velocity, read only — the clamp is returned as a delta, never applied here.</param>
    /// <param name="up">The body's up axis the walkable test measures alignment against.</param>
    /// <param name="groundedThreshold">The <c>cos(maxSlope)</c> <paramref name="normal"/>'s alignment with
    /// <paramref name="up"/> must clear to read as walkable.</param>
    /// <param name="blockClimb">Treat an approached edge as an obstruction even when its normal is walkable.
    /// Vertical support probes leave this false; a step-up must clear the edge on its raised across move.</param>
    public static Trial ComputeOrdinary(FixedVector3 normal, FixedQ4816 penetration, in FixedVector3 velocity, FixedVector3 up, FixedQ4816 groundedThreshold, bool blockClimb = false) {
        var alignment = FixedVector3.Dot(
            left: normal,
            right: up
        );
        // An authored limit just below 90 degrees can quantize to a zero cosine. A vertical face has no
        // upward separating direction, so it must never enter the lift's division by alignment.
        var grounded = (!blockClimb && (alignment > FixedQ4816.Zero) && (alignment >= groundedThreshold));

        // A face steeper than the walkable slope is a wall, and a wall pushes across `up`, never along its own
        // normal: the normal push's up-component is a lift, and against a body driving into the face every tick
        // that lift outruns gravity, so the face the slope limit refuses to ground on would still carry the body
        // to its top. The projected push resolves the same penetration (penetration / |normal ⊥ up|), and the clamp
        // removes only the horizontal approach, so a body falling past a wall keeps falling along it. A face within
        // the walkable cone of straight down is a ceiling: its projection is degenerate and its push is its normal.
        if (
            !grounded &&
            (alignment > -groundedThreshold)
        ) {
            if (normal.TryRejectNormalized(
                length: out var acrossLength,
                unit: out var wall,
                unitAxis: up
            )) {
                return new Trial(
                    Grounded: false,
                    Normal: normal,
                    PositionDelta: (wall * (penetration / acrossLength)),
                    VelocityDelta: (velocity.LimitAlong(maximum: FixedQ4816.Zero, unitAxis: -wall) - velocity)
                );
            }
        }

        // Walkable ground holds a body where it stands: it pushes along up, never along its own tilted normal. A normal
        // push and a normal clamp each leave a downslope remainder of the tick's fall (the push's sideways component,
        // and the fall's velocity less its normal part), which a body at rest on any slope would creep by every tick.
        // Lifting along up resolves the same penetration (penetration / (normal·up)) and the same approach (the
        // velocity's normal component, removed through its up component), so planar motion stays as the body drives
        // it. On level ground the normal is up and both forms are one.
        if (grounded) {
            var into = FixedVector3.Dot(
                left: velocity,
                right: normal
            );

            return new Trial(
                Grounded: true,
                Normal: normal,
                PositionDelta: (up * (penetration / alignment)),
                VelocityDelta: ((into < FixedQ4816.Zero)
                    ? (up * (-into / alignment))
                    : FixedVector3.Zero)
            );
        }

        return new Trial(
            Grounded: grounded,
            Normal: normal,
            PositionDelta: (normal * penetration),
            VelocityDelta: (velocity.LimitAlong(maximum: FixedQ4816.Zero, unitAxis: -normal) - velocity)
        );
    }
}
