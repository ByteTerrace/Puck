using System.Numerics;
using Puck.Maths;
using Puck.SdfVm.Queries;

namespace Puck.Demo.Overworld;

/// <summary>
/// One avatar's movement state and the pure, deterministic per-tick step that advances it ACROSS AN ARBITRARY SDF
/// A field-walking counterpart to <see cref="PlatformerBody"/> for a floor defined by a
/// planetoid instead of a flat room?" It is NOT a reshape of <see cref="PlatformerBody"/> (that type's floor/wall
/// clamps are Y-UP HARD-CODED — <c>room.FloorTop</c>, <c>room.MinX</c>/<c>MaxX</c>/<c>MinZ</c>/<c>MaxZ</c> all assume
/// a fixed world axis is "up" and the other two are always horizontal); this type carries no notion of world axes at
/// all. Its only input is a live <see cref="IFieldEvaluator"/>: the same field a renderer marches IS the ground this
/// body walks on and the gravity that pulls it there (<c>-gradient</c> — see <see cref="IFieldEvaluator"/>'s
/// remarks), so a body standing on a planetoid's far side has <see cref="Up"/> pointing the opposite way from one at
/// the near pole, and both are equally "grounded."
/// <para>
/// FIXED-POINT PURE, like <see cref="PlatformerBody"/>: <see cref="Step"/> is a function of <c>(this state, intent,
/// fixed dt, field)</c> with no float, no clock, no allocation, and no randomness, so a recorded intent stream
/// replays bit-identically on every machine. Float appears only at the two presentation seams
/// (<see cref="RenderRelativePositionAt"/>, <see cref="OrientationAt"/>) — never fed back into the sim.
/// </para>
/// <para>
/// ANCHOR PUBLISH (the documented <c>SdfAnchor</c> verdict — presentation, not simulation): the body's orthonormal
/// frame (<see cref="Up"/> plus the tangent basis <see cref="Step"/> derives from it) converts to a
/// <see cref="Quaternion"/> ONCE per tick, at the end of <see cref="Step"/> — never per query. A consumer publishing
/// this body to an <c>SdfAnchorTable</c> reads <see cref="OrientationAt"/> the same
/// way an existing camera-following consumer reads <see cref="PlatformerBody.OrientationAt"/>.
/// </para>
/// </summary>
public sealed class FieldWalkerBody {
    /// <summary>The body's world position.</summary>
    public WorldCoord3 Position;
    /// <summary>The body's world-space velocity (both tangential and normal components live in the same vector;
    /// <see cref="Step"/> decomposes it against <see cref="Up"/> each tick rather than tracking the two separately,
    /// so there is exactly one velocity to reason about, same as <see cref="PlatformerBody.Velocity"/>).</summary>
    public FixedVector3 Velocity;
    /// <summary>The body's current "up" — the field's gradient at <see cref="Position"/>, i.e. the unit direction
    /// pointing directly away from the nearest surface. Recomputed at the top of every <see cref="Step"/>; holds its
    /// previous value on a degenerate field query (see <see cref="Step"/>'s remarks) rather than snapping to
    /// something arbitrary.</summary>
    public FixedVector3 Up;
    /// <summary>The facing heading, as an angle in the body's OWN tangent plane — basis-agnostic (a pure function of
    /// input, via <see cref="FixedQ4816.Atan2"/>, exactly like <see cref="PlatformerBody.FacingYaw"/>), reapplied to
    /// whichever tangent basis <see cref="Step"/> derives from the CURRENT tick's <see cref="Up"/> when a consumer
    /// asks for an orientation.</summary>
    public FixedQ4816 FacingAngle;
    /// <summary>Whether the body is resting on the surface this tick (see <see cref="Step"/>'s ground-snap step).</summary>
    public bool Grounded;

