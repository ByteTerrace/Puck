using System.Numerics;
using Puck.Forge.Authoring;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Queries;

namespace Puck.World.Server;

/// <summary>
/// The SDF-backed <see cref="IContactField"/> — the second provider behind the same seam the analytic
/// <see cref="WorldColliderSet"/> answers. It compiles solid screens as AXIS-ALIGNED boxes and solid placements as
/// their emitted creation primitives
/// into ONE <see cref="SdfProgram"/> and reads it through a fixed-point
/// <see cref="SdfFieldEvaluator"/>, so
/// the contact surface a body solves against IS the rendered geometry — smooth-union blends are solid where they are
/// drawn. A solid screen's contact box is axis-aligned because the renderer only ever <c>Translate</c>s a screen
/// slab — a screen's right/up is a UV frame only, never a geometry rotation (see
/// <see cref="SdfProgramBuilder"/>'s <c>ScreenSlab</c> overload doc) — and the editor picker agrees (see
/// <c>Puck.World.Client.WorldEditorPicker</c>'s own comment on the same box — this project cannot hold a
/// <c>cref</c> to it: <c>Puck.World.Server</c> is structurally unable to reference <c>Puck.World</c>). Orienting a screen's contact
/// volume for real is a THREE-surface arc — render, contact, and picker must all rotate together — and none of the
/// three does today. "Up" is world <c>+Y</c> unless the world authors
/// <see cref="WorldContactRequirement.GradientDerivedUp"/>, which derives it from the field gradient instead (a
/// planetoid, an inverted ceiling, or the inside of a sphere are all walkable); without that requirement a vertical
/// face pushes a body but never grounds it, so a flat-up world's walls stay walls.
/// </summary>
/// <remarks>
/// <para>IMMUTABLE and per-revision: it holds no per-body state, so one instance is shared by reference across all 128
/// bodies and installing a rebuild is a single reference swap on <see cref="WorldServer"/>. The wrapped
/// <see cref="SdfFieldEvaluator"/> holds only a managed <c>CompiledInstruction[]</c> (no unmanaged handle), so a replaced
/// instance needs no disposal.</para>
/// <para>The "which op can be solid" ceiling is <see cref="SdfFieldEvaluator"/>'s warp-free excluded-op set:
/// <see cref="TryBuild"/> forwards the constructor's <see cref="ArgumentException"/> message verbatim as its reject
/// reason, so <see cref="WorldServer"/> turns an unsupported solid into a LOUD apply-time rejection instead of a
/// constructor throw at install time.</para>
/// <para>Only collider-bearing kits are solved. Under <see cref="WorldContactRequirement.GradientDerivedUp"/> each
/// iteration spends six samples on the body-root up gradient (the central-difference probe); a flat-up world spends
/// zero there (the constant <c>+Y</c> short-circuits before the evaluator). A
/// capsule then spends at most 1,033 samples on its NON-EMBEDDED worst case: two endpoint distances, a 512-sample
/// forward trace, a 512-sample reverse trace, one midpoint distance, and a six-sample midpoint gradient (the march no
/// longer takes a gradient tap on the trace hits themselves — see <see cref="Puck.SdfVm.Queries.RayHit.Normal"/>'s
/// remarks). Sphere and box volumes spend at most seven samples each. Therefore the <see cref="WorldCollider.MaxVolumes"/>
/// ceiling bounds one iteration at <c>6 + (1,033 * 16) = 16,534</c> samples gradient-up (<c>16,528</c> flat-up) and the
/// validator's four-iteration shipped tuning at 66,136 (66,112 flat-up).
/// The shipped single-capsule rows carry 1,039-sample iteration and 4,156-sample step ceilings
/// gradient-up (1,033 / 4,132 flat-up). A penetrating-but-degenerate endpoint sphere (<c>0 &lt;= distance &lt;
/// minimum</c>, gradient tap unmeasurable) still costs seven samples (the confirming <c>TryDistance</c> plus the failed
/// <c>TryFieldGradient</c>) and PUSHES via the bare-position fallback rather than no-op, so the capsule ceiling is
/// unchanged: the worst case remains two clean-miss endpoint spheres plus the full core sweep (<c>2 + 512 + 512 + 1 +
/// 6</c>), and the degenerate-fallback path never exceeds it — two degenerate endpoint spheres cost fourteen samples
/// total and skip the core resolve entirely.</para>
/// <para>EMBEDDED-ITERATION COST (the opposing-face straddle fix — see <see cref="ResolveCapsule"/>,
/// <see cref="TrialResolveSphere"/>, and <see cref="ExtractCapsule"/>): <see cref="ResolveCapsule"/> classifies
/// BEFORE any COMMITTED push, but preserves the pre-hardening sequential (Gauss-Seidel) numerics bit-exactly on
/// every non-embedded tick via DEFER-COMMIT rather than sampling both centers from one shared snapshot. The lower
/// center is sampled first (one bare <c>TryDistance</c> — identical to the pre-hardening hot path's first tap). If
/// it samples embedded (<c>distance &lt; 0</c> — unreachable by ordinary locomotion alone, a 24-unit/s max fall
/// speed at 240 Hz penetrating at most 0.1 per tick against a 0.35 radius, but reachable by a live geometry mutation
/// that rebuilds and swaps the field under a standing body, or a kit collider swap, not only a non-swept teleport),
/// the upper center is PEEKED (one more bare <c>TryDistance</c>, no push attempted) and the whole capsule extracts
/// via <see cref="ExtractCapsule"/> — 2 samples total, +0 if the peek also finds upper embedded (direction is
/// <c>up</c> unconditionally, magnitude reads both already-known depths, no further field query), +7 if the peek
/// finds upper clean (a confirming <c>TryDistance</c> at the midpoint, then, only if that confirms, a six-tap
/// <c>TryFieldGradient</c> for direction) — 2 or 9 total, exactly as before. If the lower center is NOT embedded,
/// its ordinary push is computed as a TRIAL by <see cref="TrialResolveSphere"/> — the identical arithmetic
/// <see cref="ApplyPush"/>/<see cref="ApplyDegeneratePush"/> would run (same gradient tap, only on confirmed
/// penetration, up to the existing seven-sample per-sphere ceiling) — but NOT committed to
/// position/velocity/grounded yet. The upper center is then sampled EXACTLY ONCE, at the position the trial would
/// produce if committed (one bare <c>TryDistance</c> — the identical second tap the pre-hardening hot path already
/// paid, just gating a commit-or-discard decision instead of being unconditional). If that sample is embedded, the
/// trial is discarded in full (no position push, no velocity edit, no grounded latch ever lands) and the whole
/// capsule extracts via <see cref="ExtractCapsule"/> from the CURRENT, unpushed centers, reusing both already-taken
/// samples — no third field query. This is the most expensive embedded shape: up to 7 (the lower's own discarded
/// ordinary ceiling) + 1 (the upper's classify sample) + 7 (the one-sided extraction — bothEmbedded is impossible
/// here, since lower is confirmed NOT embedded) = 15, or as low as 1 + 1 + 7 = 9 when the lower's trial was a clean
/// miss. If the upper's sample is NOT embedded, the lower's trial COMMITS (position/velocity/grounded now update for
/// real) and the upper's own ordinary resolve reuses that same sample (no third query there either) — this is the
/// non-embedded hot path, and its total cost — two distance samples, gradient taps only on confirmed per-sphere
/// penetration — and its OUTPUT are bit-identical to the pre-hardening path, because the trial computes the exact
/// same formula the direct-apply path always did before either commits or discards. The 1,033-sample capsule
/// ceiling (and the 16,534 / 66,136 totals it feeds) therefore still hold exactly as documented; nothing about this
/// restructure touches the non-embedded worst case at all. A standalone sphere or box volume follows the same
/// single-sample-then-optional-seven-sample shape at its OWN center (see <see cref="ResolveSphere"/>,
/// <see cref="ResolveBox"/>), so their seven-sample ceiling is likewise unchanged.</para>
/// </remarks>
public sealed class WorldSolidField : IContactField {
    private static readonly FixedVector3 s_unitY = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedQ4816 s_coreInset = FixedQ4816.FromDouble(value: 0.002);

