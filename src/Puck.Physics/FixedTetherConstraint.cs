using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// The rope half of the grapple primitive (a body constrained to an anchor): a distance-CAP constraint between a
/// body and an anchor point, never a distance-PIN — the rope may go slack, but never stretch past <see cref="Length"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caller-supplied geometry, like every other kernel in this library.</b> The constraint acquires no body identity
/// and no absolute anchor identity: <see cref="Solve"/> takes a resolved <see cref="FixedVector3"/> anchor position,
/// never a body index. An anchor that is itself another body's local frame is resolved by the caller each tick — see
/// <see cref="ResolveAnchor"/> — reading that body's CURRENT pose, exactly like a contact candidate's witness geometry
/// is resolved by the caller before it ever reaches a solver.
/// </para>
/// <para>
/// <b>One-way by construction.</b> <see cref="Solve"/> takes the anchor by <see langword="in"/> and never writes it:
/// an anchor body can drag a tethered body, but nothing here can ever push back on the anchor. A caller that wants a
/// two-way rope between two dynamic bodies solves it twice, once per body, with the other body's CURRENT pose as the
/// resolved anchor each time — deliberately not a service this type performs, so the direction of drag is always
/// legible at the call site rather than implied by argument order.
/// </para>
/// <para>
/// <b>Slack is an exact no-op.</b> While the body sits at or inside <see cref="Length"/> from the anchor,
/// <see cref="Solve"/> returns without touching position or velocity at all —
/// not even a rounding pass. The branch is taken on the SQUARED distance against the squared length (both exact
/// fixed-point products), never a square root, so the slack/taut boundary is never blurred by <see cref="FixedQ4816.Sqrt"/>
/// rounding pushing a genuinely-slack state into the taut branch.
/// </para>
/// <para>
/// <b>Taut removes only the outward radial velocity component.</b> Once the distance exceeds <see cref="Length"/>,
/// the position is projected back onto the sphere of radius <see cref="Length"/> around the anchor, and
/// only the component of velocity pointing away from the anchor is removed — mirroring
/// <c>Puck.World.Server.WorldBody.ApplyDynamicContact</c>'s "only remove the component driving into the surface" rule,
/// with inward and outward swapped: a tether contains the body FROM OUTSIDE the sphere, an ordinary contact excludes
/// it FROM INSIDE a solid. The tangential component — everything perpendicular to the anchor direction — is left
/// bit-for-bit untouched, which is what turns a taut rope into a pendulum rather than a stop: momentum along the
/// sphere's surface survives exactly, so a swing, a wall-kick's redirected momentum, or a rope wrapping around a
/// corner can all emerge from ordinary integration against a moving anchor rather than being scripted.
/// </para>
/// <para>
/// <b>No iteration.</b> This is a single constraint against a single anchor, solved in closed form each call — there
/// is no manifold to relax and therefore no iteration count to accept as a parameter, unlike
/// <see cref="FixedRigidSolver"/>'s multi-contact relaxation.
/// </para>
/// <para>
/// <b>Where to call it.</b> Solve a tether as a LATE correction on an already-integrated body — after its ordinary
/// motion integration and after any ordinary contact resolution have both run, exactly where a dynamic-body contact
/// correction runs today (see <c>Puck.World.Server.WorldPopulation.ResolveDynamicContacts</c>, invoked once per tick
/// after every body's own <c>Advance</c>). A body-anchored tether's anchor should be resolved from the SAME pass —
/// after every body has advanced this tick — so a moving anchor drags its tethered body within the same tick it
/// itself moved, never one tick stale.
/// </para>
/// </remarks>
public struct FixedTetherConstraint {
    private FixedRateAccumulator m_lengthAccumulator;

    /// <summary>The current rope length: the distance cap <see cref="Solve"/> enforces. Mutated only by
    /// <see cref="Reel"/>.</summary>
    public FixedQ4816 Length { get; private set; }
    /// <summary>The floor <see cref="Reel"/> clamps <see cref="Length"/> to. Reeling out (lengthening) has no
    /// declared ceiling here — a caller that wants one clamps its own reel rate/duration before calling
    /// <see cref="Reel"/>.</summary>
    public FixedQ4816 MinLength { get; }

    /// <summary>Captures the complete deterministic reel state. The accumulator remainder is authoritative: dropping
    /// it can make the next non-tick-exact reel advance differ by one raw fixed-point unit.</summary>
    /// <returns>The current rope limits and integration remainder.</returns>
    public readonly FixedTetherConstraintState CaptureState() => new(
        Length: Length,
        MinLength: MinLength,
        Remainder: m_lengthAccumulator.Remainder
    );
    /// <summary>Restores a previously captured deterministic reel state.</summary>
    /// <param name="state">The state produced by <see cref="CaptureState"/>.</param>
    /// <returns>A tether that continues reeling from the captured fixed-point fraction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> carries a negative minimum length, a
    /// length below that minimum, or a remainder outside the engine tick denominator.</exception>
    public static FixedTetherConstraint FromState(FixedTetherConstraintState state) {
        var tether = new FixedTetherConstraint(
            length: state.Length,
            minLength: state.MinLength
        ) {
            m_lengthAccumulator = FixedRateAccumulator.FromRemainder(
                remainder: state.Remainder,
                ticksPerSecond: checked((long)FixedTickConversion.TicksPerSecond)
            ),
        };

        return tether;
    }