    // Presentation-only state (never hashed, never read back into the sim) — the previous tick's position/orientation,
    // snapshotted at the top of every Step so the renderer can interpolate toward the current tick by the frame's
    // alpha, exactly mirroring PlatformerBody's m_previousPosition/m_previousFacingYaw pair.
    private WorldCoord3 m_previousPosition;
    private Quaternion m_previousOrientation;
    private Quaternion m_orientation;

    /// <summary>Creates a body at <paramref name="position"/> with an initial "up" (the caller's best guess before
    /// the first <see cref="Step"/> ever queries the field — e.g. the radial direction from a known planetoid center,
    /// or straight up for a body about to fall onto something below).</summary>
    /// <param name="position">The spawn position.</param>
    /// <param name="up">The initial up direction; normalized internally (see <see cref="FixedVector3.Normalize"/> —
    /// a zero vector normalizes to zero, which <see cref="Step"/>'s tangent-basis construction cannot use, so pass a
    /// genuine direction).</param>
    public FieldWalkerBody(WorldCoord3 position, FixedVector3 up) {
        Position = position;
        Up = up.Normalize();
        m_previousPosition = position;

        var (t1, t2) = TangentBasis(up: Up);

        m_orientation = LookRotation(forward: t2.ToVector3(), up: Up.ToVector3());
        m_previousOrientation = m_orientation; // pre-step state == spawn until the first Step, so alpha-lerp is a no-op
    }

    /// <summary>The position relative to <paramref name="renderOrigin"/>, interpolated between the previous and
    /// current fixed tick by <paramref name="alpha"/> — see <see cref="PlatformerBody.RenderRelativePositionAt"/>'s
    /// remarks (the same rebase-before-cast shape).</summary>
    /// <param name="renderOrigin">The per-frame world anchor.</param>
    /// <param name="alpha">The fraction in <c>[0, 1)</c> between the previous and current fixed tick.</param>
    /// <returns>The render-relative, interpolated position.</returns>
    public Vector3 RenderRelativePositionAt(WorldCoord3 renderOrigin, float alpha) =>
        Vector3.Lerp(
            amount: alpha,
            value1: m_previousPosition.ToRenderRelative(origin: renderOrigin),
            value2: Position.ToRenderRelative(origin: renderOrigin)
        );

    /// <summary>The orientation quaternion, interpolated between the previous and current fixed tick's PUBLISHED
    /// orthonormal frame by <paramref name="alpha"/>. Unlike <see cref="PlatformerBody.OrientationAt"/> (a single yaw
    /// angle, cheaply lerped in angle space), a walker's whole frame can rotate between ticks — as the body crosses a
    /// planetoid's terminator, <see cref="Up"/> itself sweeps — so this interpolates the two PUBLISHED quaternions
    /// directly via <see cref="Quaternion.Slerp"/>, the general-purpose choice for two arbitrary orientations.
    /// PRESENTATION ONLY.</summary>
    /// <param name="alpha">The fraction in <c>[0, 1)</c> between the previous and current fixed tick.</param>
    /// <returns>The interpolated orientation.</returns>
    public Quaternion OrientationAt(float alpha) =>
        Quaternion.Slerp(quaternion1: m_previousOrientation, quaternion2: m_orientation, amount: alpha);

