using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // The exact simulation rate a compiled follower step was bound at (TicksPerSecond / StepTicks — an exact integer
    // division by construction: FixedWorldKit.Compile derives StepTicks as FixedTickConversion.TicksPerSecond /
    // simulationRateHz, and the world's own simulationRateHz is validated to divide it exactly). Multiplying a target
    // delta by this integer Hz is the ZOH target-velocity term x' = (target − previous) / dt.
    private static FixedQ4816 StepRateHz(in SecondOrderStep step) =>
        FixedQ4816.FromInteger(value: unchecked((long)(step.TicksPerSecond / step.StepTicks)));
    // Re-seats a follower's position lane exactly from a live value, keeping the velocity lane's own raw untouched —
    // never an unconditional resync, which would discard the sixteen guard bits every step. Shared by the planar
    // (per-lane) and vertical (scalar) followers.
    private static SecondOrderState ReseatFollowerPosition(SecondOrderState follower, FixedQ4816 position) =>
        new(PositionRaw: (position.Value << 16), VelocityRaw: follower.VelocityRaw);
    private static SecondOrderState3 ReseatFollowerPosition(SecondOrderState3 follower, FixedVector3 position) => new(
        X: ReseatFollowerPosition(
            follower: follower.X,
            position: position.X
        ),
        Y: ReseatFollowerPosition(
            follower: follower.Y,
            position: position.Y
        ),
        Z: ReseatFollowerPosition(
            follower: follower.Z,
            position: position.Z
        )
    );
    // Advances the planar dynamics follower by one step and writes m_planarVelocity from it, magnitude-clamped to
    // the resolved planar move speed (sprint included, never the CURRENT target's own magnitude — a released stick's
    // target is zero, and clamping to that would snap the coast-down to zero instead of letting it decay through the
    // follower's own release rate). A live overshoot (r > 0, ζ < 1) never exceeds what the kit's speed envelope
    // admits. The follower's own Position lane is left at its unclamped step result; the NEXT step's re-seed (above)
    // is what pulls it back to the clamped value, so the clamp never discards the follower's own physics, only what
    // reaches the body.
    private FixedVector3 StepPlanarFollower(in SecondOrderStep step, FixedVector3 target, FixedQ4816 ceiling) {
        if (m_planarFollower.Position != m_planarVelocity) {
            m_planarFollower = ReseatFollowerPosition(
                follower: m_planarFollower,
                position: m_planarVelocity
            );
        }

        // The FIRST step after a reset seeds m_planarPreviousTarget rather than differencing it against the zeroed
        // default — otherwise a teleport under a held stick manufactures a target-velocity impulse the reset never
        // intended (r ≠ 0 would react to it as if the target had actually swept from the origin in one tick).
        var targetVelocity = (m_planarFollowerSeeded
            ? ((target - m_planarPreviousTarget) * StepRateHz(step: in step))
            : FixedVector3.Zero
        );

        m_planarFollowerSeeded = true;
        m_planarFollower = step.Step(
            state: m_planarFollower,
            target: target,
            targetVelocity: targetVelocity
        );
        m_planarPreviousTarget = target;

        var followed = m_planarFollower.Position;
        var speed = followed.Length;

        m_planarVelocity = ((speed > ceiling)
            ? ((followed / speed) * ceiling)
            : followed
        );

        return m_planarVelocity;
    }
    // The swim vertical lane's counterpart to StepPlanarFollower, under the SAME compiled propagator — clamped to
    // the medium's own terminal speeds rather than a target-magnitude ceiling (the vertical target is already signed
    // and asymmetric, unlike the planar target's isotropic disc).
    private FixedQ4816 StepVerticalFollower(in SecondOrderStep step, FixedQ4816 target, FixedQ4816 minimum, FixedQ4816 maximum) {
        if (m_verticalFollower.Position != m_verticalVelocity) {
            m_verticalFollower = ReseatFollowerPosition(
                follower: m_verticalFollower,
                position: m_verticalVelocity
            );
        }

        var targetVelocity = (m_verticalFollowerSeeded
            ? ((target - m_verticalPreviousTarget) * StepRateHz(step: in step))
            : FixedQ4816.Zero
        );

        m_verticalFollowerSeeded = true;
        m_verticalFollower = step.Step(
            state: m_verticalFollower,
            target: target,
            targetVelocity: targetVelocity
        );
        m_verticalPreviousTarget = target;

        m_verticalVelocity = FixedQ4816.Clamp(
            value: m_verticalFollower.Position,
            minimum: minimum,
            maximum: maximum
        );

        return m_verticalVelocity;
    }
}