    private readonly SdfFieldEvaluator m_evaluator;
    private readonly FixedQ4816 m_skin;
    private readonly FixedQ4816 m_groundedThreshold;
    private readonly FixedQ4816 m_gradientProbe;
    private readonly int m_iterations;
    private readonly bool m_gradientUp;

    private WorldSolidField(SdfFieldEvaluator evaluator, int instructionCount, long placementShapeCount, WorldContactCensus census, FixedWorldCollision tuning) {
        m_evaluator = evaluator;
        InstructionCount = instructionCount;
        PlacementShapeCount = placementShapeCount;
        Census = census;
        m_skin = tuning.ContactSkin;
        m_groundedThreshold = tuning.GroundedThreshold;
        m_gradientProbe = tuning.GradientProbe;
        m_iterations = Math.Max(val1: 1, val2: tuning.MaxIterations);
        m_gradientUp = tuning.GradientUp;
    }

    /// <summary>Gets the compiled program's instruction count — the <c>world.collision.status</c> read-back (a rough size of
    /// the solid field the solver walks).</summary>
    public int InstructionCount { get; }

    /// <summary>Gets the placement primitive-shape emissions in the compiled field.</summary>
    public long PlacementShapeCount { get; }

    /// <inheritdoc/>
    public WorldContactCensus Census { get; }

    /// <summary>Re-wraps this field's ALREADY-COMPILED program with fresh solver scalars, reusing the wrapped
    /// <see cref="SdfFieldEvaluator"/> (safe to share by reference — it holds only an immutable instruction array). A
    /// <c>SetCollision</c> edit touches only the collision tuning row, never the geometry the program bakes (screens and
    /// placements), so a slope/skin/probe/iteration tweak reuses the program instead of
    /// recompiling it. The result is a distinct instance (per-revision immutability) so the install-time reference swap
    /// still bumps the revision.</summary>
    /// <param name="tuning">The recompiled collision tuning to adopt.</param>
    /// <returns>A new field over the same evaluator with the new scalars.</returns>
    public WorldSolidField WithTuning(FixedWorldCollision tuning) =>
        new(evaluator: m_evaluator, instructionCount: InstructionCount, placementShapeCount: PlacementShapeCount, census: Census, tuning: tuning);

    /// <summary>Gets the field evaluator the <c>world.collision.probe</c> verb reads distance/material/gradient from, so the
    /// surface the simulation itself solves against is directly observable.</summary>
    public IFieldEvaluator Evaluator => m_evaluator;

    /// <summary>Gets a value indicating whether this field's collision tuning authors <see cref="WorldContactRequirement.GradientDerivedUp"/>.</summary>
    public bool GradientUp => m_gradientUp;