    /// <summary>Advances one FIXED simulation tick against the live <paramref name="field"/>. <paramref name="dt"/>
    /// is the constant tick period in seconds.</summary>
    /// <param name="intent">This tick's input (only <see cref="PlayerIntent.Move"/>/<see cref="PlayerIntent.RunHeld"/>
    /// are consumed — no jump this wave; a planet WALK is this wave's proof, jump is a follow-on).</param>
    /// <param name="tuning">The feel constants.</param>
    /// <param name="dt">The fixed tick period, in seconds.</param>
    /// <param name="field">The live field to walk (the SAME program a renderer would march — see the type remarks).</param>
    public void Step(in PlayerIntent intent, FieldWalkerTuning tuning, FixedQ4816 dt, IFieldEvaluator field) {
        ArgumentNullException.ThrowIfNull(argument: tuning);
        ArgumentNullException.ThrowIfNull(argument: field);

        m_previousPosition = Position;
        m_previousOrientation = m_orientation;

        // 1. gradient -> Up. A degenerate field query (the probe landed exactly on a flat/self-canceling point —
        // see IFieldEvaluator.TryFieldGradient's remarks) leaves Up at its previous tick's value rather than
        // snapping to something arbitrary; the field is expected to be well-conditioned everywhere this body walks.
        if (field.TryFieldGradient(position: Position, gradient: out var gradient)) {
            Up = gradient;
        }

        // 2. Tangent basis, reseeded from whichever world axis is LEAST aligned with Up (see TangentBasis) so the
        // Gram-Schmidt step below never divides by a near-zero vector, even as Up sweeps through an axis-aligned
        // direction while walking.
        var (t1, t2) = TangentBasis(up: Up);

        // 3. Input -> tangent move: PlayerIntent.Move.X/Y (strafe/forward) become t1/t2 coefficients — the exact
        // convention PlatformerBody's world-XZ horizontal uses, just expressed in the LOCAL tangent basis instead of
        // fixed world axes.
        var move = new FixedVector2(X: FixedQ4816.FromDouble(value: intent.Move.X), Y: FixedQ4816.FromDouble(value: intent.Move.Y));
        var hasInput = ((move.X != FixedQ4816.Zero) || (move.Y != FixedQ4816.Zero));
        var targetTangent = (((t1 * move.X) + (t2 * move.Y)) * (intent.RunHeld ? (tuning.WalkSpeed * tuning.SprintMultiplier) : tuning.WalkSpeed));

        // Decompose the current velocity against Up (recomputed fresh every tick, since Up itself may have moved) into
        // its normal (along Up) and tangential (in-plane) parts, accelerate the tangential part toward the target
        // exactly like PlatformerBody's horizontal MoveToward, then re-combine after gravity updates the normal part.
        var normalSpeed = FixedVector3.Dot(left: Velocity, right: Up);
        var tangentVelocity = (Velocity - (Up * normalSpeed));
        var acceleration = (Grounded
            ? (hasInput ? tuning.GroundAcceleration : tuning.GroundDeceleration)
            : (hasInput ? tuning.AirAcceleration : tuning.AirDeceleration));

        tangentVelocity = MoveToward(current: tangentVelocity, target: targetTangent, maxDelta: (acceleration * dt));

        if (hasInput) {
            FacingAngle = FixedQ4816.Atan2(y: move.X, x: move.Y);
        }

        // 4. Gravity along -Up: the field's gradient supplied the DIRECTION in step 1; the tuning supplies the
        // MAGNITUDE (see FieldWalkerTuning.GravityAcceleration's remarks — the field itself carries no strength).
        normalSpeed = FixedQ4816.Max(x: (normalSpeed - (tuning.GravityAcceleration * dt)), y: -tuning.MaxFallSpeed);
        Velocity = (tangentVelocity + (Up * normalSpeed));

        // Integrate in the body's cell-local frame (same shape as PlatformerBody's own integration step).
        var nextLocal = (Position.Local + (Velocity * dt));
        var nextPosition = Position.WithLocal(local: nextLocal);

        // 5. Ground-snap: evaluate the field at the POST-INTEGRATION position (the same "check where you're about to
        // be" order PlatformerBody's floor clamp uses). "Inward" means the normal velocity is not pulling the body
        // away from the surface — with no jump this wave, that is true whenever the body isn't already resting
        // (normalSpeed's only source is gravity, always <= 0, until a ground-snap zeroes it).
        if (field.TryDistance(position: nextPosition, distance: out var distance, material: out _)) {
            var inward = (normalSpeed <= FixedQ4816.Zero);

            if ((distance <= tuning.GroundSnapEpsilon) && inward) {
                // Project velocity onto the tangent plane (discard the normal component — the body has landed) and
                // push the position exactly onto the surface along Up.
                Velocity -= (Up * FixedVector3.Dot(left: Velocity, right: Up));
                nextPosition += (Up * -distance);
                Grounded = true;
            } else {
                Grounded = false;
            }
        } else {
            Grounded = false;
        }

        Position = nextPosition;

        // 6. Publish the orientation ONCE per tick (float, presentation only — the documented SdfAnchor verdict):
        // the facing tangent direction is FacingAngle's cosine/sine combination of THIS tick's t1/t2 (never the
        // fixed-point vectors themselves — trig on the presentation seam only), with Up as the frame's up axis.
        var facing = ((float)(double)FacingAngle);
        var forwardTangent = ((t2.ToVector3() * MathF.Cos(x: facing)) + (t1.ToVector3() * MathF.Sin(x: facing)));

        m_orientation = LookRotation(forward: forwardTangent, up: Up.ToVector3());
    }

