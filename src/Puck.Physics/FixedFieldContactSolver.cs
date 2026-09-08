using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// Resolves a body's compiled volumes against a scalar field — the field provider's half of
/// <see cref="IContactField"/>, over <see cref="IFieldEvaluator"/> and the sweeping half of
/// <see cref="IWorldQuery"/>.
/// </summary>
/// <remarks>
/// <para>Contact is measured, never assumed: a push direction is the field's own gradient at a confirmed penetration,
/// so a body resolves against the surface the field actually describes rather than an axis chosen in advance.</para>
/// <para>Sample cost per iteration is bounded by the volume count. A capsule spends at most 1,033 samples on its
/// non-embedded worst case — two endpoint distances, a 512-sample forward trace, a 512-sample reverse trace, one
/// midpoint distance, and a six-sample midpoint gradient; sphere and box volumes spend at most seven each.</para>
/// <para><paramref name="field"/> and <paramref name="query"/> are ordinarily the same object under two seams. They
/// are separate parameters because the two capabilities are separate: a provider that can answer a distance need not
/// be able to sweep.</para>
/// </remarks>
/// <param name="field">The scalar field the solve measures against.</param>
/// <param name="query">The sweeping surface the capsule core trace uses.</param>
/// <param name="contactSkin">The signed skin kept between a body and every surface.</param>
/// <param name="groundedThreshold">The <c>cos(maxSlope)</c> a contact normal's up-alignment must clear to ground.</param>
/// <param name="gradientProbe">The finite-difference step the up axis is sampled with.</param>
/// <param name="maxIterations">The relaxation iteration count; values below one resolve one pass.</param>
/// <param name="gradientUp">Whether the up axis comes from the field gradient rather than world <c>+Y</c>.</param>
public sealed class FixedFieldContactSolver(
    IFieldEvaluator field,
    IWorldQuery query,
    FixedQ4816 contactSkin,
    FixedQ4816 groundedThreshold,
    FixedQ4816 gradientProbe,
    int maxIterations,
    bool gradientUp
) : IContactField {
    private static readonly FixedVector3 UnitY = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedQ4816 CoreInset = FixedQ4816.FromDouble(value: 0.002);
    private readonly IFieldEvaluator m_field = field;
    private readonly FixedQ4816 m_gradientProbe = gradientProbe;
    private readonly bool m_gradientUp = gradientUp;
    private readonly FixedQ4816 m_groundedThreshold = groundedThreshold;
    private readonly int m_iterations = Math.Max(
        val1: 1,
        val2: maxIterations
    );
    private readonly IWorldQuery m_query = query;
    private readonly FixedQ4816 m_skin = contactSkin;

    /// <summary>Gets a value indicating whether the up axis is derived from the field gradient.</summary>
    public bool GradientUp => m_gradientUp;

    // A degenerate gradient after confirmed penetration means the sample point is mirror-symmetric in the field —
    // no measured surface normal exists. Eject by bare position push along reverse-of-motion (the de-tunneling
    // direction), or up for a body at rest; touch nothing else: the direction is not a measured normal, so clamping
    // velocity along it would fabricate physics, and it must never ground a body. Defensive — no shipped world's
    // geometry reaches it. The claim depends on every call site gating this to a confirmed, non-interior
    // penetration (0 <= distance < minimum) before the gradient tap that can degenerate; all three call sites
    // (ResolveSphere, ResolveBox, ResolveCore) share that gate.
    private static void ApplyDegeneratePush(ref FixedVector3 position, in FixedVector3 velocity, FixedQ4816 penetration, FixedVector3 up) {
        position += FixedContactPushMath.ComputeDegenerate(
            penetration: penetration,
            up: up,
            velocity: in velocity
        ).PositionDelta;
    }
    // Extraction push for a single embedded center (distance < 0, already confirmed by the caller's TryDistance —
    // this method pays only the direction gradient tap). Direction is the center's own gradient; a degenerate
    // (unmeasured) gradient falls back to `up` directly, never to -velocity like ApplyDegeneratePush, since an
    // embedded center's stored velocity carries no reliable de-tunneling direction. Clamps approach velocity along
    // the extraction direction like an ordinary contact push, but never grounds. Callers own the iteration's one
    // extraction authority; this method does not check or set it. Takes no lastNormal parameter by design: a body
    // ejected from inside a wall did not just get blocked by it.
    private void ApplyExtractionPush(ref FixedVector3 position, ref FixedVector3 velocity, FixedPosition coord, FixedQ4816 magnitude, FixedVector3 up) {
        var direction = (m_field.TryFieldGradient(
            epsilon: m_gradientProbe,
            gradient: out var gradient,
            position: coord
        )
            ? gradient
            : up
        );

        position += (direction * magnitude);

        var into = FixedVector3.Dot(
            left: velocity,
            right: direction
        );

        if (into < FixedQ4816.Zero) {
            velocity -= (direction * into);
        }
    }
    // world.contacts' obstruction witness tracks only a NON-walkable push (a wall, not the ground/a ramp) — a
    // standing body re-resolves its ground contact every solver iteration, so an unconditional "last push" would
    // have the ground overwrite a genuine wall push from an earlier iteration in the SAME tick and hide it again.
    private void ApplyPush(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 normal,
        FixedQ4816 penetration, FixedVector3 up, ref bool grounded, ref FixedVector3 lastNormal, ref FixedVector3 groundNormal) {
        FixedContactPushMath.Commit(
            grounded: ref grounded,
            groundNormal: ref groundNormal,
            lastNormal: ref lastNormal,
            position: ref position,
            trial: FixedContactPushMath.ComputeOrdinary(
                groundedThreshold: m_groundedThreshold,
                normal: normal,
                penetration: penetration,
                up: up,
                velocity: in velocity
            ),
            velocity: ref velocity
        );
    }
    // Commits a trial computed by TrialResolveSphere: applies its position/velocity deltas, latches grounded if
    // the trial says so (a one-way latch — never cleared, matching ApplyPush), and records the obstruction witness
    // exactly like ApplyPush's else-branch — only when the trial was non-walkable AND carries a measured normal
    // (never for a degenerate-gradient trial, matching ApplyDegeneratePush's silence on lastNormal).
    private static void CommitSpherePush(ref FixedVector3 position, ref FixedVector3 velocity, ref bool grounded, ref FixedVector3 lastNormal, ref FixedVector3 groundNormal, in SphereResolveTrial trial) {
        FixedContactPushMath.Commit(
            grounded: ref grounded,
            groundNormal: ref groundNormal,
            lastNormal: ref lastNormal,
            position: ref position,
            trial: new FixedContactPushMath.Trial(
                Grounded: trial.Grounded,
                Normal: trial.Normal,
                PositionDelta: trial.PositionDelta,
                VelocityDelta: trial.VelocityDelta
            ),
            velocity: ref velocity
        );
    }
    // Capsule extraction — the opposing-face straddle escape: a capsule can land with both spheres inside one
    // solid, straddling its Y-midplane, not only via a non-swept teleport but any live geometry mutation that
    // rebuilds and swaps the field under a standing body, or a kit collider swap. Per-sphere resolution is the
    // failure mode there: the lower center pushes toward the nearer floor face, the upper toward the roof, against
    // one shared position, netting to a stable fixed point (a transient intermediate push can even latch `grounded`
    // dishonestly). One authority replaces both: push the whole capsule one way, and only one volume claims that
    // authority per body per iteration (see the Resolve loop).
    //
    // Magnitude keys to embed depth, not midpoint clearance: the max, over whichever center(s) sample embedded, of
    // that center's own already-sampled |distance| + (radius + skin) — never the midpoint's clearance to whatever
    // surface happens to be nearest it. Keying to midpoint clearance is backwards (a clear midpoint between two
    // embedded centers means a shallow straddle, not a deep one) and admits a period-2 limit cycle: overshoot past
    // the surface on one push, then overshoot back the next iteration. Keying to the embedded center's own measured
    // depth bounds the push at the actual embed and cannot construct that cycle. A two-sided straddle takes the max
    // of both centers' depths with no further field query; a one-sided embed still needs a direction (below), which
    // is the only case that samples the midpoint.
    //
    // The position push clamps approach velocity along the extraction direction exactly like an ordinary contact
    // push, but never grounds: extraction is honest displacement, never a resolved contact. Once no center samples
    // inside geometry, the next iteration finds the ordinary per-sphere/core path clean and settles the body
    // honestly on the exterior surface it exited through.
    //
    // Direction: a two-sided straddle forces `up` rather than trusting the midpoint's own gradient, because the
    // capsule's sphere-center midpoint does not generally coincide with the straddled solid's own symmetry plane —
    // a midpoint on the lower side of an off-center straddle can still read a gradient pointing further down even
    // though the upper sphere has already crossed into the upper half, which would tunnel the body downward through
    // the floor instead of extracting it upward. `up` is authored, never zero, and is the same per-iteration value
    // TryUp resolved, so it matches gradient-derived-up worlds too. A one-sided embed keeps the midpoint-gradient
    // direction instead, since it has no opposing-authority conflict to mis-resolve, gated behind a confirming
    // TryDistance so a program with no geometry to answer never pays for a gradient tap guaranteed to fail.
    //
    // Known residual: this is a floor-biased heuristic. A two-sided straddle under a ceiling still forces `up`,
    // extracting the body through the overhead structure rather than back down into the room, and a pitched capsule
    // (not Y-aligned) breaks the "split across a Y-midplane" framing entirely, since `up` need not align with
    // either sphere's own local axis.
    private bool ExtractCapsule(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 lowerCenter, FixedVector3 upperCenter,
        FixedQ4816 lowerDistance, FixedQ4816 upperDistance, bool lowerEmbedded, bool upperEmbedded, FixedQ4816 radius, FixedVector3 up) {
        var bothEmbedded = (lowerEmbedded && upperEmbedded);
        var minimum = (radius + m_skin);
        var lowerDepth = (lowerEmbedded
            ? FixedQ4816.Abs(value: lowerDistance)
            : FixedQ4816.Zero
        );
        var upperDepth = (upperEmbedded
            ? FixedQ4816.Abs(value: upperDistance)
            : FixedQ4816.Zero
        );
        var depth = ((lowerDepth > upperDepth)
            ? lowerDepth
            : upperDepth
        );
        var magnitude = (depth + minimum);
        var midpoint = ((lowerCenter + upperCenter) / FixedQ4816.FromInteger(value: 2L));
        var coord = FixedPosition.FromLocal(local: midpoint);
        FixedVector3 direction;

        if (bothEmbedded) {
            direction = up;
        } else if (
            m_field.TryDistance(
            distance: out _,
            material: out _,
            position: coord
        ) &&
            m_field.TryFieldGradient(
            epsilon: m_gradientProbe,
            gradient: out var gradient,
            position: coord
        )
        ) {
            direction = gradient;
        } else {
            direction = up;
        }

        position += (direction * magnitude);

        var into = FixedVector3.Dot(
            left: velocity,
            right: direction
        );

        if (into < FixedQ4816.Zero) {
            velocity -= (direction * into);
        }

        return true;
    }
    private bool ResolveBox(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 conservativeMinimum, FixedVector3 up, ref bool grounded, ref bool extracted, ref FixedVector3 lastNormal, ref FixedVector3 groundNormal) {
        var center = (position + orientation.Rotate(vector: volume.Center));
        var coord = FixedPosition.FromLocal(local: center);

        if (!m_field.TryDistance(
            distance: out var distance,
            material: out _,
            position: coord
        )) {
            return false;
        }

        // The tight per-normal support needs the gradient tap, which has not run yet — pre-screen against the
        // orientation-independent worst case (Cauchy-Schwarz: no unit normal projects the half-extents past their
        // vector length) so a clean miss never pays for a gradient sample. conservativeMinimum (HalfExtents.Length +
        // m_skin) is invariant across every relaxation iteration for this volume, so Resolve hoists it into a
        // once-per-call buffer instead of re-running the fixed-point sqrt on every pass.
        if (distance >= conservativeMinimum) {
            return false;
        }

        if (distance < FixedQ4816.Zero) {
            // EMBEDDED (center strictly inside geometry — see ResolveSphere's remarks on the opposing-face straddle
            // class): extraction push along the center's own gradient (degenerate -> up), sized off the
            // conservative bound — extraction only needs to clear the outer envelope, not find the tightest
            // support. Claims the iteration's one extraction authority (see the Resolve loop) if nothing already
            // has; skips entirely (no push at all this iteration) if something already claimed it.
            if (extracted) {
                return false;
            }

            extracted = true;

            ApplyExtractionPush(
                position: ref position,
                velocity: ref velocity,
                coord: coord,
                magnitude: (FixedQ4816.Abs(value: distance) + conservativeMinimum),
                up: up
            );

            return true;
        }

        if (!m_field.TryFieldGradient(
            epsilon: m_gradientProbe,
            gradient: out var normal,
            position: coord
        )) {
            ApplyDegeneratePush(
                penetration: (conservativeMinimum - distance),
                position: ref position,
                up: up,
                velocity: in velocity
            );

            return true;
        }

        var rotation = (orientation * volume.Rotation).Normalize();
        var localNormal = rotation.RotateInverse(vector: normal);
        var support = (((FixedQ4816.Abs(value: localNormal.X) * volume.HalfExtents.X) +
                       (FixedQ4816.Abs(value: localNormal.Y) * volume.HalfExtents.Y)) +
                       (FixedQ4816.Abs(value: localNormal.Z) * volume.HalfExtents.Z));
        var minimum = (support + m_skin);

        if (distance >= minimum) {
            return false;
        }

        ApplyPush(
            groundNormal: ref groundNormal,
            grounded: ref grounded,
            lastNormal: ref lastNormal,
            normal: normal,
            penetration: (minimum - distance),
            position: ref position,
            up: up,
            velocity: ref velocity
        );

        return true;
    }
    // Resolve one capsule volume: classify both centers as embedded or not BEFORE any push commits, without
    // changing the ordinary (non-embedded) path's sequential (Gauss-Seidel) numerics. Sampling both centers from one
    // shared pre-push snapshot would also classify correctly, but would change the ordinary path's trajectory on
    // every tick where both centers are simultaneously in ordinary contact (Jacobi instead of sequential), so it is
    // not used here.
    //
    // Lower is sampled first (one bare TryDistance). If it is embedded, upper is peeked (one more bare TryDistance,
    // no push attempted) purely to tell a two-sided straddle from a one-sided embed, and the whole capsule extracts
    // via ExtractCapsule.
    //
    // If lower is not embedded, its ordinary push is computed as a trial (TrialResolveSphere) but not committed.
    // Upper is then sampled exactly once, at the position the trial would produce if committed. If that sample is
    // embedded, the trial is discarded wholesale (no position push, no velocity edit, no grounded latch) and the
    // capsule extracts via ExtractCapsule from the current, unpushed centers, reusing both already-taken samples. If
    // upper's sample is clean, the trial commits for real and upper's own ordinary resolve reuses that same sample
    // (ResolveSphere's presampledDistance parameter) — bit-identical to the sequential path, since the trial's
    // arithmetic is that path's arithmetic, just deferred by one branch.
    private bool ResolveCapsule(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedVector3 up, ref bool grounded, ref bool extracted, ref FixedVector3 lastNormal, ref FixedVector3 groundNormal) {
        var lowerCenter = (position + orientation.Rotate(vector: volume.Center));
        var lowerSampled = m_field.TryDistance(
            position: FixedPosition.FromLocal(local: lowerCenter),
            distance: out var lowerDistance,
            material: out _
        );
        var lowerEmbedded = (lowerSampled && (lowerDistance < FixedQ4816.Zero));

        if (lowerEmbedded) {
            var peekCenter = (position + orientation.Rotate(vector: volume.Endpoint));
            var upperPeeked = m_field.TryDistance(
                position: FixedPosition.FromLocal(local: peekCenter),
                distance: out var upperPeek,
                material: out _
            );
            var upperPeekEmbedded = (upperPeeked && (upperPeek < FixedQ4816.Zero));

            if (extracted) {
                return false;
            }

            extracted = true;

            return ExtractCapsule(
                position: ref position,
                velocity: ref velocity,
                lowerCenter: lowerCenter,
                upperCenter: peekCenter,
                lowerDistance: lowerDistance,
                upperDistance: upperPeek,
                lowerEmbedded: true,
                upperEmbedded: upperPeekEmbedded,
                radius: volume.Radius,
                up: up
            );
        }

        var trial = (lowerSampled
            ? TrialResolveSphere(
                center: lowerCenter,
                velocity: in velocity,
                radius: volume.Radius,
                up: up,
                distance: lowerDistance
            )
            : default
        );
        var trialPosition = (trial.Pushed
            ? (position + trial.PositionDelta)
            : position
        );
        var upperCenter = (trialPosition + orientation.Rotate(vector: volume.Endpoint));
        var upperSampled = m_field.TryDistance(
            position: FixedPosition.FromLocal(local: upperCenter),
            distance: out var upperDistance,
            material: out _
        );
        var upperEmbedded = (upperSampled && (upperDistance < FixedQ4816.Zero));

        if (upperEmbedded) {
            // Discard the trial WHOLESALE — no committed push, no grounded, no velocity edit — and classify from
            // the CURRENT (unpushed) centers: this is what defect 2 actually demands. lowerDistance/upperDistance
            // are already the correct samples for extraction's embed-depth accounting (Item 2) — no third query.
            if (extracted) {
                return false;
            }

            extracted = true;

            var currentLowerCenter = (position + orientation.Rotate(vector: volume.Center));
            var currentUpperCenter = (position + orientation.Rotate(vector: volume.Endpoint));

            return ExtractCapsule(
                position: ref position,
                velocity: ref velocity,
                lowerCenter: currentLowerCenter,
                upperCenter: currentUpperCenter,
                lowerDistance: lowerDistance,
                upperDistance: upperDistance,
                lowerEmbedded: false,
                upperEmbedded: true,
                radius: volume.Radius,
                up: up
            );
        }

        var pushed = false;

        if (trial.Pushed) {
            CommitSpherePush(
                groundNormal: ref groundNormal,
                grounded: ref grounded,
                lastNormal: ref lastNormal,
                position: ref position,
                trial: in trial,
                velocity: ref velocity
            );
            pushed = true;
        }

        if (upperSampled) {
            pushed |= ResolveSphere(
                position: ref position,
                velocity: ref velocity,
                center: upperCenter,
                radius: volume.Radius,
                up: up,
                grounded: ref grounded,
                allowEmbedExtraction: false,
                distance: out _,
                extracted: ref extracted,
                lastNormal: ref lastNormal,
                groundNormal: ref groundNormal,
                presampledDistance: upperDistance
            );
        }

        if (pushed) {
            return true;
        }

        lowerCenter = (position + orientation.Rotate(vector: volume.Center));
        var coreUpperCenter = (position + orientation.Rotate(vector: volume.Endpoint));
        var core = (coreUpperCenter - lowerCenter);
        var coreLength = core.Length;

        return (
            (coreLength > FixedQ4816.Zero) &&
            ResolveCore(
            groundNormal: ref groundNormal,
            position: ref position,
            velocity: ref velocity,
            lowerCenter: lowerCenter,
            coreLength: coreLength,
            radius: volume.Radius,
            direction: (core / coreLength),
            up: up,
            grounded: ref grounded,
            extracted: ref extracted,
            lastNormal: ref lastNormal
        )
        );
    }
    // Sweeps the capsule CORE (the segment between the two endpoint spheres) via a forward and backward SphereCast,
    // then samples the midpoint of the two hits for a contact. distance < 0 at that midpoint means the swept core
    // itself is embedded — a thin slab through the capsule waist that BOTH endpoint centers can sample clean of (see
    // ResolveCapsule's remarks): an interior sample, not an ordinary contact, so it extracts (Item 5's guard below)
    // exactly like a capsule-endpoint or standalone-volume embed rather than running the ordinary ApplyPush path,
    // which would push, clamp, AND ground from an interior sample.
    private bool ResolveCore(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 lowerCenter,
        FixedQ4816 coreLength, FixedQ4816 radius, FixedVector3 direction, FixedVector3 up, ref bool grounded, ref bool extracted, ref FixedVector3 lastNormal, ref FixedVector3 groundNormal) {
        var sweptLength = (coreLength - (CoreInset + CoreInset));

        if (sweptLength <= FixedQ4816.Zero) {
            return false;
        }

        var minimum = (radius + m_skin);
        var start = FixedPosition.FromLocal(local: (lowerCenter + (direction * CoreInset)));

        if (!m_query.SphereCast(
            dir: direction,
            hit: out var forward,
            maxDist: sweptLength,
            origin: start,
            radius: minimum
        )) {
            return false;
        }

        var end = FixedPosition.FromLocal(local: (lowerCenter + (direction * (coreLength - CoreInset))));
        var midpointDistance = (CoreInset + forward.Distance);

        if (m_query.SphereCast(
            dir: -direction,
            hit: out var backward,
            maxDist: sweptLength,
            origin: end,
            radius: minimum
        )) {
            var backwardDistance = ((coreLength - CoreInset) - backward.Distance);

            midpointDistance = ((midpointDistance + backwardDistance) / FixedQ4816.FromInteger(value: 2L));
        }

        var midpoint = FixedPosition.FromLocal(local: (lowerCenter + (direction * midpointDistance)));

        if (
            !m_field.TryDistance(
            distance: out var distance,
            material: out _,
            position: midpoint
        ) ||
            (distance >= minimum)
        ) {
            return false;
        }

        if (distance < FixedQ4816.Zero) {
            // EMBEDDED at the swept-core midpoint (see this method's remarks): extraction, not an ordinary contact —
            // never grounds, and cedes the iteration's one extraction authority (see the Resolve loop) exactly like
            // every other embedded volume/center.
            if (extracted) {
                return false;
            }

            extracted = true;

            ApplyExtractionPush(
                position: ref position,
                velocity: ref velocity,
                coord: midpoint,
                magnitude: (FixedQ4816.Abs(value: distance) + minimum),
                up: up
            );

            return true;
        }

        if (!m_field.TryFieldGradient(
            epsilon: m_gradientProbe,
            gradient: out var normal,
            position: midpoint
        )) {
            ApplyDegeneratePush(
                penetration: (minimum - distance),
                position: ref position,
                up: up,
                velocity: in velocity
            );

            return true;
        }

        ApplyPush(
            groundNormal: ref groundNormal,
            grounded: ref grounded,
            lastNormal: ref lastNormal,
            normal: normal,
            penetration: (minimum - distance),
            position: ref position,
            up: up,
            velocity: ref velocity
        );

        return true;
    }
    // Depenetrate one sphere volume from the field: sample the distance at its center (the common cost — one
    // TryDistance), and only on actual penetration take the gradient tap for the push direction. Grounds the body
    // when the surface normal's alignment with the body up clears the compiled walkable-slope threshold.
    //
    // distance < 0 means the center itself sits strictly inside geometry — reachable not just by a non-swept
    // teleport but by a live geometry mutation that rebuilds and swaps the field under a standing body, or a kit
    // collider swap; ordinary locomotion's worst-case fall penetration never closes a 0.35 radius to zero. A
    // standalone sphere volume (allowEmbedExtraction: true) extracts itself along its own gradient, claiming the
    // iteration's one extraction authority if nothing already has. A capsule's embed classification never routes
    // through this method — ResolveCapsule samples both centers directly so it can decide before any push commits —
    // so this method's embed branch (allowEmbedExtraction: false) is reachable only defensively.
    //
    // The embed branch below never touches lastNormal — it returns through ApplyExtractionPush, not ApplyPush — so
    // a body ejected from embedded geometry never reports a wall obstruction; only the ordinary confirmed-
    // penetration branch's ApplyPush call can record one.
    private bool ResolveSphere(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 center, FixedQ4816 radius, FixedVector3 up, ref bool grounded, bool allowEmbedExtraction, out FixedQ4816 distance, ref bool extracted, ref FixedVector3 lastNormal, ref FixedVector3 groundNormal, FixedQ4816? presampledDistance = null) {
        var coord = FixedPosition.FromLocal(local: center);

        if (presampledDistance is { } sampled) {
            distance = sampled;
        } else if (!m_field.TryDistance(
            distance: out distance,
            material: out _,
            position: coord
        )) {
            return false;
        }

        var minimum = (radius + m_skin);

        if (distance >= minimum) {
            return false;
        }

        if (distance < FixedQ4816.Zero) {
            if (!allowEmbedExtraction) {
                return false;
            }

            if (extracted) {
                return false;
            }

            extracted = true;

            ApplyExtractionPush(
                position: ref position,
                velocity: ref velocity,
                coord: coord,
                magnitude: (FixedQ4816.Abs(value: distance) + minimum),
                up: up
            );

            return true;
        }

        // Penetration confirmed, center outside geometry — NOW take the gradient tap for the surface normal.
        if (!m_field.TryFieldGradient(
            epsilon: m_gradientProbe,
            gradient: out var normal,
            position: coord
        )) {
            ApplyDegeneratePush(
                penetration: (minimum - distance),
                position: ref position,
                up: up,
                velocity: in velocity
            );

            return true;
        }

        ApplyPush(
            groundNormal: ref groundNormal,
            grounded: ref grounded,
            lastNormal: ref lastNormal,
            normal: normal,
            penetration: (minimum - distance),
            position: ref position,
            up: up,
            velocity: ref velocity
        );

        return true;
    }
    private static FixedQ4816 SmallestSweepRadius(ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        var smallest = FixedQ4816.MaxValue;

        foreach (ref readonly var volume in volumes) {
            var radius = ((volume.Kind == FixedBodyColliderKind.Box)
                ? FixedQ4816.Min(
                    x: volume.HalfExtents.X,
                    y: FixedQ4816.Min(
                        x: volume.HalfExtents.Y,
                        y: volume.HalfExtents.Z
                    )
                )
                : volume.Radius
            );

            if (
                (radius > FixedQ4816.Zero) &&
                (radius < smallest)
            ) {
                smallest = radius;
            }
        }
        return ((smallest == FixedQ4816.MaxValue)
            ? FixedQ4816.Zero
            : smallest
        );
    }
    // Computes the would-be ordinary push for a sphere center already confirmed not embedded (distance >= 0, sampled
    // by the caller) without applying it to position/velocity/grounded: the caller samples the other center at the
    // position this trial's PositionDelta would produce if committed, and only actually commits (CommitSpherePush)
    // once that second sample proves clean. Built from the same FixedContactPushMath calls ApplyPush and
    // ApplyDegeneratePush use, so the two can never drift from this uncommitted path.
    private SphereResolveTrial TrialResolveSphere(FixedVector3 center, in FixedVector3 velocity, FixedQ4816 radius, FixedVector3 up, FixedQ4816 distance) {
        var minimum = (radius + m_skin);

        if (distance >= minimum) {
            return default;
        }

        var coord = FixedPosition.FromLocal(local: center);
        var penetration = (minimum - distance);
        var trial = (m_field.TryFieldGradient(
            epsilon: m_gradientProbe,
            gradient: out var normal,
            position: coord
        )
            ? FixedContactPushMath.ComputeOrdinary(
                groundedThreshold: m_groundedThreshold,
                normal: normal,
                penetration: penetration,
                up: up,
                velocity: in velocity
            )
            : FixedContactPushMath.ComputeDegenerate(
                penetration: penetration,
                up: up,
                velocity: in velocity
            )
        );

        return new SphereResolveTrial(
            Grounded: trial.Grounded,
            Normal: trial.Normal,
            PositionDelta: trial.PositionDelta,
            Pushed: true,
            VelocityDelta: trial.VelocityDelta
        );
    }

    /// <summary>Queries the wrapped deterministic SDF evaluator for an unobstructed segment.</summary>
    public bool LineOfSight(in FixedVector3 from, in FixedVector3 to) =>
        m_query.LineOfSight(
            from: FixedPosition.FromLocal(local: from),
            to: FixedPosition.FromLocal(local: to)
        );
    /// <summary>Reads the field at a point the way the solver does — the <c>world.collision.probe</c> diagnostic. The
    /// gradient uses the same authored probe step the resolver walks, so the printed direction is exactly the surface
    /// normal a contact push reads. It is the body UP axis only under
    /// a gradient-derived up axis; a flat-up world's bodies integrate against constant
    /// <c>+Y</c> regardless of what this prints.</summary>
    /// <param name="position">The world-space point to sample.</param>
    /// <param name="distance">The signed nearest-surface distance (negative inside geometry), when the field answered.</param>
    /// <param name="material">The nearest surface's material id, when the field answered.</param>
    /// <param name="gradient">The unit gradient (up direction), or <see cref="FixedVector3.Zero"/> on a degenerate query.</param>
    /// <returns><see langword="true"/> when the field has geometry to answer against.</returns>
    public bool Probe(in FixedVector3 position, out FixedQ4816 distance, out int material, out FixedVector3 gradient) {
        var coord = FixedPosition.FromLocal(local: position);

        gradient = FixedVector3.Zero;

        if (!m_field.TryDistance(
            distance: out distance,
            material: out material,
            position: coord
        )) {
            return false;
        }

        _ = m_field.TryFieldGradient(
            epsilon: m_gradientProbe,
            gradient: out gradient,
            position: coord
        );

        return true;
    }
    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
        var grounded = false;
        var groundNormal = FixedVector3.Zero;
        var lastNormal = FixedVector3.Zero;
        // Box conservative bounds are invariant across every iteration below (same volume.HalfExtents, same m_skin),
        // so the fixed-point sqrt behind FixedVector3.Length runs once per box volume here instead of once per
        // iteration per box volume; non-box slots stay unread. The stack buffer is bounded — IContactField.Resolve
        // declares no span-length ceiling, so an oversized caller falls back to the heap instead of growing the
        // stack with its input.
        const int StackVolumeBudget = 64;
        var boxConservativeMinimum = ((volumes.Length <= StackVolumeBudget)
            ? stackalloc FixedQ4816[StackVolumeBudget]
            : new FixedQ4816[volumes.Length]
        )[..volumes.Length];

        for (var index = 0; (index < volumes.Length); index++) {
            if (volumes[index].Kind == FixedBodyColliderKind.Box) {
                boxConservativeMinimum[index] = (volumes[index].HalfExtents.Length + m_skin);
            }
        }

        for (var iteration = 0; (iteration < m_iterations); iteration++) {
            // A field authoring its own up samples it HERE, per iteration and at the body's current position: that is
            // what makes a gradient-derived walker follow the surface it is being extracted from. Only a caller with
            // no such field of its own imposes the axis.
            if (!(m_gradientUp && TryUp(
                position: in position,
                up: out var iterationUp
            ))) {
                iterationUp = up;
            }


            var pushed = false;
            // One extraction authority per body per iteration: a FromCreation collider compiles up to
            // WorldCollider.MaxVolumes (16) sphere/box volumes, all sharing one position, so extracting more than
            // one per iteration would tug-of-war. The first embedded volume this iteration claims extraction; every
            // volume visited after it skips its own extraction and runs only ordinary non-embedded handling this
            // pass. A skipped volume is re-classified next iteration against wherever the claiming volume moved
            // the body.
            var extracted = false;
            var volumeIndex = 0;

            foreach (ref readonly var volume in volumes) {
                pushed |= volume.Kind switch {
                    FixedBodyColliderKind.Sphere => ResolveSphere(
                    groundNormal: ref groundNormal,
                    position: ref position,
                    velocity: ref velocity,
                    center: (position + orientation.Rotate(vector: volume.Center)),
                    radius: volume.Radius,
                    up: iterationUp,
                    grounded: ref grounded,
                    allowEmbedExtraction: true,
                    distance: out _,
                    extracted: ref extracted,
                    lastNormal: ref lastNormal
                ),
                    FixedBodyColliderKind.Capsule => ResolveCapsule(
                    extracted: ref extracted,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    orientation: in orientation,
                    position: ref position,
                    up: iterationUp,
                    velocity: ref velocity,
                    volume: in volume
                ),
                    FixedBodyColliderKind.Box => ResolveBox(
                    conservativeMinimum: boxConservativeMinimum[volumeIndex],
                    extracted: ref extracted,
                    groundNormal: ref groundNormal,
                    grounded: ref grounded,
                    lastNormal: ref lastNormal,
                    orientation: in orientation,
                    position: ref position,
                    up: iterationUp,
                    velocity: ref velocity,
                    volume: in volume
                ),
                    _ => throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}."),
                };
                volumeIndex++;
            }

            if (!pushed) {
                break;
            }
        }

        return new ContactResolution(
            GroundNormal: groundNormal,
            Grounded: grounded,
            ObstructionNormal: lastNormal
        );
    }
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
        // An endpoint inside a thin floor has an ambiguous nearest gradient; near an edge it can point sideways or
        // downward. Walk the deterministic segment until the ordinary endpoint solver first reports contact. That
        // sample is still on the approached exterior, so its measured normal resolves the same top face the body
        // actually reached instead of extracting through an arbitrary nearer side.
        var delta = (position - previousPosition);
        var distance = delta.Length;
        var stepLength = SmallestSweepRadius(volumes: volumes);

        if (
            (distance <= stepLength) ||
            (stepLength <= FixedQ4816.Zero)
        ) {
            return Resolve(
                orientation: in orientation,
                position: ref position,
                up: in up,
                velocity: ref velocity,
                volumes: volumes
            );
        }

        var steps = Math.Max(
            val1: 2,
            val2: checked((int)(((distance.Value + stepLength.Value) - 1L) / stepLength.Value))
        );
        var denominator = FixedQ4816.FromInteger(value: steps);
        var originalVelocity = velocity;

        for (var step = 1; (step <= steps); step++) {
            var proposed = (previousPosition + (delta * (FixedQ4816.FromInteger(value: step) / denominator)));
            var candidate = proposed;
            var candidateVelocity = originalVelocity;
            var resolution = Resolve(
                orientation: in orientation,
                position: ref candidate,
                up: in up,
                velocity: ref candidateVelocity,
                volumes: volumes
            );

            if (
                resolution.Grounded ||
                (resolution.ObstructionNormal != FixedVector3.Zero) ||
                (candidate != proposed)
            ) {
                position = candidate;
                velocity = candidateVelocity;
                return resolution;
            }
        }

        return Resolve(
            orientation: in orientation,
            position: ref position,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    }
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        // Gradient-derived ambient up is authored (WorldContactRequirement.GradientDerivedUp), never assumed. When
        // disabled this provider answers +Y at zero field-query cost; a caller may still pass another resolved up to
        // the contact solve (for example, one opposed to authored gravity).
        if (!m_gradientUp) {
            up = UnitY;

            return true;
        }

        if (m_field.TryFieldGradient(
            position: FixedPosition.FromLocal(local: position),
            epsilon: m_gradientProbe,
            gradient: out var gradient
        )) {
            // The gradient is the direction of steepest distance INCREASE — the direction pointing directly away from the
            // nearest surface, i.e. UP. A grounded body's gravity opposes it and the standing test aligns against it.
            up = gradient;

            return true;
        }

        up = UnitY;

        return false;
    }

    // The uncommitted outcome of an ordinary (confirmed non-embedded) sphere push — see TrialResolveSphere. Pushed
    // is false for a clean miss (distance >= minimum); the deltas are meaningless in that case and left default.
    // Normal is null exactly when FixedContactPushMath.ComputeDegenerate produced the trial — CommitSpherePush reads
    // it to record the obstruction witness the same way ApplyPush does, never fabricating a normal for a degenerate
    // push.
    private readonly record struct SphereResolveTrial(bool Pushed, FixedVector3 PositionDelta, FixedVector3 VelocityDelta, bool Grounded, FixedVector3? Normal);
}