    /// <summary>Queries the wrapped deterministic SDF evaluator for an unobstructed segment.</summary>
    public bool LineOfSight(in FixedVector3 from, in FixedVector3 to) =>
        m_evaluator.LineOfSight(from: FixedPosition.FromLocal(local: from), to: FixedPosition.FromLocal(local: to));

    /// <summary>Builds the SDF contact field from a definition WITHOUT installing it, or reports the offending op by name.</summary>
    /// <param name="definition">The world definition supplying the collision tuning and solid rows.</param>
    /// <param name="built">The built field on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The forwarded <see cref="SdfFieldEvaluator"/> reject reason when a solid names an op the
    /// warp-free evaluator cannot interpret; empty on success.</param>
    /// <returns><see langword="true"/> when the field compiled, <see langword="false"/> with a named reason otherwise.</returns>
    public static bool TryBuild(WorldDefinition definition, out WorldSolidField? built, out string reason) {
        built = null;
        reason = string.Empty;

        var tuning = FixedWorldCollision.Compile(collision: definition.Collision);
        var builder = new SdfProgramBuilder();
        var placementShapeCount = 0L;

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not { } solid) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            // The same center derivation the frame source and picker bake: the geometry box sits one HalfDepth behind the
            // lit face along the face normal.
            var normal = Vector3.Normalize(value: Vector3.Cross(vector1: screen.Right, vector2: screen.Up));
            var center = (screen.Origin - (normal * screen.HalfDepth));