    // The tangent basis at `up`: t1 is the world axis LEAST aligned with `up`, Gram-Schmidt-projected into the plane
    // perpendicular to `up` and normalized; t2 completes a right-handed orthonormal frame. Reseeding the reference
    // axis by alignment (rather than a single fixed choice, e.g. always World.Up) guarantees the projected vector
    // never has near-zero length: the least-aligned of three mutually orthogonal unit axes has |dot| <= 1/sqrt(3)
    // against ANY unit `up`, so the projected length is always >= sqrt(1 - 1/3) ~ 0.816 before normalizing.
    private static (FixedVector3 T1, FixedVector3 T2) TangentBasis(FixedVector3 up) {
        var reference = LeastAlignedAxis(up: up);
        var t1 = (reference - (up * FixedVector3.Dot(left: reference, right: up))).Normalize();
        var t2 = FixedVector3.Cross(left: up, right: t1);

        return (t1, t2);
    }

    private static readonly FixedVector3 WorldAxisX = new(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 WorldAxisY = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 WorldAxisZ = new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);

    private static FixedVector3 LeastAlignedAxis(FixedVector3 up) {
        var dotX = FixedQ4816.Abs(value: FixedVector3.Dot(left: WorldAxisX, right: up));
        var dotY = FixedQ4816.Abs(value: FixedVector3.Dot(left: WorldAxisY, right: up));
        var dotZ = FixedQ4816.Abs(value: FixedVector3.Dot(left: WorldAxisZ, right: up));

        if ((dotX <= dotY) && (dotX <= dotZ)) {
            return WorldAxisX;
        }

        return ((dotY <= dotZ) ? WorldAxisY : WorldAxisZ);
    }
    private static FixedVector3 MoveToward(FixedVector3 current, FixedVector3 target, FixedQ4816 maxDelta) {
        var delta = (target - current);
        var distance = delta.Length;

        // distance == 0 (already at target) folds into the first test, so the divisor below is always positive.
        return (((distance <= maxDelta) || (distance <= FixedQ4816.Zero))
            ? target
            : (current + (delta * (maxDelta / distance))));
    }

    // A general look-rotation from a forward/up pair (unlike PlatformerBody's CreateFromAxisAngle special case, which
    // only works when "up" is the fixed world Y axis): builds an orthonormal right-handed frame and reads the
    // quaternion off its rotation matrix. Presentation-only (System.Numerics float), never fed back into the sim.
    private static Quaternion LookRotation(Vector3 forward, Vector3 up) {
        var f = Vector3.Normalize(value: forward);
        var right = Vector3.Normalize(value: Vector3.Cross(vector1: up, vector2: f));
        var correctedUp = Vector3.Cross(vector1: f, vector2: right);
        var matrix = new Matrix4x4(
            m11: right.X, m12: right.Y, m13: right.Z, m14: 0f,
            m21: correctedUp.X, m22: correctedUp.Y, m23: correctedUp.Z, m24: 0f,
            m31: f.X, m32: f.Y, m33: f.Z, m34: 0f,
            m41: 0f, m42: 0f, m43: 0f, m44: 1f
        );

        return Quaternion.CreateFromRotationMatrix(matrix: matrix);
    }
}