    /// <summary>Constructs a tether at an initial length.</summary>
    /// <param name="length">The initial rope length. Must be at least <paramref name="minLength"/>.</param>
    /// <param name="minLength">The floor <see cref="Reel"/> clamps to. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minLength"/> is negative, or
    /// <paramref name="length"/> is less than <paramref name="minLength"/>.</exception>
    public FixedTetherConstraint(FixedQ4816 length, FixedQ4816 minLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: minLength.Value,
            paramName: nameof(minLength)
        );

        if (length.Value < minLength.Value) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(length),
                message: "The initial rope length must be at least the minimum length."
            );
        }

        Length = length;
        MinLength = minLength;
        m_lengthAccumulator = new FixedRateAccumulator(ticksPerSecond: checked((long)FixedTickConversion.TicksPerSecond));
    }

    /// <summary>Resolves a body-anchored tether's anchor point from the anchor body's CURRENT pose: the local-frame
    /// offset rotated into world axes by the anchor's orientation and added to its position. Pure and caller-supplied
    /// — this performs no body-identity lookup itself, so a caller resolves <paramref name="anchorPosition"/> and
    /// <paramref name="anchorOrientation"/> off whatever it holds body state in (see the remarks on
    /// <see cref="FixedTetherConstraint"/> for when in the tick that should happen).</summary>
    /// <param name="anchorPosition">The anchor body's current world position.</param>
    /// <param name="anchorOrientation">The anchor body's current world orientation.</param>
    /// <param name="localOffset">The anchor point in the anchor body's local frame.</param>
    /// <returns>The anchor point in world axes.</returns>
    public static FixedVector3 ResolveAnchor(in FixedVector3 anchorPosition, in FixedQuaternion anchorOrientation, in FixedVector3 localOffset) =>
        (anchorPosition + anchorOrientation.Rotate(vector: localOffset));
    /// <summary>Advances <see cref="Length"/> by <paramref name="ratePerSecond"/> over <paramref name="elapsedTicks"/>,
    /// clamped at <see cref="MinLength"/> — positive reels the rope out, negative reels it in. Integrated through a
    /// <see cref="FixedRateAccumulator"/> bound to the engine tick base, so a rate that is not an exact multiple of one
    /// raw unit per tick accumulates its remainder instead of losing it every call, the same discipline
    /// <c>Puck.World.Server.WorldBody</c>'s own per-tick accumulators follow. Call this BEFORE <see cref="Solve"/> in
    /// the same tick so the same tick's cap reflects the same tick's reel.</summary>
    /// <param name="ratePerSecond">The signed rate <see cref="Length"/> changes at, in units per second.</param>
    /// <param name="elapsedTicks">The number of engine ticks this call advances.</param>
    public void Reel(FixedQ4816 ratePerSecond, ulong elapsedTicks) {
        var delta = m_lengthAccumulator.Integrate(
            elapsedTicks: elapsedTicks,
            ratePerSecond: ratePerSecond
        );
        var next = (Length + delta);

        if (next < MinLength) {
            next = MinLength;
            // Mirrors a Gravity hold's own clamp-then-reset (WorldBody.Hold.cs's ApplyHoldGravity): once the floor
            // absorbs the remainder, keeping it banked would let a held reel-in bury an ever-growing debt that a
            // later reel-out has to pay down before the rope visibly lengthens.
            m_lengthAccumulator.Reset();
        }

        Length = next;
    }
    /// <summary>Solves the distance cap against the CURRENT tick's resolved anchor position. A no-op — bit for bit —
    /// while <paramref name="position"/> sits at or inside <see cref="Length"/> from <paramref name="anchor"/>; once
    /// beyond it, projects <paramref name="position"/> back onto the sphere of radius <see cref="Length"/> and removes
    /// only the outward radial component of <paramref name="velocity"/>, leaving every tangential component exactly as
    /// it arrived.</summary>
    /// <param name="position">The body's position (in/out).</param>
    /// <param name="velocity">The body's velocity (in/out): only the component pointing away from the anchor is ever
    /// removed, and only once the rope is taut.</param>
    /// <param name="anchor">The resolved anchor position this tick. Never written.</param>
    /// <returns>Whether the rope was taut this call.</returns>
    public readonly FixedTetherResolution Solve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedVector3 anchor) {
        var offset = (position - anchor);
        var distanceSquared = offset.LengthSquared;
        var lengthSquared = (Length * Length);

        if (distanceSquared <= lengthSquared) {
            return new FixedTetherResolution(Taut: false);
        }

        var distance = FixedQ4816.Sqrt(value: distanceSquared);
        var radial = (offset / distance);

        position = (anchor + (radial * Length));

        var radialSpeed = FixedVector3.Dot(
            left: velocity,
            right: radial
        );

        if (radialSpeed > FixedQ4816.Zero) {
            velocity -= (radial * radialSpeed);
        }

        return new FixedTetherResolution(Taut: true);
    }
}
/// <summary>The outcome of one <see cref="FixedTetherConstraint.Solve"/> call.</summary>
/// <param name="Taut">Whether the rope was at its cap this call — <see langword="false"/> means <c>Solve</c> left
/// position and velocity untouched.</param>
public readonly record struct FixedTetherResolution(bool Taut);
/// <summary>The complete deterministic state of a <see cref="FixedTetherConstraint"/>.</summary>
/// <param name="Length">The current rope length.</param>
/// <param name="MinLength">The reel-in floor.</param>
/// <param name="Remainder">The reel rate accumulator's signed remainder over the engine tick denominator.</param>
public readonly record struct FixedTetherConstraintState(FixedQ4816 Length, FixedQ4816 MinLength, long Remainder);
