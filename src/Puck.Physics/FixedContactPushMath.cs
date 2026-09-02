using Puck.Maths;

namespace Puck.Physics;

/// <summary>The push-resolution arithmetic shared by every contact-source solver over <see cref="IContactField"/>:
/// displace out of penetration, latch the grounded/obstruction witness by the normal's walkable alignment, and clamp
/// approach velocity along the normal. One home so a correction to it reaches every caller in the same change.</summary>
internal static class FixedContactPushMath {
    /// <summary>The uncommitted outcome of resolving one confirmed contact push: the position/velocity deltas a
    /// caller adds, whether the push reads as walkable, and the measured surface normal. <see cref="Normal"/> is
    /// <see langword="null"/> exactly for a degenerate-gradient push (<see cref="ComputeDegenerate"/>), which
    /// fabricates no normal and so never latches an obstruction witness.</summary>
    internal readonly record struct Trial(FixedVector3 PositionDelta, FixedVector3 VelocityDelta, bool Grounded, FixedVector3? Normal);

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

            if (trial.Normal is { } walkableNormal) {
                groundNormal = walkableNormal;
            }
        } else if (trial.Normal is { } normal) {
            lastNormal = normal;
        }
    }
    /// <summary>Computes the trial for the degenerate-gradient fallback: bare positional displacement along the
    /// reverse-of-motion direction (or <paramref name="up"/> for a body at rest), with no velocity clamp and no
    /// grounded/obstruction latch — the direction is not a measured normal, so it must never fabricate one.</summary>
    /// <param name="velocity">The body's velocity, read only.</param>
    /// <param name="penetration">The confirmed penetration depth.</param>
    /// <param name="up">The body's up axis, used when <paramref name="velocity"/> is zero.</param>
    internal static Trial ComputeDegenerate(in FixedVector3 velocity, FixedQ4816 penetration, FixedVector3 up) {
        var direction = (-velocity).Normalize();

        if (direction == FixedVector3.Zero) {
            direction = up;
        }

        return new Trial(
            Grounded: false,
            Normal: null,
            PositionDelta: (direction * penetration),
            VelocityDelta: FixedVector3.Zero
        );
    }
    /// <summary>Computes the trial for an ordinary push against a MEASURED surface normal: displacement along the
    /// normal, the walkable test against <paramref name="up"/>, and the approach-velocity clamp.</summary>
    /// <param name="normal">The measured contact normal.</param>
    /// <param name="penetration">The confirmed penetration depth, added along <paramref name="normal"/>.</param>
    /// <param name="velocity">The body's velocity, read only — the clamp is returned as a delta, never applied here.</param>
    /// <param name="up">The body's up axis the walkable test measures alignment against.</param>
    /// <param name="groundedThreshold">The <c>cos(maxSlope)</c> <paramref name="normal"/>'s alignment with
    /// <paramref name="up"/> must clear to read as walkable.</param>
    internal static Trial ComputeOrdinary(FixedVector3 normal, FixedQ4816 penetration, in FixedVector3 velocity, FixedVector3 up, FixedQ4816 groundedThreshold) {
        var alignment = FixedVector3.Dot(
            left: normal,
            right: up
        );
        var grounded = (alignment >= groundedThreshold);

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
            var across = (normal - (up * alignment));

            if (
                across.TryLength(length: out var acrossLength) &&
                (acrossLength > FixedQ4816.Zero)
            ) {
                var wall = (across / acrossLength);
                var intoWall = FixedVector3.Dot(
                    left: velocity,
                    right: wall
                );

                return new Trial(
                    Grounded: false,
                    Normal: normal,
                    PositionDelta: (wall * (penetration / acrossLength)),
                    VelocityDelta: ((intoWall < FixedQ4816.Zero)
                        ? -(wall * intoWall)
                        : FixedVector3.Zero
                    )
                );
            }
        }

        var into = FixedVector3.Dot(
            left: velocity,
            right: normal
        );
        var velocityDelta = ((into < FixedQ4816.Zero)
            ? -(normal * into)
            : FixedVector3.Zero
        );

        return new Trial(
            Grounded: grounded,
            Normal: normal,
            PositionDelta: (normal * penetration),
            VelocityDelta: velocityDelta
        );
    }
}
