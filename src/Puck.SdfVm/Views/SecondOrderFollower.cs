using System.Numerics;

using Puck.Maths;

namespace Puck.SdfVm.Views;

/// <summary>The single-precision pole-matched state-transition matrix for one frame's delta time — the presentation
/// twin of <see cref="SecondOrderStep"/>. Never fed by simulation state; nothing it returns flows back into the
/// tick.</summary>
/// <param name="A11">Row 1, column 1 (dimensionless).</param>
/// <param name="A12">Row 1, column 2 (seconds).</param>
/// <param name="A21">Row 2, column 1 (reciprocal seconds).</param>
/// <param name="A22">Row 2, column 2 (dimensionless).</param>
public readonly record struct SecondOrderPropagator(float A11, float A12, float A21, float A22) {
    /// <summary>Gets the identity propagator — a follower left untouched.</summary>
    public static SecondOrderPropagator Identity => new(A11: 1f, A12: 0f, A21: 0f, A22: 1f);
}
/// <summary>
/// The presentation-side twin of <see cref="Puck.Maths.SecondOrderDynamics"/>: the same t3ssel8r-style second-order
/// system, derived and propagated in <see cref="float"/> at the render/frame seam rather than in fixed point. Kept in
/// sync with <see cref="Puck.Maths.SecondOrderDynamics"/>'s formulas by hand — the two never share code because the
/// fixed twin must stay allocation-free and BigInteger-exact at authoring time, while this twin must stay a handful
/// of transcendental calls with no BigInteger anywhere. This twin is presentation only: never feeds simulation state,
/// never persisted, never hashed.
/// </summary>
public readonly record struct SecondOrderResponse {
    /// <summary>The authored natural frequency, in Hz.</summary>
    public required float Frequency { get; init; }
    /// <summary>The authored damping ratio (dimensionless).</summary>
    public required float DampingRatio { get; init; }
    /// <summary>The authored initial response (dimensionless).</summary>
    public required float InitialResponse { get; init; }
    /// <summary>The analytic branch <see cref="DampingRatio"/> selected.</summary>
    public required SecondOrderDynamicsBranch Branch { get; init; }
    /// <summary>ζω, in reciprocal seconds.</summary>
    public required float DecayRate { get; init; }
    /// <summary>The damped oscillation rate ω_d (underdamped) or the real half-difference σ (overdamped), in
    /// radians per second. Exactly zero at <see cref="SecondOrderDynamicsBranch.CriticallyDamped"/>.</summary>
    public required float OscillationRate { get; init; }
    /// <summary>ω², in reciprocal seconds squared.</summary>
    public required float Stiffness { get; init; }
    /// <summary>k3 = rζ/ω, in seconds (signed with r).</summary>
    public required float TargetVelocityGain { get; init; }

    /// <summary>Derives a follower's constants from its authored triple.</summary>
    /// <param name="frequencyHz">The natural frequency in Hz; must be finite and strictly positive.</param>
    /// <param name="dampingRatio">The damping ratio; must be finite and non-negative.</param>
    /// <param name="initialResponse">The initial response; must be finite.</param>
    /// <returns>The derived constants.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is non-finite, <paramref name="frequencyHz"/> is not
    /// strictly positive, or <paramref name="dampingRatio"/> is negative.</exception>
    public static SecondOrderResponse Create(float frequencyHz, float dampingRatio, float initialResponse) {
        if (!float.IsFinite(f: frequencyHz) || (frequencyHz <= 0f)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(frequencyHz),
                message: "The natural frequency must be finite and strictly positive."
            );
        }
        if (!float.IsFinite(f: dampingRatio) || (dampingRatio < 0f)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(dampingRatio),
                message: "The damping ratio must be finite and non-negative."
            );
        }
        if (!float.IsFinite(f: initialResponse)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(initialResponse),
                message: "The initial response must be finite."
            );
        }

        var omega = ((2f * MathF.PI) * frequencyHz);
        var decayRate = (dampingRatio * omega);
        var branch = ((dampingRatio < 1f)
            ? SecondOrderDynamicsBranch.Underdamped
            : ((dampingRatio == 1f)
                ? SecondOrderDynamicsBranch.CriticallyDamped
                : SecondOrderDynamicsBranch.Overdamped));
        var oscillationRate = ((branch == SecondOrderDynamicsBranch.CriticallyDamped)
            ? 0f
            : (omega * MathF.Sqrt(x: MathF.Abs(x: (1f - (dampingRatio * dampingRatio))))));

        return new() {
            Branch = branch,
            DampingRatio = dampingRatio,
            DecayRate = decayRate,
            Frequency = frequencyHz,
            InitialResponse = initialResponse,
            OscillationRate = oscillationRate,
            Stiffness = (omega * omega),
            TargetVelocityGain = ((initialResponse * dampingRatio) / omega),
        };
    }
    /// <summary>Forms the exact pole-matched propagator for one frame's delta time.</summary>
    /// <param name="deltaSeconds">The frame's delta time, in seconds.</param>
    /// <returns><see cref="SecondOrderPropagator.Identity"/> when <paramref name="deltaSeconds"/> is non-positive or
    /// non-finite; otherwise the propagator for that interval.</returns>
    public SecondOrderPropagator Propagator(float deltaSeconds) {
        if (!float.IsFinite(f: deltaSeconds) || (deltaSeconds <= 0f)) {
            return SecondOrderPropagator.Identity;
        }

        switch (Branch) {
            case SecondOrderDynamicsBranch.CriticallyDamped: {
                    var e = MathF.Exp(x: -(DecayRate * deltaSeconds));
                    var omegaT = (DecayRate * deltaSeconds);

                    return new(
                        A11: (e * (1f + omegaT)),
                        A12: (e * deltaSeconds),
                        A21: (-Stiffness * (e * deltaSeconds)),
                        A22: (e * (1f - omegaT))
                    );
                }
            case SecondOrderDynamicsBranch.Underdamped: {
                    var e = MathF.Exp(x: -(DecayRate * deltaSeconds));
                    var angle = (OscillationRate * deltaSeconds);

                    var (sin, cos) = (MathF.Sin(x: angle), MathF.Cos(x: angle));
                    var ratio = (DecayRate / OscillationRate);
                    var eSinOverOmegaD = ((e * sin) / OscillationRate);

                    return new(
                        A11: (e * (cos + (ratio * sin))),
                        A12: eSinOverOmegaD,
                        A21: (-Stiffness * eSinOverOmegaD),
                        A22: (e * (cos - (ratio * sin)))
                    );
                }
            default: { // Overdamped: p1 = ζω − σ, p2 = ζω + σ (both positive); the poles are −p1, −p2; p1·p2 = ω².
                    var p1 = (DecayRate - OscillationRate);
                    var p2 = (DecayRate + OscillationRate);
                    var lambda1 = MathF.Exp(x: -(p1 * deltaSeconds));
                    var lambda2 = MathF.Exp(x: -(p2 * deltaSeconds));
                    var twoSigma = (2f * OscillationRate);
                    var a12 = ((lambda1 - lambda2) / twoSigma);

                    return new(
                        A11: (((p2 * lambda1) - (p1 * lambda2)) / twoSigma),
                        A12: a12,
                        A21: (-Stiffness * a12),
                        A22: (((p2 * lambda2) - (p1 * lambda1)) / twoSigma)
                    );
                }
        }
    }
    /// <summary>Advances one scalar follower lane by one propagator step, in place.</summary>
    /// <param name="position">The lane's position, updated in place.</param>
    /// <param name="velocity">The lane's velocity, updated in place.</param>
    /// <param name="target">The target held over the step.</param>
    /// <param name="targetVelocity">The target's velocity, held constant over the step.</param>
    /// <param name="propagator">The step's propagator, from <see cref="Propagator"/>.</param>
    /// <param name="targetVelocityGain">k3 — pass <see cref="TargetVelocityGain"/>.</param>
    public static void Step(ref float position, ref float velocity, float target, float targetVelocity, in SecondOrderPropagator propagator, float targetVelocityGain) {
        var xStar = (target + (targetVelocityGain * targetVelocity));
        var e = (position - xStar);
        var v = velocity;
        var nextE = ((propagator.A11 * e) + (propagator.A12 * v));
        var nextV = ((propagator.A21 * e) + (propagator.A22 * v));

        if (!float.IsFinite(f: nextE) || !float.IsFinite(f: nextV)) {
            position = xStar;
            velocity = 0f;

            return;
        }

        position = (xStar + nextE);
        velocity = nextV;
    }
}
/// <summary>
/// A zero-allocation, mutable second-order follower over a <see cref="Vector3"/> lane — the position lag a stamped
/// creation's root or a bound part rides. The caller estimates the target's velocity by differencing consecutive
/// targets, since a presentation consumer rarely has an authoritative one.
/// </summary>
public struct SecondOrderFollower3 {
    /// <summary>The follower's current value.</summary>
    public Vector3 Value;
    /// <summary>The follower's current velocity.</summary>
    public Vector3 Velocity;
    /// <summary>The target this follower last saw, used to estimate the target's velocity by differencing.</summary>
    public Vector3 PreviousTarget;
    /// <summary>Gets a value indicating whether this follower has been seeded at least once.</summary>
    public bool Seeded;