            _ = builder
                .Translate(offset: center)
                .Box(halfExtents: new Vector3(x: (screen.HalfWidth + solid.Margin), y: (screen.HalfHeight + solid.Margin), z: (screen.HalfDepth + solid.Margin)), round: screen.Round, material: material)
                .ResetPoint();
        }

        foreach (var placement in definition.Placements) {
            if ((placement.Solid is not { } solid) || (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: placement.CreationId) is not { } creation)) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (placement.YawDegrees * (MathF.PI / 180f)));

            CreationStampLattice.ForEachInstance(
                origin: placement.Position,
                rotation: rotation,
                pattern: WorldPlacementStamp.PatternFor(placement: placement),
                mirror: WorldPlacementStamp.MirrorFor(placement: placement),
                visitor: instance => {
                    CreationStampEmitter.Emit(
                        builder: builder,
                        document: creation.Document,
                        transform: new CreationStampTransform(
                            Origin: instance.Origin,
                            Rotation: rotation,
                            Scale: placement.Scale,
                            ReflectionNormal: instance.ReflectionNormal
                        ),
                        materialFor: _ => material,
                        contactMargin: solid.Margin
                    );
                    placementShapeCount += (creation.Document.Shapes?.Count ?? 0);
                }
            );
        }

        var program = builder.Build(buildInstanceGrid: false);
        SdfFieldEvaluator evaluator;

        try {
            evaluator = new SdfFieldEvaluator(program: program);
        } catch (ArgumentException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        built = new WorldSolidField(
            evaluator: evaluator,
            instructionCount: program.Instructions.Count,
            placementShapeCount: placementShapeCount,
            census: WorldColliderSet.Measure(definition: definition),
            tuning: tuning
        );

        return true;
    }

    /// <summary>Reads the field at a point the way the solver does — the <c>world.collision.probe</c> diagnostic. The
    /// gradient uses the SAME authored probe step the resolver walks, so the printed direction is exactly the surface
    /// normal a contact push reads. It is the body UP axis only under
    /// <see cref="WorldContactRequirement.GradientDerivedUp"/>; a flat-up world's bodies integrate against constant
    /// <c>+Y</c> regardless of what this prints.</summary>
    /// <param name="position">The world-space point to sample.</param>
    /// <param name="distance">The signed nearest-surface distance (negative inside geometry), when the field answered.</param>
    /// <param name="material">The nearest surface's material id, when the field answered.</param>
    /// <param name="gradient">The unit gradient (up direction), or <see cref="FixedVector3.Zero"/> on a degenerate query.</param>
    /// <returns><see langword="true"/> when the field has geometry to answer against.</returns>
    public bool Probe(in FixedVector3 position, out FixedQ4816 distance, out int material, out FixedVector3 gradient) {
        var coord = FixedPosition.FromLocal(local: position);

        gradient = FixedVector3.Zero;

        if (!m_evaluator.TryDistance(position: coord, distance: out distance, material: out material)) {
            return false;
        }

        _ = m_evaluator.TryFieldGradient(position: coord, epsilon: m_gradientProbe, gradient: out gradient);

        return true;
    }

    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        var grounded = false;
        var lastNormal = FixedVector3.Zero;

        for (var iteration = 0; (iteration < m_iterations); iteration++) {
            if (!TryUp(position: in position, up: out var up)) {
                break;
            }

            var pushed = false;
            // ONE extraction authority per BODY per iteration: a FromCreation collider compiles up to
            // WorldCollider.MaxVolumes (16) sphere/box volumes, and each used to extract along its own gradient
            // against the SAME shared position — an N-way tug-of-war. The first embedded volume this iteration
            // claims extraction (sets this true); every volume visited after it — even if it is itself embedded —
            // skips its own extraction and runs only its ordinary non-embedded handling this pass. A skipped
            // volume is not lost: the next iteration re-classifies it against wherever the claiming volume's
            // extraction moved the body, and picks it up once it is the one that still needs to move.
            var extracted = false;

            foreach (var volume in volumes) {
                pushed |= volume.Kind switch {
                    FixedBodyColliderKind.Sphere => ResolveSphere(
                        position: ref position,
                        velocity: ref velocity,
                        center: (position + orientation.Rotate(vector: volume.Center)),
                        radius: volume.Radius,
                        up: up,
                        grounded: ref grounded,
                        allowEmbedExtraction: true,
                        distance: out _,
                        extracted: ref extracted,
                        lastNormal: ref lastNormal
                    ),
                    FixedBodyColliderKind.Capsule => ResolveCapsule(
                        position: ref position,
                        velocity: ref velocity,
                        orientation: in orientation,
                        volume: in volume,
                        up: up,
                        grounded: ref grounded,
                        extracted: ref extracted,
                        lastNormal: ref lastNormal
                    ),
                    FixedBodyColliderKind.Box => ResolveBox(
                        position: ref position,
                        velocity: ref velocity,
                        orientation: in orientation,
                        volume: in volume,
                        up: up,
                        grounded: ref grounded,
                        extracted: ref extracted,
                        lastNormal: ref lastNormal
                    ),
                    _ => throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}."),
                };
            }

            if (!pushed) {
                break;
            }
        }

        return new ContactResolution(Grounded: grounded, ObstructionNormal: lastNormal);
    }

    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        // Gradient-derived up is authored (WorldContactRequirement.GradientDerivedUp), never assumed: a flat-up world
        // keeps world +Y so its walls push without ever grounding, at zero field-query cost.
        if (!m_gradientUp) {
            up = s_unitY;

            return true;
        }

        if (m_evaluator.TryFieldGradient(position: FixedPosition.FromLocal(local: position), epsilon: m_gradientProbe, gradient: out var gradient)) {
            // The gradient is the direction of steepest distance INCREASE — the direction pointing directly away from the
            // nearest surface, i.e. UP. A grounded body's gravity opposes it and the standing test aligns against it.
            up = gradient;

            return true;
        }

        up = s_unitY;

        return false;
    }

    // Depenetrate one sphere volume from the field: sample the distance at its center (the COMMON cost — one
    // TryDistance), and only on actual penetration take the gradient tap for the push direction. Grounds the body
    // when the surface normal's alignment with the body up clears the compiled walkable-slope threshold.
    //
    // distance < 0 means the CENTER ITSELF sits strictly inside geometry — a DEEP-EMBED instance of the
    // opposing-face straddle class (see class remarks), not a full discriminator for it: an OPPOSING SHALLOW
    // penetration — a slab roughly 0.26-0.74 of the capsule's core length thick, crossing the waist so BOTH centers
    // still sample distance >= 0 — still two-cycles under ordinary per-sphere resolution and this check does not see
    // it (detectable only by comparing the two contact normals; deliberately NOT handled here — ledgered with the
    // campaign, not this wave). Ordinary locomotion's worst-case fall penetration (24-unit/s max fall / 240 Hz = 0.1
    // per tick) never closes a 0.35 radius to zero, so the deep-embed case is unreachable by falling alone — but IS
    // reachable by a live geometry mutation that rebuilds and swaps the field under a standing body, or a kit
    // collider swap, not only a non-swept teleport. A STANDALONE sphere volume (allowEmbedExtraction: true)
    // extracts itself along its own gradient below, claiming the iteration's one extraction authority (see the
    // Resolve loop) if nothing already has. A capsule's embed CLASSIFICATION never routes through this method —
    // ResolveCapsule samples both centers directly so it can decide before any push commits (see its remarks); this
    // method's embed branch (allowEmbedExtraction: false) exists only as a defensive report path and is never
    // actually reachable from ResolveCapsule's calls into it, since those only ever run once a center is already
    // confirmed clean.
    //
    // JUDGMENT CALL (extraction vs. the obstruction witness): the embed branch below never touches lastNormal — it
    // returns through ApplyExtractionPush, not ApplyPush — so a body EJECTED from embedded geometry never reports a
    // wall obstruction; only the ordinary confirmed-penetration branch's ApplyPush call (below) can record one.
    private bool ResolveSphere(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 center, FixedQ4816 radius, FixedVector3 up, ref bool grounded, bool allowEmbedExtraction, out FixedQ4816 distance, ref bool extracted, ref FixedVector3 lastNormal, FixedQ4816? presampledDistance = null) {
        var coord = FixedPosition.FromLocal(local: center);

        if (presampledDistance is { } sampled) {
            distance = sampled;
        } else if (!m_evaluator.TryDistance(position: coord, distance: out distance, material: out _)) {
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

            ApplyExtractionPush(position: ref position, velocity: ref velocity, coord: coord, magnitude: (FixedQ4816.Abs(value: distance) + minimum), up: up);

            return true;
        }

        // Penetration confirmed, center outside geometry — NOW take the gradient tap for the surface normal.
        if (!m_evaluator.TryFieldGradient(position: coord, epsilon: m_gradientProbe, gradient: out var normal)) {
            ApplyDegeneratePush(position: ref position, velocity: in velocity, penetration: (minimum - distance), up: up);

            return true;
        }

        ApplyPush(position: ref position, velocity: ref velocity, normal: normal, penetration: (minimum - distance), up: up, grounded: ref grounded, lastNormal: ref lastNormal);

        return true;
    }

    // The uncommitted outcome of an ordinary (confirmed non-embedded) sphere push — see TrialResolveSphere. Pushed
    // is false for a clean miss (distance >= minimum); the deltas are meaningless in that case and left default.
    // Normal mirrors ApplyPush's measured surface normal and is null exactly when the degenerate-gradient branch
    // (ApplyDegeneratePush's mirror) ran — CommitSpherePush reads it to record the obstruction witness the same way
    // ApplyPush does, never fabricating a normal for a degenerate push.
    private readonly record struct SphereResolveTrial(bool Pushed, FixedVector3 PositionDelta, FixedVector3 VelocityDelta, bool Grounded, FixedVector3? Normal);

    // Computes the WOULD-BE ordinary push for a sphere center ALREADY CONFIRMED not embedded (distance >= 0,
    // sampled by the caller) WITHOUT applying it to position/velocity/grounded — the DEFER-COMMIT half of
    // ResolveCapsule's classify-before-any-COMMITTED-push restructure (see its remarks): the caller samples the
    // OTHER center at the position this trial's PositionDelta would produce if committed, and only actually commits
    // (CommitSpherePush) once that second sample proves clean, which is what keeps the ordinary path's numerics
    // bit-identical to the pre-hardening direct-apply shape on every tick where nothing ends up embedded. The
    // arithmetic below is intentionally a byte-for-byte mirror of ApplyPush/ApplyDegeneratePush — same branches,
    // same formulas, same gradient-only-on-penetration cost — so a committed trial reproduces exactly what the
    // direct-apply path would have done; a change to either of those two methods must be mirrored here too.
    private SphereResolveTrial TrialResolveSphere(FixedVector3 center, in FixedVector3 velocity, FixedQ4816 radius, FixedVector3 up, FixedQ4816 distance) {
        var minimum = (radius + m_skin);

        if (distance >= minimum) {
            return default;
        }

        var coord = FixedPosition.FromLocal(local: center);
        var penetration = (minimum - distance);

        if (!m_evaluator.TryFieldGradient(position: coord, epsilon: m_gradientProbe, gradient: out var normal)) {
            var direction = (-velocity).Normalize();

            if (direction == FixedVector3.Zero) {
                direction = up;
            }

            return new SphereResolveTrial(Pushed: true, PositionDelta: (direction * penetration), VelocityDelta: FixedVector3.Zero, Grounded: false, Normal: null);
        }

        var grounded = (FixedVector3.Dot(left: normal, right: up) >= m_groundedThreshold);
        var into = FixedVector3.Dot(left: velocity, right: normal);
        var velocityDelta = ((into < FixedQ4816.Zero) ? -(normal * into) : FixedVector3.Zero);

        return new SphereResolveTrial(Pushed: true, PositionDelta: (normal * penetration), VelocityDelta: velocityDelta, Grounded: grounded, Normal: normal);
    }

    // Commits a trial computed by TrialResolveSphere: applies its position/velocity deltas, latches grounded if
    // the trial says so (a one-way latch — never cleared, matching ApplyPush), and records the obstruction witness
    // exactly like ApplyPush's else-branch — only when the trial was non-walkable AND carries a measured normal
    // (never for a degenerate-gradient trial, matching ApplyDegeneratePush's silence on lastNormal).
    private static void CommitSpherePush(ref FixedVector3 position, ref FixedVector3 velocity, ref bool grounded, ref FixedVector3 lastNormal, in SphereResolveTrial trial) {
        position += trial.PositionDelta;
        velocity += trial.VelocityDelta;

        if (trial.Grounded) {
            grounded = true;
        } else if (trial.Normal is { } normal) {
            lastNormal = normal;
        }
    }

    // Resolve one capsule volume: DEFER-COMMIT — classify before any push COMMITS, without changing the ordinary
    // (non-embedded) path's numerics. The earlier shape ran the lower sphere's ORDINARY resolve — push + velocity
    // clamp + grounded latch, applied for real — before it knew whether the upper was embedded, so a one-sided
    // embed could commit an ordinary push AND then run an extraction in the opposite direction in the same pass,
    // with grounded latched dishonestly (defect 2). Sampling both centers from one shared pre-push snapshot would
    // fix that too, but changes the ordinary path's trajectory on every tick where both centers are simultaneously
    // in ordinary contact (Jacobi instead of the pre-hardening sequential/Gauss-Seidel scheme) — rejected, because
    // the fix does not require it.
    //
    // Lower is sampled first (one bare TryDistance — identical to the pre-hardening hot path's first tap). If it is
    // embedded (see ResolveSphere's remarks on the opposing-face straddle class), upper is PEEKED (one more bare
    // TryDistance, no push attempted) purely to tell a two-sided straddle from a one-sided embed, and the whole
    // capsule extracts via ExtractCapsule — unchanged from before.
    //
    // If lower is NOT embedded, its ordinary push is computed as a TRIAL (TrialResolveSphere) — not committed yet.
    // Upper is then sampled EXACTLY ONCE, at the position the trial WOULD produce if committed — the identical
    // second tap the pre-hardening hot path already paid, now gating a decision instead of being unconditional. If
    // that sample is embedded, the trial is discarded WHOLESALE (no position push, no velocity edit, no grounded
    // latch ever lands) and the capsule extracts via ExtractCapsule from the CURRENT, unpushed centers, reusing both
    // already-taken samples — classification before any COMMITTED push, which is what defect 2 actually demands,
    // without touching the ordinary path's cost or numerics. If upper's sample is clean, the trial COMMITS for
    // real and upper's own ordinary resolve reuses that same sample (ResolveSphere's presampledDistance parameter)
    // — bit-identical to the pre-hardening sequential path, because the trial's arithmetic IS that path's arithmetic,
    // just deferred by one branch.
    private bool ResolveCapsule(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedVector3 up, ref bool grounded, ref bool extracted, ref FixedVector3 lastNormal) {
        var lowerCenter = (position + orientation.Rotate(vector: volume.Center));
        var lowerSampled = m_evaluator.TryDistance(position: FixedPosition.FromLocal(local: lowerCenter), distance: out var lowerDistance, material: out _);
        var lowerEmbedded = (lowerSampled && (lowerDistance < FixedQ4816.Zero));

        if (lowerEmbedded) {
            var peekCenter = (position + orientation.Rotate(vector: volume.Endpoint));
            var upperPeeked = m_evaluator.TryDistance(position: FixedPosition.FromLocal(local: peekCenter), distance: out var upperPeek, material: out _);
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
            ? TrialResolveSphere(center: lowerCenter, velocity: in velocity, radius: volume.Radius, up: up, distance: lowerDistance)
            : default);
        var trialPosition = (trial.Pushed ? (position + trial.PositionDelta) : position);
        var upperCenter = (trialPosition + orientation.Rotate(vector: volume.Endpoint));
        var upperSampled = m_evaluator.TryDistance(position: FixedPosition.FromLocal(local: upperCenter), distance: out var upperDistance, material: out _);
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
            CommitSpherePush(position: ref position, velocity: ref velocity, grounded: ref grounded, lastNormal: ref lastNormal, trial: in trial);
            pushed = true;
        }

        if (upperSampled) {
            pushed |= ResolveSphere(position: ref position, velocity: ref velocity, center: upperCenter, radius: volume.Radius, up: up, grounded: ref grounded, allowEmbedExtraction: false, distance: out _, extracted: ref extracted, lastNormal: ref lastNormal, presampledDistance: upperDistance);
        }

        if (pushed) {
            return true;
        }

        lowerCenter = (position + orientation.Rotate(vector: volume.Center));
        var coreUpperCenter = (position + orientation.Rotate(vector: volume.Endpoint));
        var core = (coreUpperCenter - lowerCenter);
        var coreLength = core.Length;

        return ((coreLength > FixedQ4816.Zero) && ResolveCore(
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
        ));
    }

    private bool ResolveBox(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedVector3 up, ref bool grounded, ref bool extracted, ref FixedVector3 lastNormal) {
        var center = (position + orientation.Rotate(vector: volume.Center));
        var coord = FixedPosition.FromLocal(local: center);

        if (!m_evaluator.TryDistance(position: coord, distance: out var distance, material: out _)) {
            return false;
        }

        // The tight per-normal support needs the gradient tap, which has not run yet — pre-screen against the
        // orientation-independent worst case (Cauchy-Schwarz: no unit normal projects the half-extents past their
        // vector length) so a clean miss never pays for a gradient sample.
        var conservativeMinimum = (volume.HalfExtents.Length + m_skin);

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

            ApplyExtractionPush(position: ref position, velocity: ref velocity, coord: coord, magnitude: (FixedQ4816.Abs(value: distance) + conservativeMinimum), up: up);

            return true;
        }

        if (!m_evaluator.TryFieldGradient(position: coord, epsilon: m_gradientProbe, gradient: out var normal)) {
            ApplyDegeneratePush(position: ref position, velocity: in velocity, penetration: (conservativeMinimum - distance), up: up);

            return true;
        }

        var rotation = (orientation * volume.Rotation).Normalize();
        var localNormal = rotation.RotateInverse(vector: normal);
        var support = ((FixedQ4816.Abs(value: localNormal.X) * volume.HalfExtents.X) +
                       (FixedQ4816.Abs(value: localNormal.Y) * volume.HalfExtents.Y) +
                       (FixedQ4816.Abs(value: localNormal.Z) * volume.HalfExtents.Z));
        var minimum = (support + m_skin);

        if (distance >= minimum) {
            return false;
        }

        ApplyPush(position: ref position, velocity: ref velocity, normal: normal, penetration: (minimum - distance), up: up, grounded: ref grounded, lastNormal: ref lastNormal);

        return true;
    }

    // Sweeps the capsule CORE (the segment between the two endpoint spheres) via a forward and backward SphereCast,
    // then samples the midpoint of the two hits for a contact. distance < 0 at that midpoint means the swept core
    // itself is embedded — a thin slab through the capsule waist that BOTH endpoint centers can sample clean of (see
    // ResolveCapsule's remarks): an interior sample, not an ordinary contact, so it extracts (Item 5's guard below)
    // exactly like a capsule-endpoint or standalone-volume embed rather than running the ordinary ApplyPush path,
    // which would push, clamp, AND ground from an interior sample.
    private bool ResolveCore(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 lowerCenter,
        FixedQ4816 coreLength, FixedQ4816 radius, FixedVector3 direction, FixedVector3 up, ref bool grounded, ref bool extracted, ref FixedVector3 lastNormal) {
        var sweptLength = (coreLength - (s_coreInset + s_coreInset));

        if (sweptLength <= FixedQ4816.Zero) {
            return false;
        }

        var minimum = (radius + m_skin);
        var start = FixedPosition.FromLocal(local: (lowerCenter + (direction * s_coreInset)));

        if (!m_evaluator.SphereCast(origin: start, dir: direction, radius: minimum, maxDist: sweptLength, hit: out var forward)) {
            return false;
        }

        var end = FixedPosition.FromLocal(local: (lowerCenter + (direction * (coreLength - s_coreInset))));
        var midpointDistance = (s_coreInset + forward.Distance);

        if (m_evaluator.SphereCast(origin: end, dir: -direction, radius: minimum, maxDist: sweptLength, hit: out var backward)) {
            var backwardDistance = ((coreLength - s_coreInset) - backward.Distance);
            midpointDistance = ((midpointDistance + backwardDistance) / FixedQ4816.FromInteger(value: 2L));
        }

        var midpoint = FixedPosition.FromLocal(local: (lowerCenter + (direction * midpointDistance)));

        if (!m_evaluator.TryDistance(position: midpoint, distance: out var distance, material: out _) || (distance >= minimum)) {
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

            ApplyExtractionPush(position: ref position, velocity: ref velocity, coord: midpoint, magnitude: (FixedQ4816.Abs(value: distance) + minimum), up: up);

            return true;
        }

        if (!m_evaluator.TryFieldGradient(position: midpoint, epsilon: m_gradientProbe, gradient: out var normal)) {
            ApplyDegeneratePush(position: ref position, velocity: in velocity, penetration: (minimum - distance), up: up);

            return true;
        }

        ApplyPush(position: ref position, velocity: ref velocity, normal: normal, penetration: (minimum - distance), up: up, grounded: ref grounded, lastNormal: ref lastNormal);

        return true;
    }

    // CAPSULE EXTRACTION — the opposing-face straddle escape (see ResolveSphere/ResolveCapsule's remarks): a
    // capsule can land with both spheres inside one solid, straddling its Y-midplane — not only via a non-swept
    // teleport, but any live geometry mutation that rebuilds and swaps the field under a standing body, or a kit
    // collider swap. Per-sphere resolution IS the bug there — the lower center pushes toward the nearer floor face,
    // the upper toward the roof, against ONE shared position, net ~zero, a stable fixed point (a transient
    // intermediate up-push even latches `grounded` dishonestly). ONE authority replaces both: push the WHOLE capsule
    // ONE way — the tug-of-war dies because only one direction ever decides this iteration, and only one volume ever
    // claims that authority per body per iteration (see the Resolve loop).
    //
    // MAGNITUDE keys to EMBED DEPTH, not midpoint clearance: the max, over whichever center(s) sample embedded, of
    // that center's OWN already-sampled |distance| + (radius + skin) — never the midpoint's clearance to whatever
    // surface happens to be nearest it. Keying to midpoint clearance instead throws the body harder the CLEARER the
    // midpoint reads, which is backwards (a clear midpoint between two embedded centers means a shallow straddle,
    // not a deep one) and admits a period-2 limit cycle: overshoot past the surface on one push, and the next
    // iteration's midpoint clearance is now large on the opposite side, so it overshoots back. Keying to the
    // embedded center's own measured depth bounds the push at the actual embed and cannot construct that cycle. A
    // two-sided straddle takes the max of both centers' depths and needs no further field query at all; a one-sided
    // embed still needs a direction (below), which is the only case that samples the midpoint.
    //
    // The position push clamps approach velocity along the extraction direction exactly like an ordinary contact
    // push (ApplyPush) — we displaced along that direction, so canceling velocity into it is honest — but NEVER
    // grounds: extraction is honest displacement, never a resolved contact. Once no center samples inside geometry,
    // the SAME Resolve call's next iteration finds the ordinary per-sphere/core path clean and settles the body
    // honestly on the exterior surface it exited through.
    //
    // DIRECTION — bothEmbedded forces `up` rather than trusting the midpoint's OWN gradient (measured, not the
    // originally-drafted design): the capsule's sphere-center midpoint does not generally coincide with the
    // straddled solid's own symmetry plane — this repo's authored arcade cabinet spans core Y [0.0947, 1.7053]
    // (true symmetry at Y 0.9) against a capsule whose center-to-center midpoint sits at root+0.85, 0.05 below that
    // line — so the midpoint gradient there still reads "down" even though the upper sphere has already crossed
    // into the roof-favoring half. Trusting it re-creates the tug-of-war one level up: the body tunnels DOWNWARD
    // through the floor instead of extracting to the roof (confirmed by running the fix against the exact reported
    // repro before this override existed). A genuine two-sided straddle is exactly the case ApplyDegeneratePush's
    // "at rest" philosophy already covers for a symmetric point with no trustworthy measured direction — `up` is
    // authored, never zero, and matches gradient-derived-up worlds too since `up` here is the SAME per-iteration
    // value TryUp resolved, not a hardcoded world axis. A one-sided embed (only one center inside geometry) keeps
    // the originally-drafted midpoint-gradient direction, which has no opposing-authority conflict to mis-resolve —
    // gated behind a confirming TryDistance at the midpoint so a program with no geometry to answer never pays for a
    // gradient tap that is guaranteed to fail.
    //
    // ACCEPTED, LEDGERED RESIDUALS of the flat-up bias (not solved here — this is the line short of a general
    // depenetration solver, which belongs to the campaign lead, not this wave): a two-sided straddle under a
    // CEILING (the solid is above, not below) still forces `up`, extracting the body UPWARD THROUGH the overhead
    // structure rather than back down into the room it stands in — `up` is a floor-biased heuristic proven against
    // one authored configuration (a solid straddled from below), not a general "which side is thinner" decision. A
    // pitched capsule (a free-model 6DOF pose, not the upright standing case this was proven against) breaks the
    // framing entirely: `up` is the per-iteration TryUp value, not necessarily anywhere near either sphere's own
    // local axis once the capsule is not Y-aligned, so "two-sided straddle" stops meaning "split across a
    // Y-midplane" for such a body. Revisit if a shipped world authors a ceiling straddle or a pitched-capsule embed.
    private bool ExtractCapsule(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 lowerCenter, FixedVector3 upperCenter,
        FixedQ4816 lowerDistance, FixedQ4816 upperDistance, bool lowerEmbedded, bool upperEmbedded, FixedQ4816 radius, FixedVector3 up) {
        var bothEmbedded = (lowerEmbedded && upperEmbedded);
        var minimum = (radius + m_skin);
        var lowerDepth = (lowerEmbedded ? FixedQ4816.Abs(value: lowerDistance) : FixedQ4816.Zero);
        var upperDepth = (upperEmbedded ? FixedQ4816.Abs(value: upperDistance) : FixedQ4816.Zero);
        var depth = ((lowerDepth > upperDepth) ? lowerDepth : upperDepth);
        var magnitude = (depth + minimum);
        var midpoint = ((lowerCenter + upperCenter) / FixedQ4816.FromInteger(value: 2L));
        var coord = FixedPosition.FromLocal(local: midpoint);
        FixedVector3 direction;

        if (bothEmbedded) {
            direction = up;
        } else if (m_evaluator.TryDistance(position: coord, distance: out _, material: out _) &&
                   m_evaluator.TryFieldGradient(position: coord, epsilon: m_gradientProbe, gradient: out var gradient)) {
            direction = gradient;
        } else {
            direction = up;
        }

        position += (direction * magnitude);

        var into = FixedVector3.Dot(left: velocity, right: direction);

        if (into < FixedQ4816.Zero) {
            velocity -= (direction * into);
        }

        return true;
    }

    // A degenerate gradient after confirmed penetration means the sample point is mirror-symmetric in the field —
    // no measured surface normal exists. Eject by bare position push along reverse-of-motion (the de-tunneling
    // direction), or up for a body at rest; touch NOTHING else: the direction is not a measured normal, so clamping
    // velocity along it would fabricate physics, and it must never ground a body. DEFENSIVE — no shipped world's
    // geometry reaches it; verified by argument, not battery. That claim depends on every call site being gated to a
    // CONFIRMED, non-interior penetration (0 <= distance < minimum) before the gradient tap that can degenerate:
    // ResolveSphere and ResolveBox always were; ResolveCore's swept-core midpoint was NOT until its embed guard
    // (distance < 0, see ResolveCore's remarks) intercepted the interior case first — before that guard, an
    // interior swept-core sample could reach this same degenerate-gradient path with no argument covering it. With
    // the guard in place, all three call sites share the identical non-interior gate and the claim holds uniformly.
    private void ApplyDegeneratePush(ref FixedVector3 position, in FixedVector3 velocity, FixedQ4816 penetration, FixedVector3 up) {
        var direction = (-velocity).Normalize();

        if (direction == FixedVector3.Zero) {
            direction = up;
        }

        position += (direction * penetration);
    }

    // Extraction push for a single embedded center (distance < 0, already confirmed by the caller's TryDistance —
    // this method pays only the direction gradient tap). Direction = the center's own gradient; degenerate (no
    // measured normal) falls back to `up` directly — never zero, and never -velocity like ApplyDegeneratePush: an
    // embedded center's stored velocity carries no reliable de-tunneling direction, only `up` is authored as the
    // always-safe default. Clamps approach velocity along the extraction direction exactly like an ordinary contact
    // push (ApplyPush) — we displaced along that direction, so canceling velocity into it is honest — but NEVER
    // grounds: extraction is honest displacement, never a resolved contact. Callers own the iteration's one
    // extraction authority (see the Resolve loop); this method does not itself check or set it. Takes no lastNormal
    // parameter by design — an extraction rescuing a body from embedded geometry must never record as an
    // obstruction (a body ejected from inside a wall did not just get blocked by it).
    private void ApplyExtractionPush(ref FixedVector3 position, ref FixedVector3 velocity, FixedPosition coord, FixedQ4816 magnitude, FixedVector3 up) {
        var direction = m_evaluator.TryFieldGradient(position: coord, epsilon: m_gradientProbe, gradient: out var gradient) ? gradient : up;

        position += (direction * magnitude);

        var into = FixedVector3.Dot(left: velocity, right: direction);

        if (into < FixedQ4816.Zero) {
            velocity -= (direction * into);
        }
    }

    private void ApplyPush(ref FixedVector3 position, ref FixedVector3 velocity, FixedVector3 normal,
        FixedQ4816 penetration, FixedVector3 up, ref bool grounded, ref FixedVector3 lastNormal) {
        position += (normal * penetration);

        var walkable = (FixedVector3.Dot(left: normal, right: up) >= m_groundedThreshold);

        if (walkable) {
            grounded = true;
        } else {
            // world.contacts' obstruction witness tracks only a NON-walkable push (a wall, not the ground/a ramp) —
            // a standing body re-resolves its ground contact every solver iteration, so an unconditional "last push"
            // would have the ground overwrite a genuine wall push from an earlier iteration in the SAME tick and
            // hide it again.
            lastNormal = normal;
        }

        var into = FixedVector3.Dot(left: velocity, right: normal);

        if (into < FixedQ4816.Zero) {
            velocity -= (normal * into);
        }
    }

}