    /// <summary>Seeds the follower at rest on a target, discarding any prior state.</summary>
    /// <param name="target">The target to seed at.</param>
    public void Seed(Vector3 target) {
        Value = target;
        Velocity = Vector3.Zero;
        PreviousTarget = target;
        Seeded = true;
    }
    /// <summary>Marks the follower unseeded — the next <see cref="Step"/> re-seeds at rest rather than lagging
    /// across the gap (a hard cut).</summary>
    public void Reseed() {
        Seeded = false;
    }
    /// <summary>Advances the follower by one frame.</summary>
    /// <param name="response">The follower's derived constants.</param>
    /// <param name="deltaSeconds">The frame's delta time, in seconds.</param>
    /// <param name="target">This frame's target.</param>
    /// <returns>The follower's value after the step.</returns>
    public Vector3 Step(in SecondOrderResponse response, float deltaSeconds, Vector3 target) {
        if (!Seeded) {
            Seed(target: target);

            return Value;
        }
        if (deltaSeconds <= 0f) {
            // The target may still have moved this frame even though no time elapsed to render it (a second pack in
            // the same frame, a headless dt-0 composition step); latch it here so the NEXT frame's difference spans
            // only its own delta rather than accumulating across the skipped interval.
            PreviousTarget = target;

            return Value;
        }

        var targetVelocity = ((target - PreviousTarget) / deltaSeconds);

        PreviousTarget = target;

        var propagator = response.Propagator(deltaSeconds: deltaSeconds);
        var x = Value.X;
        var y = Value.Y;
        var z = Value.Z;
        var vx = Velocity.X;
        var vy = Velocity.Y;
        var vz = Velocity.Z;

        SecondOrderResponse.Step(ref x, ref vx, target.X, targetVelocity.X, propagator, response.TargetVelocityGain);
        SecondOrderResponse.Step(ref y, ref vy, target.Y, targetVelocity.Y, propagator, response.TargetVelocityGain);
        SecondOrderResponse.Step(ref z, ref vz, target.Z, targetVelocity.Z, propagator, response.TargetVelocityGain);

        Value = new(x: x, y: y, z: z);
        Velocity = new(x: vx, y: vy, z: vz);

        return Value;
    }
}
/// <summary>
/// A zero-allocation, mutable second-order follower over a <see cref="Vector4"/> lane — a quaternion's four
/// components, each following independently. The caller is responsible for hemisphere-matching consecutive targets
/// (so a follower never chases the long way around) and for normalizing the result before using it as a rotation.
/// </summary>
public struct SecondOrderFollower4 {
    /// <summary>The follower's current value.</summary>
    public Vector4 Value;
    /// <summary>The follower's current velocity.</summary>
    public Vector4 Velocity;
    /// <summary>The target this follower last saw, used to estimate the target's velocity by differencing.</summary>
    public Vector4 PreviousTarget;
    /// <summary>Gets a value indicating whether this follower has been seeded at least once.</summary>
    public bool Seeded;

    /// <summary>Seeds the follower at rest on a target, discarding any prior state.</summary>
    /// <param name="target">The target to seed at.</param>
    public void Seed(Vector4 target) {
        Value = target;
        Velocity = Vector4.Zero;
        PreviousTarget = target;
        Seeded = true;
    }
    /// <summary>Marks the follower unseeded — the next <see cref="Step"/> re-seeds at rest rather than lagging
    /// across the gap (a hard cut).</summary>
    public void Reseed() {
        Seeded = false;
    }
    /// <summary>Advances the follower by one frame.</summary>
    /// <param name="response">The follower's derived constants.</param>
    /// <param name="deltaSeconds">The frame's delta time, in seconds.</param>
    /// <param name="target">This frame's target — already hemisphere-matched against <see cref="PreviousTarget"/> by
    /// the caller.</param>
    /// <returns>The follower's (un-normalized) value after the step.</returns>
    public Vector4 Step(in SecondOrderResponse response, float deltaSeconds, Vector4 target) {
        if (!Seeded) {
            Seed(target: target);

            return Value;
        }
        if (deltaSeconds <= 0f) {
            // The target may still have moved this frame even though no time elapsed to render it (a second pack in
            // the same frame, a headless dt-0 composition step); latch it here so the NEXT frame's difference spans
            // only its own delta rather than accumulating across the skipped interval.
            PreviousTarget = target;

            return Value;
        }

        var targetVelocity = ((target - PreviousTarget) / deltaSeconds);

        PreviousTarget = target;

        var propagator = response.Propagator(deltaSeconds: deltaSeconds);
        var x = Value.X;
        var y = Value.Y;
        var z = Value.Z;
        var w = Value.W;
        var vx = Velocity.X;
        var vy = Velocity.Y;
        var vz = Velocity.Z;
        var vw = Velocity.W;

        SecondOrderResponse.Step(ref x, ref vx, target.X, targetVelocity.X, propagator, response.TargetVelocityGain);
        SecondOrderResponse.Step(ref y, ref vy, target.Y, targetVelocity.Y, propagator, response.TargetVelocityGain);
        SecondOrderResponse.Step(ref z, ref vz, target.Z, targetVelocity.Z, propagator, response.TargetVelocityGain);
        SecondOrderResponse.Step(ref w, ref vw, target.W, targetVelocity.W, propagator, response.TargetVelocityGain);

        Value = new(w: w, x: x, y: y, z: z);
        Velocity = new(w: vw, x: vx, y: vy, z: vz);

        return Value;
    }
}
/// <summary>The hemisphere-matched position+orientation follower step every root/part pose lag in this library
/// shares.</summary>
public static class SecondOrderPoseFollower {
    /// <summary>Steps a position follower and an orientation follower together by one frame, flipping the
    /// orientation target to the near hemisphere of the follower's own previous target first so the quaternion
    /// follower never eases the long way around.</summary>
    /// <param name="position">The position follower, stepped in place.</param>
    /// <param name="orientation">The orientation follower (over the raw <see cref="Vector4"/> components), stepped
    /// in place.</param>
    /// <param name="response">The derived response both lanes step under.</param>
    /// <param name="deltaSeconds">The frame's delta time, in seconds.</param>
    /// <param name="targetPosition">This frame's target position.</param>
    /// <param name="targetOrientation">This frame's target orientation.</param>
    /// <returns>The eased position, and the eased orientation — re-normalized, or <paramref name="targetOrientation"/>
    /// verbatim when the eased quaternion's length falls at or below the guard (an unseeded or freshly reseeded
    /// follower).</returns>
    public static (Vector3 Position, Quaternion Orientation) StepPose(
        ref SecondOrderFollower3 position,
        ref SecondOrderFollower4 orientation,
        in SecondOrderResponse response,
        float deltaSeconds,
        Vector3 targetPosition,
        Quaternion targetOrientation
    ) {
        var easedPosition = position.Step(
            deltaSeconds: deltaSeconds,
            response: in response,
            target: targetPosition
        );
        var targetVector = new Vector4(w: targetOrientation.W, x: targetOrientation.X, y: targetOrientation.Y, z: targetOrientation.Z);

        if (
            orientation.Seeded &&
            (Vector4.Dot(vector1: orientation.PreviousTarget, vector2: targetVector) < 0f)
        ) {
            targetVector = -targetVector;
        }

        var easedVector = orientation.Step(
            deltaSeconds: deltaSeconds,
            response: in response,
            target: targetVector
        );
        var easedOrientation = ((easedVector.LengthSquared() > 1e-12f)
            ? Quaternion.Normalize(value: new Quaternion(w: easedVector.W, x: easedVector.X, y: easedVector.Y, z: easedVector.Z))
            : targetOrientation);

        return (easedPosition, easedOrientation);
    }
}
