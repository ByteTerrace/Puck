using System.Numerics;
using Puck.Maths;

namespace Puck.SdfVm.Debug;

/// <summary>
/// Emits the fullscreen SDF debug subject into an <see cref="SdfProgramBuilder"/>. The subject is WORLD-level (no
/// instance, no dynamic transform) — one pair needs neither. The <see cref="SdfDebugScene.Scope"/> toggle chooses
/// between the two emission orders below; their contrast IS the debugging tool for the scoped field accumulator.
/// <para>
/// COMMON to both: point-class ops (stack order) warp shape 1's evaluation point and emit FIRST — the op stack is
/// shape 1's (shape 2 re-anchors with its own ResetPoint). SHAPE 2 (optional) is translated by its offset and blended
/// against the accumulator, which at that moment is exactly shape 1's field, so even the intersection/xor families
/// compose exactly the authored pair ("author an intersection pair FIRST, against the empty accumulator" holds by
/// construction — the pair is the whole subject so far). The ground plane is always a plain Union.
/// </para>
/// <para>
/// SCOPED (default, <see cref="SdfDebugScene.Scope"/> ON): point ops → <c>PushField(Union)</c> → SHAPE 1 → field-class
/// ops (onion/dilate/displace) → SHAPE 2 → <c>PopField</c> → floor. The field ops sit INSIDE the scope, so they shell
/// the SUBJECT alone; the floor, emitted after the Pop, stays solid. This is the correct authoring form — a field op
/// bounded to its subtree.
/// </para>
/// <para>
/// FLAT (<see cref="SdfDebugScene.Scope"/> OFF): point ops → SHAPE 1 → SHAPE 2 → floor → field-class ops LAST. Every
/// field op runs on the ONE shared accumulator after the whole scene (subject AND floor) is assembled, so an onion
/// doubles the floor's zero-contour and a dilate fattens it — the "a field op shells the whole scene" pathology the
/// scoped accumulator exists to fix. (Onion/Dilate are point-independent; a flat-mode Displace reads the floor's
/// point context — an accepted debug quirk, since the point of flat mode is to EXHIBIT the flat behavior, and the
/// scoped path is the correct authoring form.)
/// </para>
/// CARVES (the runtime subtraction pool) always emit LAST, after the subject/floor and any flat-mode field ops — one
/// static <see cref="SdfBlendOp.Subtraction"/> (or <see cref="SdfBlendOp.SmoothSubtraction"/>) instance per carve, so
/// each bites the already-unioned subject+floor. <see cref="EmitProbe"/> emits the worst case (max stack + the TWO
/// wordiest shapes + floor + scoped Push/Pop + a FULL pool of <see cref="SdfDebugScene.MaxCarves"/> carves) so the
/// capacity probe covers any live state.
/// </summary>
public sealed class SdfDebugRenderer {
    // A pleasant off-white subject with modest specular, so the lit / normals views read form; the floor is a dimmer
    // neutral so it never competes with the subject. Full-bright debug room (the frame source pins Ambient/Sun to 1).
    private static readonly Vector3 SubjectAlbedo = new(x: 0.78f, y: 0.75f, z: 0.70f);
    // The second shape's albedo — a muted teal, clearly distinct from shape 1's off-white so a hard-blend pair reads
    // as two bodies in the lit view (a smooth/chamfer seam still shades by material winner per pixel).
    private static readonly Vector3 Shape2Albedo = new(x: 0.35f, y: 0.62f, z: 0.60f);

    private const float SubjectShininess = 40f;
    private const float SubjectSpecular = 0.35f;

    private static readonly Vector3 FloorAlbedo = new(x: 0.28f, y: 0.30f, z: 0.34f);
    // The carve cavity walls: a dark interior tone so a subtracted cavity reads as an exposed hollow against the
    // off-white subject (the carve sphere is the cutter — its material shades the newly-exposed inner surface).
    private static readonly Vector3 CarveAlbedo = new(x: 0.20f, y: 0.18f, z: 0.22f);
    // The ground plane sits a little below the subject (its surface at y = -FloorDrop), so a ~1-unit shape rests on it.
    // Internal: the meteor shower (SdfDebugScene.TickMeteor) lands floor craters relative to this surface height.
    internal const float FloorDrop = 1.3f;

    /// <summary>Emits the debug subject (+ optional floor) for a LIVE render.</summary>
    /// <param name="builder">The program builder (the program is only this subject while the mode is up).</param>
    /// <param name="scene">The debug scene state.</param>
    public void Emit(SdfProgramBuilder builder, SdfDebugScene scene) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(scene);

        var subjectMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: SubjectAlbedo, Specular: SubjectSpecular, Shininess: SubjectShininess));
        var scoped = scene.Scope;
        var chain = builder.ResetPoint();

        // POINT-class ops (folds/warps/transforms) apply BEFORE shape 1, in push order — the stack is SHAPE 1's. They
        // warp only the point; the field scope (opened next in scoped mode) reseeds only the FIELD, so they sit ahead
        // of the PushField either way.
        foreach (var op in scene.Ops) {
            if (!SdfDebugScene.IsFieldOp(kind: op.Kind)) {
                chain = ApplyPointOp(builder: chain, op: op);
            }
        }

        // SCOPED mode: open the subject scope so shape 1, its field ops, and shape 2 accumulate into a FRESH field.
        if (scoped) {
            chain = chain.PushField(compose: SdfBlendOp.Union);
        }

        chain = AppendShape(builder: chain, kind: scene.Shape, parameters: scene.Params, lift: scene.Lift, liftAmount: scene.LiftAmount, material: subjectMaterial);

        // FIELD-class ops (shell/inflate/relief). SCOPED: emit them HERE, inside the scope, so they shell the subject
        // (shape 1) alone. FLAT: DEFER them to the end of the program (below), so they shell the whole accumulated
        // field — subject AND floor — which is the flat-accumulator pathology the toggle exists to contrast.
        if (scoped) {
            chain = ApplyFieldOps(builder: chain, ops: scene.Ops);
        }

        // SHAPE 2 (optional): re-anchor the point (ResetPoint drops shape 1's point ops), translate to its offset,
        // and blend against the accumulator — which IS shape 1's field here (scoped: the fresh scope field; flat: the
        // shared field, still just shape 1), so the intersection/xor families compose exactly the authored pair.
        if (scene.Shape2 is { } second) {
            var secondMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: Shape2Albedo, Specular: SubjectSpecular, Shininess: SubjectShininess));

            chain = chain.ResetPoint().Translate(offset: scene.Offset2);
            chain = AppendShape(builder: chain, kind: second, parameters: scene.Params2, lift: scene.Lift, liftAmount: scene.LiftAmount, material: secondMaterial, blend: scene.Blend, smooth: scene.BlendSmooth);
        }

        // SCOPED mode: close the subject scope BEFORE the floor, composing it back with a plain Union (FAR-neutral, so
        // the scope stays cullable/segment-eligible) — the floor is then emitted OUTSIDE the scope and stays solid.
        if (scoped) {
            _ = chain.PopField();
        }

        // Floor (a plain Union — local/order-safe). SCOPED: outside the closed scope, so no field op reaches it. FLAT:
        // part of the shared accumulator, so the deferred field ops below shell it too.
        if (scene.Floor) {
            var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: FloorAlbedo));

            _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: FloorDrop, material: floorMaterial);
        }

        // FLAT mode: apply the field ops LAST, on the ONE shared field (subject + floor) — an onion doubles the floor's
        // zero-contour, a dilate fattens the whole scene. (builder === chain — the fluent builder returns this — so the
        // discarded return just runs the op on the shared accumulator; the point context is the floor's, an accepted
        // flat-mode Displace quirk noted in the class remarks.)
        if (!scoped) {
            _ = ApplyFieldOps(builder: builder, ops: scene.Ops);
        }

        // CARVES (the subtraction pool) emit LAST — after the subject, the floor, AND any flat-mode field ops — so each
        // carve has a higher segment index than everything it bites. By here the accumulator holds subject UNION floor
        // (a scoped subject was already popped with a Union compose, so its field is folded in), so ONE static instance
        // per carve subtracts from BOTH at once. Subtraction is FAR-NEUTRAL (max(acc, -sphere) = acc beyond the sphere),
        // so each carve packs a finite bound (its radius) and stays cullable — the smooth variant's k halo is added by
        // the packer (MaxSmoothBlendRadius); do NOT inflate it here.
        if (scene.Carves.Count > 0) {
            var carveMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: CarveAlbedo, Specular: SubjectSpecular, Shininess: SubjectShininess));

            // Route through the scene's carve-bake planner (carve-bake plan §4): it emits one SampledRegion per adopted
            // bin and analytic instances for the rest — and, with the switch off or nothing baked, a byte-identical
            // analytic emission (the same instructions the raw EmitCarves loop produces).
            scene.CarvePlanner.Emit(builder: builder, carves: scene.Carves, material: carveMaterial);
        }
    }

    // Emits each carve as a STATIC world-level instance: a bounding sphere at the carve, holding one Sphere baked to the
    // carve position (ResetPoint + Translate) that SUBTRACTS from the running accumulator (hard Subtraction, or
    // SmoothSubtraction with k when Smooth). Carves don't move, so the instance is STATIC (no dynamic slot). Bound =
    // carve radius EXACTLY — the packer adds float-safety padding and the smooth halo, so passing the radius alone is
    // correct (double-inflating would over-cull the cavity's seam tiles). Shared by the live subject (Emit) and the
    // carve bench (EmitBenchCarves) and folded worst-case by EmitProbe.
    internal static void EmitCarves(SdfProgramBuilder builder, IReadOnlyList<SdfCarve> carves, int material) {
        foreach (var carve in carves) {
            var blend = (carve.Smooth ? SdfBlendOp.SmoothSubtraction : SdfBlendOp.Subtraction);

            builder.BeginInstance(boundCenter: carve.Center, boundRadius: carve.Radius);
            _ = builder.ResetPoint().Translate(offset: carve.Center).Sphere(radius: carve.Radius, material: material, blend: blend, smooth: (carve.Smooth ? carve.SmoothK : 0f));
            builder.EndInstance();
        }
    }

    // Applies every FIELD-class op in emission order (shared by the scoped and flat paths in Emit — the two orders
    // differ only in WHERE this runs, never how). builder === the fluent chain, so the returned builder is just the
    // running chain after each op.
    private static SdfProgramBuilder ApplyFieldOps(SdfProgramBuilder builder, IEnumerable<SdfDebugOp> ops) {
        var chain = builder;

        foreach (var op in ops) {
            if (SdfDebugScene.IsFieldOp(kind: op.Kind)) {
                chain = ApplyFieldOp(builder: chain, op: op);
            }
        }

        return chain;
    }

    /// <summary>Emits the WORST-CASE debug program (a full <see cref="SdfDebugScene.MaxOps"/> stack of single-word ops
    /// plus the wordiest shapes, the floor, AND the scoped Push/Pop pair — the wordier of the two emission orders) so
    /// the frame source's capacity probe reserves an envelope every live push fits inside. Never rendered.
    /// <para>This probe ALSO bounds every GALLERY exhibit (<see cref="EmitGallery"/>): the largest INSTANCE exhibit is
    /// the carve-ceiling (a subject sphere + floor + 256 carve instances), and 256 &lt; the full <see cref="SdfDebugScene.MaxCarves"/>
    /// (4096) carve pool this probe already reserves; the largest WORD exhibit is the drift monolith
    /// (<see cref="SdfDriftMonolith"/>, ~50 world-level instructions across ten materials, zero instances), still far
    /// under the 12-op stack + two lifted Stars + scope + 4096-carve pool below — so no exhibit can outgrow the frozen
    /// envelope and none needs its own probe.</para></summary>
    /// <param name="builder">The probe builder (already carrying the room's own worst case).</param>
    public void EmitProbe(SdfProgramBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        var subjectMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: SubjectAlbedo, Specular: SubjectSpecular, Shininess: SubjectShininess));
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: FloorAlbedo));

        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: FloorDrop, material: floorMaterial);

        var chain = builder.ResetPoint();

        // Every op is a single instruction, so MaxOps of any op is the worst instruction count regardless of the
        // point/field split — a full stack of Repeat covers it. The wordiest shape (a lifted Star bakes the most
        // constants) closes the subject; the SECOND shape's worst case (ResetPoint + Translate + another lifted Star
        // with a smooth blend — the blend rides the shape instruction, zero extra words) follows, so a live pair with
        // a full stack always fits the frozen envelope. The second material joins the probe's table too.
        for (var index = 0; (index < SdfDebugScene.MaxOps); index++) {
            chain = chain.Repeat(spacing: new Vector3(x: 4f, y: 4f, z: 4f));
        }

        // Wrap the pair in the scoped Push/Pop (two extra words): scoped mode is strictly wordier than flat (flat adds
        // no scope instructions), so reserving for it covers both. The scope holds two shapes, satisfying PopField's
        // at-least-one-shape rule.
        chain = chain.PushField(compose: SdfBlendOp.Union);
        chain = chain.Star(points: 5, radius: 0.9f, sharpness: 2.6f, lift: SdfLift.Revolve, liftAmount: 0.5f, material: subjectMaterial);

        var secondMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: Shape2Albedo, Specular: SubjectSpecular, Shininess: SubjectShininess));

        chain = chain.ResetPoint().Translate(offset: new Vector3(x: 1.2f, y: 0f, z: 0f)).Star(points: 5, radius: 0.9f, sharpness: 2.6f, lift: SdfLift.Revolve, liftAmount: 0.5f, material: secondMaterial, blend: SdfBlendOp.SmoothUnion, smooth: 0.25f);

        _ = chain.PopField();

        // CARVES: the live subject can carry up to MaxCarves carves, each a static subtraction instance emitted after
        // the subject/floor (see Emit + EmitCarves). Fold the worst FORM — SMOOTH subtraction — into the probe; a hard
        // carve is word/instruction-identical (the smooth halo is a packer BOUND inflation, not extra words), so smooth
        // vs. hard costs the same envelope. Full pool of MaxCarves, all at the origin (position is irrelevant to size).
        //
        // ENVELOPE MATH. Each carve = 1 instance (BeginInstance/EndInstance) + 3 instructions (ResetPoint, Translate,
        // Sphere) + one instance-directory entry (2 vectors = 8 words). The debug subject + floor are WORLD-level (0
        // instances), so the LIVE debug program tops out at MaxCarves = 4096 instances — well inside MaxInstances =
        // 16384. This probe over-covers by folding the 4096 carves ON TOP OF the room's own instances (a few dozen), so
        // the subject probe is (room + 4096) << 16384. The BENCH probe (EmitBenchProbe: 16384 lifted-Star instances) is
        // a SEPARATE probe MAX-folded against this one (OverworldFrameSource.MeasureWorstCaseEnvelope) and DOMINATES
        // both dimensions — 16384 > room + 4096 instances, and 16384 wordy Stars > 4096 carve spheres + the op stack —
        // so the frozen envelope stays bench-bound and carves do not grow it. Folding them here keeps the subject probe
        // honest regardless of which probe wins the MAX.
        var carveMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: CarveAlbedo, Specular: SubjectSpecular, Shininess: SubjectShininess));
        var worstCarves = new List<SdfCarve>(capacity: SdfDebugScene.MaxCarves);

        for (var index = 0; (index < SdfDebugScene.MaxCarves); index++) {
            worstCarves.Add(item: new SdfCarve(Center: Vector3.Zero, Radius: SdfDebugScene.DefaultCarveRadius, Smooth: true, SmoothK: SdfDebugScene.DefaultCarveSmoothK));
        }

        EmitCarves(builder: builder, carves: worstCarves, material: carveMaterial);

        // CARVE-BAKE: the worst MIXED case adds MaxBricks full-resolution SampledRegion instances ON TOP of the full
        // analytic pool (carve-bake plan §4) — over-covering (a baked bin's carves are NOT also emitted analytic), but
        // it keeps the frozen envelope safe for any settle state, brick or analytic.
        SdfCarveBakePlanner.EmitWorstCaseBricks(builder: builder, material: carveMaterial);
    }

    private static SdfProgramBuilder ApplyPointOp(SdfProgramBuilder builder, SdfDebugOp op) {
        return op.Kind switch {
            SdfDebugOpKind.Twist => builder.TwistY(rate: op.A),
            SdfDebugOpKind.BendX => builder.BendX(rate: op.A),
            SdfDebugOpKind.BendY => builder.BendY(rate: op.A),
            SdfDebugOpKind.BendZ => builder.BendZ(rate: op.A),
            SdfDebugOpKind.Scale => builder.Scale(scale: op.V0),
            SdfDebugOpKind.Elongate => builder.Elongate(extents: op.V0),
            SdfDebugOpKind.Repeat => builder.Repeat(spacing: op.V0),
            SdfDebugOpKind.RepeatLimited => builder.RepeatLimited(spacing: op.V0, limit: op.V1),
            SdfDebugOpKind.Polar => builder.RepeatPolar(count: op.I0, axis: (SdfPolarAxis)op.I1, mirror: op.Flag),
            SdfDebugOpKind.Symmetry => builder.SymmetryPlane(normal: op.V0, offset: op.A),
            SdfDebugOpKind.LogSphere => builder.LogSphere(shellRatio: op.A, twist: op.B),
            SdfDebugOpKind.CellJitter => builder.CellJitter(spacing: op.V0, jitter: op.A, seed: (uint)op.I0, tumble: op.B),
            SdfDebugOpKind.DomainWarp => builder.DomainWarp(frequency: op.V0, amplitude: op.A),
            _ => builder,
        };
    }
    private static SdfProgramBuilder ApplyFieldOp(SdfProgramBuilder builder, SdfDebugOp op) {
        return op.Kind switch {
            SdfDebugOpKind.Onion => builder.Onion(thickness: op.A),
            SdfDebugOpKind.Dilate => builder.Dilate(radius: op.A),
            SdfDebugOpKind.Displace => builder.Displace(frequency: op.V0, amplitude: op.A),
            _ => builder,
        };
    }

    // Appends ONE primitive from the shared catalog: `kind`/`parameters` select it (shape 1 or shape 2 — same
    // catalog), `lift`/`liftAmount` supply the shared 2D-family lift, and `blend`/`smooth` ride the primitive
    // instruction (shape 1 passes the default Union-against-empty; shape 2 passes the authored pair blend). Shared by
    // the debug subject (Emit) and the bench workloads (EmitBench).
    internal static SdfProgramBuilder AppendShape(SdfProgramBuilder builder, SdfDebugShapeKind kind, IReadOnlyList<float> parameters, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        var p = parameters;

        float At(int index, float fallback) => ((index < p.Count) ? p[index] : fallback);

        return kind switch {
            SdfDebugShapeKind.Sphere => builder.Sphere(radius: At(fallback: 1f, index: 0), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Box => builder.Box(halfExtents: new Vector3(x: At(fallback: 0.8f, index: 0), y: At(fallback: 0.8f, index: 1), z: At(fallback: 0.8f, index: 2)), round: At(fallback: 0.05f, index: 3), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Torus => builder.Torus(majorRadius: At(fallback: 1f, index: 0), minorRadius: At(fallback: 0.35f, index: 1), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Capsule => builder.Capsule(endpoint: new Vector3(x: 0f, y: (2f * At(fallback: 1f, index: 0)), z: 0f), radius: At(fallback: 0.35f, index: 1), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Cylinder => builder.Cylinder(radius: At(fallback: 0.7f, index: 0), halfHeight: At(fallback: 1f, index: 1), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Ellipsoid => builder.Ellipsoid(radii: new Vector3(x: At(fallback: 1f, index: 0), y: At(fallback: 0.7f, index: 1), z: At(fallback: 0.5f, index: 2)), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Vesica => builder.Vesica(radius: At(fallback: 1f, index: 0), halfSeparation: At(fallback: 0.5f, index: 1), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.RoundCone => builder.RoundCone(lowerRadius: At(fallback: 0.7f, index: 0), upperRadius: At(fallback: 0.3f, index: 1), height: At(fallback: 1.2f, index: 2), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.RoundedRect => builder.RoundedRectangle(halfWidth: At(fallback: 0.8f, index: 0), halfHeight: At(fallback: 0.5f, index: 1), cornerRadius: At(fallback: 0.15f, index: 2), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Polygon => builder.RegularPolygon(sides: (int)At(fallback: 6f, index: 0), radius: At(fallback: 0.9f, index: 1), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Star => builder.Star(points: (int)At(fallback: 5f, index: 0), radius: At(fallback: 0.9f, index: 1), sharpness: At(fallback: 2.6f, index: 2), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Trapezoid => builder.Trapezoid(bottomHalfWidth: At(fallback: 0.8f, index: 0), topHalfWidth: At(fallback: 0.4f, index: 1), halfHeight: At(fallback: 0.7f, index: 2), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Ellipse => builder.Ellipse(semiX: At(fallback: 0.9f, index: 0), semiY: At(fallback: 0.6f, index: 1), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            _ => builder.Sphere(radius: 1f, material: material, blend: blend, smooth: smooth),
        };
    }

    // ── SDF gallery (the torture museum) exhibit emitters ───────────────────────────────────────────────────────────
    // Each emits ONE hand-authored known-nasty scene (a takeover, like the debug subject / bench workload), reusing the
    // shared shape/carve emitters where it can. Deterministic and parameterized — no wall clock, no RNG — so every
    // exhibit's breakdown reproduces run to run. The camera pose + plaque live in SdfGalleryScene.

    /// <summary>Emits one gallery exhibit's scene into <paramref name="builder"/>. Dispatched by <see cref="SdfGalleryExhibit"/>.
    /// Every exhibit is small (well inside the debug subject's worst-case envelope — see <see cref="EmitProbe"/>).</summary>
    public void EmitGallery(SdfProgramBuilder builder, SdfGalleryExhibit exhibit) {
        ArgumentNullException.ThrowIfNull(builder);

        // The drift monolith reaches two of its hex-stride materials POSITIONALLY through the wallpaper fold's
        // materialStride, so it owns its whole material palette and must be the FIRST thing emitted — before the
        // shared subject material below, which every other exhibit shares.
        if (exhibit == SdfGalleryExhibit.DriftMonolith) {
            SdfDriftMonolith.Emit(builder: builder);

            return;
        }

        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: SubjectAlbedo, Specular: SubjectSpecular, Shininess: SubjectShininess));

        switch (exhibit) {
            case SdfGalleryExhibit.LiarSpiral:
                // A thin blade twisted HARD (rate 3): the field over-estimates distance where the twist shears space, so
                // it breaks 1-Lipschitz — the Lipschitz clamp's whole reason. Pairs with debug.view.overshoot.
                _ = builder.ResetPoint().TwistY(rate: 3f).Box(halfExtents: new Vector3(x: 0.18f, y: 1.4f, z: 0.9f), round: 0.02f, material: material);

                break;
            case SdfGalleryExhibit.DrosteTunnel:
                // LogSphere shellRatio 2 — a discontinuous log-polar fold that stresses cross-backend parity.
                _ = builder.ResetPoint().LogSphere(shellRatio: 2f).Sphere(radius: 1f, material: material);

                break;
            case SdfGalleryExhibit.CellJitterCreases:
                // A CONTAINED jittered prototype (jitter/2 + radius = 0.4 + 0.5 <= spacing/2 = 1.0) that STILL seams: the
                // round fold picks each point's own cell, not the nearest copy.
                _ = builder.ResetPoint().CellJitter(spacing: new Vector3(x: 2f, y: 2f, z: 2f), jitter: 0.8f, seed: 1u, tumble: 0f).Sphere(radius: 0.5f, material: material);

                break;
            case SdfGalleryExhibit.NotchHorizon:
                // A ground plane stretching to the horizon plus two grounded reference boxes — the far-ground silhouette
                // against the sky is the notch (grazed by the exhibit's low-pitch pose).
                EmitGalleryFloor(builder: builder);
                _ = builder.ResetPoint().Translate(offset: new Vector3(x: -1.2f, y: (0.4f - FloorDrop), z: -2.5f)).Box(halfExtents: new Vector3(x: 0.4f, y: 0.4f, z: 0.4f), round: 0.03f, material: material);
                _ = builder.ResetPoint().Translate(offset: new Vector3(x: 1.4f, y: (0.6f - FloorDrop), z: -4f)).Box(halfExtents: new Vector3(x: 0.5f, y: 0.6f, z: 0.5f), round: 0.03f, material: material);

                break;
            case SdfGalleryExhibit.SmoothChain:
                EmitSmoothChain(builder: builder, material: material);

                break;
            case SdfGalleryExhibit.WallpaperP4G:
                // P4G renders as p4 (KNOWN DEFECT) — an ASYMMETRIC tile reveals the dropped mirror classes. Tiles XZ.
                _ = builder.ResetPoint().WallpaperFold(group: SdfWallpaperGroup.P4G, cell: new Vector2(x: 2f, y: 2f), limit: new Vector2(x: 3f, y: 3f), plane: SdfWallpaperPlane.XZ).Box(halfExtents: new Vector3(x: 0.55f, y: 0.3f, z: 0.22f), round: 0.03f, material: material);

                break;
            case SdfGalleryExhibit.CarveCeiling:
                // ~256 clustered hard carves on a subject sphere + floor — the honest destruction budget made visible
                // (reuses the carve bench emitter). Watch with debug.view.mask.
                EmitBenchCarves(builder: builder, family: SdfBenchCarveFamily.Clustered, count: 256, material: material);

                break;
            case SdfGalleryExhibit.LogSphereRunDoc:
                // Aggressive LogSphere (shellRatio 2.8 + twist) over a floor — validator-legal, marcher-breaking when the
                // camera sits DOWN INSIDE the fold (the pose the scene supplies). Pairs with overshoot + termination.
                _ = builder.ResetPoint().LogSphere(shellRatio: 2.8f, twist: 0.6f).Sphere(radius: 1.2f, material: material);
                EmitGalleryFloor(builder: builder);

                break;
            default:
                _ = builder.ResetPoint().Sphere(radius: 1f, material: material);

                break;
        }
    }

    // The gallery's ground plane (its own dimmer neutral material), at the same drop the debug subject's floor uses.
    private void EmitGalleryFloor(SdfProgramBuilder builder) {
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: FloorAlbedo));

        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: FloorDrop, material: floorMaterial);
    }

    // Eight spheres in a row, each folded into the accumulator with an ALTERNATING SmoothUnion/ChamferUnion blend — a
    // long chain whose per-blend LSB rounding accumulates down its length (the scoped accumulator bounds its reach).
    private static void EmitSmoothChain(SdfProgramBuilder builder, int material) {
        const int links = 8;

        var chain = builder.ResetPoint().Translate(offset: new Vector3(x: -2.1f, y: 0f, z: 0f)).Sphere(radius: 0.45f, material: material);

        for (var index = 1; (index < links); index++) {
            var blend = (((index & 1) == 0) ? SdfBlendOp.SmoothUnion : SdfBlendOp.ChamferUnion);
            var x = (-2.1f + (index * 0.6f));
            var y = (0.15f * MathF.Sin(x: (index * 1.1f)));

            chain = chain.ResetPoint().Translate(offset: new Vector3(x: x, y: y, z: 0f)).Sphere(radius: 0.45f, material: material, blend: blend, smooth: 0.3f);
        }
    }

    // ── SDF perf-bench workload emitters ────────────────────────────────────────────────────────────────────────────
    // These emit one BENCH configuration's program (a takeover, like the debug subject) so the bench runner can measure
    // its per-pass GPU cost. World-level for shapes/ops (one always-evaluated subject); a real instance grid for the
    // instance workloads (BeginInstance/EndInstance with covering bounds, so the beam's tile cull is exercised).

    private static readonly Vector3 BenchAlbedo = new(x: 0.78f, y: 0.75f, z: 0.70f);

    private const float BenchShininess = 40f;
    private const float BenchSpecular = 0.35f;
    // The fixed subject for the ops workload — a plain torus (its default params), so each op's marginal cost reads
    // against the Baseline row (a bare torus behind an identity Translate).
    private static readonly float[] BenchTorusParams = [1f, 0.35f];
    // The carve bench's fixed subject: a ~2-unit sphere at the origin (radius 1.6 → ~3.2 across) plus a floor below it,
    // that the carve pool subtracts from. A FIXED subject means the bench camera never reframes across the ladder (the
    // subject only shrinks as carves bite it, so it stays in frame at SingleShapeDistance — see SdfBenchScene).
    private const float BenchCarveSubjectRadius = 1.6f;
    private const float BenchCarveRadius = 0.35f;   // matches the live default — the honest per-carve footprint
    private const float BenchCarveSmoothK = 0.15f;
    private const float BenchCarveFloorDrop = 2.2f; // the floor sits below the subject so grounded scatter carves can bite it
    private const float BenchScatterExtent = 12f;   // the scatter cube's full side (empty-space + floor spread, subject-dwarfing)

    /// <summary>Emits ONE bench configuration's workload into <paramref name="builder"/> (a takeover — the room is
    /// replaced). Dispatched by <see cref="SdfBenchWorkload"/>. A <paramref name="carvePlanner"/> (the bench's settle-0
    /// planner) routes the <see cref="SdfBenchWorkload.Carves"/> workload through the carve-bake pipeline — adopted bins
    /// emit as bricks, the rest analytic (carve-bake plan §4); null keeps carves fully analytic.</summary>
    public void EmitBench(SdfProgramBuilder builder, SdfBenchConfig config, SdfCarveBakePlanner? carvePlanner = null) {
        ArgumentNullException.ThrowIfNull(builder);

        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: BenchAlbedo, Specular: BenchSpecular, Shininess: BenchShininess));

        switch (config.Workload) {
            case SdfBenchWorkload.Shapes:
                _ = AppendShape(builder: builder.ResetPoint(), kind: config.Shape, parameters: SdfDebugScene.DefaultParams(kind: config.Shape), lift: SdfLift.Revolve, liftAmount: 0.5f, material: material);

                break;
            case SdfBenchWorkload.Ops:
                EmitBenchOp(builder: builder, op: config.Op, material: material);

                break;
            case SdfBenchWorkload.Instances:
                EmitInstances(builder: builder, shape: config.Shape, count: config.InstanceCount, material: material);

                break;
            case SdfBenchWorkload.Rigs:
                EmitRigs(builder: builder, count: config.InstanceCount, material: material);

                break;
            case SdfBenchWorkload.Carves:
                EmitBenchCarves(builder: builder, family: config.CarveFamily, count: config.InstanceCount, material: material, carvePlanner: carvePlanner);

                break;
            case SdfBenchWorkload.Storm:
                if (config.StormMode == SdfBenchStormMode.Motion) {
                    // The MOTION rung: N DYNAMIC instances riding the per-frame transform buffer (the always-list cliff).
                    EmitStorm(builder: builder, count: config.InstanceCount, material: material);
                } else {
                    // The REBUILD + CAMERA rungs: N STATIC instances (grid-cullable) — rebuild bumps the revision every
                    // frame (upload/pack cost), camera sweeps the pose (re-cull cost); neither moves the geometry.
                    EmitInstances(builder: builder, shape: config.Shape, count: config.InstanceCount, material: material);
                }

                break;
            default:
                _ = builder.ResetPoint().Sphere(radius: 1f, material: material);

                break;
        }
    }

    /// <summary>Advances a carve-bake planner one produced frame for a <see cref="SdfBenchWorkload.Carves"/> config — the
    /// HEADLESS synthetic bench's brick-advance hook (the interactive <c>sdf.bench</c> ladder uses the equivalent
    /// <see cref="SdfBenchScene.AdvanceCarveBake"/>). It feeds the planner the IDENTICAL deterministic carve list
    /// <see cref="EmitBench"/> emits (<see cref="BuildBenchCarves"/> is a pure function of family/count), so the planner's
    /// binning matches the emission exactly; any non-carves config feeds an empty pool so a stale brick is released.
    /// Returns whether the adopted set changed (the caller rebuilds + re-arms its takeover program).</summary>
    /// <param name="config">The active bench configuration.</param>
    /// <param name="planner">The scene's settle-0 carve-bake planner.</param>
    /// <param name="carveRevision">A monotonic content revision (constant for a fixed synthetic workload).</param>
    /// <param name="bakes">The engine's brick-bake service.</param>
    /// <returns>Whether the emit plan changed.</returns>
    public bool AdvanceBenchCarveBake(SdfBenchConfig config, SdfCarveBakePlanner planner, int carveRevision, ISdfBrickBakeService bakes) {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(bakes);

        var carves = ((config.Workload == SdfBenchWorkload.Carves)
            ? BuildBenchCarves(family: config.CarveFamily, count: config.InstanceCount)
            : Array.Empty<SdfCarve>());

        return planner.Advance(carves: carves, carveRevision: carveRevision, bakes: bakes);
    }

    /// <summary>Emits <paramref name="count"/> DYNAMIC instances of a compact sphere, each on its OWN dynamic-transform
    /// slot (instance i rides slot i), so all move per produced frame purely through the frame's dynamic-transform buffer
    /// — no program rebuild. The ring-local frame grid resolves and bins their moving centers; this workload exposes the
    /// per-frame grid-build/upload and moving-instance mask cost. Its per-frame
    /// transforms come from <see cref="SdfBenchScene.TryPackStormTransforms"/> (deterministic: instance index +
    /// produced-frame counter). The count is clamped to the storm ceiling, which the render assembly reserves dynamic-
    /// transform capacity for (<see cref="SdfBenchScene.MaxStormInstances"/>).</summary>
    public void EmitStorm(SdfProgramBuilder builder, int count, int material) {
        ArgumentNullException.ThrowIfNull(builder);

        var n = Math.Clamp(value: count, min: 0, max: Math.Min(val1: SdfBenchScene.MaxStormInstances, val2: SdfProgramBuilder.MaxInstances));

        for (var index = 0; (index < n); index++) {
            // boundOffset zero: the whole orbit+bob displacement is baked into the slot's per-frame position, so the
            // instance's bound (center = slot position + offset) tracks the mover and need only cover the sphere.
            builder.BeginInstanceDynamic(slot: index, boundOffset: Vector3.Zero, boundRadius: SdfBenchScene.StormBoundRadius);
            _ = builder.ResetPoint().TransformDynamic(slot: index).Sphere(radius: SdfBenchScene.StormInstanceRadius, material: material);
            builder.EndInstance();
        }
    }

    /// <summary>Emits heterogeneous walking-style articulated rigs: 12..36 independent dynamic bone slots per avatar
    /// (60..180 authored instructions) and five rigid instructions per leaf. Puck.Maths low-discrepancy samples vary
    /// counts, shape order, dimensions, and poses without RNG state; every avatar owns distinct instruction records.</summary>
    public void EmitRigs(SdfProgramBuilder builder, int count, int material) {
        ArgumentNullException.ThrowIfNull(builder);

        var avatars = Math.Clamp(value: count, min: 1, max: SdfBenchScene.MaxRigAvatars);
        var slotBase = 0;

        for (var avatar = 0; (avatar < avatars); avatar++) {
            var boneCount = SdfBenchScene.RigBoneCountForAvatar(avatar: avatar);

            for (var bone = 0; (bone < boneCount); bone++, slotBase++) {
                var variation = LowDiscrepancy.R2(index: (ulong)(((avatar * SdfBenchScene.MaxRigBoneCount) + bone) + 1));
                var poseX = (float)(double)variation.X;
                var poseY = (float)(double)variation.Y;
                var side = (((bone & 1) == 0) ? -1f : 1f);
                var band = (bone / 4);
                var authoredRotation = Quaternion.CreateFromYawPitchRoll(
                    yaw: ((poseX - 0.5f) * 0.22f),
                    pitch: (side * (0.06f + (0.03f * poseY) + (0.008f * band))),
                    roll: ((poseY - 0.5f) * 0.10f)
                );
                var leafOffset = new Vector3(x: 0f, y: ((poseX - 0.5f) * 0.03f), z: 0f);
                var scale = (0.85f + (0.30f * poseY));
                var shape = (int)(((ulong)variation.X.Value * 4u) >> 32);

                // One cull bit per lowered rigid leaf. This is execution metadata, not an authored ISA expansion: the
                // avatar still owns only its authored five-op chains, but a tile touching its hand no longer admits
                // every other bone (or every bone of every neighboring avatar).
                builder.BeginInstanceDynamic(slot: slotBase, boundOffset: Vector3.Zero, boundRadius: SdfBenchScene.RigBoneBoundRadius);
                var chain = builder
                    .ResetPoint()
                    .TransformDynamic(slot: slotBase)
                    .Translate(offset: leafOffset)
                    .Rotate(rotation: authoredRotation);

                _ = shape switch {
                    0 => chain.Box(halfExtents: new Vector3(x: (0.11f * scale), y: (0.18f * scale), z: (0.09f * scale)), round: (0.04f * scale), material: material),
                    1 => chain.Capsule(endpoint: new Vector3(x: 0f, y: (0.28f * scale), z: 0f), radius: (0.07f * scale), material: material),
                    2 => chain.Cylinder(radius: (0.085f * scale), halfHeight: (0.16f * scale), material: material),
                    _ => chain.Sphere(radius: (0.11f * scale), material: material),
                };

                builder.EndInstance();
            }
        }
    }

    // A fixed torus + EXACTLY ONE op (point ops fold the point before the shape; field ops shell the field after it).
    // The Baseline is the bare torus behind an identity Translate, so every op row measures one extra instruction.
    private static void EmitBenchOp(SdfProgramBuilder builder, SdfBenchOp op, int material) {
        var chain = builder.ResetPoint();

        // POINT-class ops warp the evaluation point BEFORE the shape.
        chain = op switch {
            SdfBenchOp.Baseline => chain.Translate(offset: Vector3.Zero),
            SdfBenchOp.Twist => chain.TwistY(rate: 1.0f),
            SdfBenchOp.BendX => chain.BendX(rate: 0.5f),
            SdfBenchOp.Elongate => chain.Elongate(extents: new Vector3(x: 0.3f, y: 0f, z: 0f)),
            SdfBenchOp.Repeat => chain.Repeat(spacing: new Vector3(x: 3f, y: 3f, z: 3f)),
            SdfBenchOp.RepeatLimited => chain.RepeatLimited(spacing: new Vector3(x: 3f, y: 3f, z: 3f), limit: new Vector3(x: 1f, y: 1f, z: 1f)),
            SdfBenchOp.Polar => chain.RepeatPolar(count: 6, axis: SdfPolarAxis.Y),
            SdfBenchOp.Symmetry => chain.SymmetryPlane(normal: Vector3.UnitX),
            SdfBenchOp.Wallpaper => chain.WallpaperFold(group: SdfWallpaperGroup.P4M, cell: new Vector2(x: 2.5f, y: 2.5f), limit: new Vector2(x: 2f, y: 2f), plane: SdfWallpaperPlane.XZ),
            SdfBenchOp.LogSphere => chain.LogSphere(shellRatio: 2f),
            SdfBenchOp.CellJitter => chain.CellJitter(spacing: new Vector3(x: 3f, y: 3f, z: 3f), jitter: 0.5f),
            SdfBenchOp.DomainWarp => chain.DomainWarp(frequency: new Vector3(x: 2f, y: 2f, z: 2f), amplitude: 0.15f),
            SdfBenchOp.Scale => chain.Scale(scale: new Vector3(x: 0.8f, y: 0.8f, z: 0.8f)),
            _ => chain, // field ops apply after the shape (below) — no point fold
        };

        chain = AppendShape(builder: chain, kind: SdfDebugShapeKind.Torus, parameters: BenchTorusParams, lift: SdfLift.Revolve, liftAmount: 0.5f, material: material);

        // FIELD-class ops shell/inflate/displace the accumulated field AFTER the shape.
        _ = op switch {
            SdfBenchOp.Displace => chain.Displace(frequency: new Vector3(x: 6f, y: 6f, z: 6f), amplitude: 0.08f),
            SdfBenchOp.Onion => chain.Onion(thickness: 0.05f),
            SdfBenchOp.Dilate => chain.Dilate(radius: 0.1f),
            _ => chain,
        };
    }

    /// <summary>Emits <paramref name="count"/> REAL instances of <paramref name="shape"/> in a centred 3D grid (each a
    /// BeginInstance/EndInstance pair with a covering bound, Active=true), sized so neighbours don't overlap and the
    /// whole grid fits the render range. The bench camera (see <see cref="SdfBenchScene.CameraFrame"/>) pulls back to
    /// frame it.</summary>
    public void EmitInstances(SdfProgramBuilder builder, SdfDebugShapeKind shape, int count, int material) {
        ArgumentNullException.ThrowIfNull(builder);

        var n = Math.Clamp(value: count, min: 0, max: SdfProgramBuilder.MaxInstances);
        var grid = SdfBenchScene.GridDimension(count: n);
        var half = (((grid - 1) * SdfBenchScene.InstanceSpacing) * 0.5f);
        var parameters = InstanceParams(kind: shape);

        for (var index = 0; (index < n); index++) {
            var ix = (index % grid);
            var iy = ((index / grid) % grid);
            var iz = (index / (grid * grid));
            var center = new Vector3(
                x: ((ix * SdfBenchScene.InstanceSpacing) - half),
                y: ((iy * SdfBenchScene.InstanceSpacing) - half),
                z: ((iz * SdfBenchScene.InstanceSpacing) - half)
            );

            builder.BeginInstance(boundCenter: center, boundRadius: SdfBenchScene.InstanceBoundRadius);
            _ = AppendShape(builder: builder.ResetPoint().Translate(offset: center), kind: shape, parameters: parameters, lift: SdfLift.Revolve, liftAmount: 0.12f, material: material);
            builder.EndInstance();
        }
    }

    /// <summary>Folds the bench WORST CASE into the frame source's capacity probe: <see cref="SdfProgramBuilder.MaxInstances"/>
    /// instances of the WORDIEST single shape (a lifted Star bakes the most constants) — so <c>sdf.bench instances 4096</c>
    /// always fits the frozen program/instance envelope. Never rendered.
    /// <para>STORM does NOT grow this probe. Its worst rung is 4096 DYNAMIC spheres
    /// (<see cref="SdfBenchScene.MaxStormInstances"/>); 4096 &lt; 16384 (MaxInstances) on the instance axis, and a
    /// dynamic sphere instance (BeginInstanceDynamic + ResetPoint + TransformDynamic + Sphere) is FEWER words than a
    /// lifted Star, so 16384 Stars dominates both the word and instance dimensions this probe already reserves. The one
    /// axis storm DOES grow is DYNAMIC-TRANSFORM capacity — 4096 moving slots vs the room's few dozen — but that floor
    /// is a SEPARATE render-assembly reservation (the frame source's WorstCaseDynamicTransformCapacity →
    /// SdfWorldRenderSpec.DynamicTransformCapacity), NOT this word/instance probe, so nothing here changes for it.</para></summary>
    public void EmitBenchProbe(SdfProgramBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: BenchAlbedo, Specular: BenchSpecular, Shininess: BenchShininess));

        EmitInstances(builder: builder, shape: SdfDebugShapeKind.Star, count: SdfProgramBuilder.MaxInstances, material: material);

        // The sdf.carves workload can adopt up to MaxBricks bricks on top of its analytic carves (carve-bake plan §4);
        // fold that worst mixed case into the bench probe so a baked carves rung fits the frozen envelope. Negligible
        // against the 16384 Star instances above, which already dominate both the word and instance dimensions.
        SdfCarveBakePlanner.EmitWorstCaseBricks(builder: builder, material: material);
    }

    /// <summary>Emits the CARVE bench workload: a fixed ~2-unit subject sphere + a floor (world-level), then
    /// <paramref name="count"/> carves in the given <paramref name="family"/> — <see cref="SdfBenchCarveFamily.Clustered"/>
    /// (packed on the subject surface, densely overlapping the same tiles: the honest views-cost worst case),
    /// <see cref="SdfBenchCarveFamily.Scattered"/> (spread through empty space + the floor, mostly masking out: the
    /// beam-wall control where beam grows O(n) while views stays flat), or <see cref="SdfBenchCarveFamily.Smooth"/>
    /// (clustered SmoothSubtraction — halo × mask-width pressure). Placement is DETERMINISTIC (golden-angle /
    /// low-discrepancy, no RNG), so a run reproduces bit-for-bit across sessions.</summary>
    public void EmitBenchCarves(SdfProgramBuilder builder, SdfBenchCarveFamily family, int count, int material, SdfCarveBakePlanner? carvePlanner = null) {
        ArgumentNullException.ThrowIfNull(builder);

        // The subject the carves bite — a large sphere at the origin unioned with a floor plane. Both are WORLD-level
        // (always evaluated); the carves are the instances the bench actually measures.
        _ = builder.ResetPoint().Sphere(radius: BenchCarveSubjectRadius, material: material);
        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: BenchCarveFloorDrop, material: material);

        var carves = BuildBenchCarves(family: family, count: count);

        // With a planner (the sdf.carves workload) route through the carve-bake pipeline: adopted clusters emit as
        // bricks, the rest analytic (carve-bake plan §4). Without one (the gallery's carve-ceiling exhibit) stay fully
        // analytic. The planner is fed the IDENTICAL list by SdfBenchScene.AdvanceCarveBake (BuildBenchCarves is a pure
        // function of family/count), so its binning at Advance matches this emission exactly.
        if (carvePlanner is not null) {
            carvePlanner.Emit(builder: builder, carves: carves, material: material);
        } else {
            EmitCarves(builder: builder, carves: carves, material: material);
        }
    }

    /// <summary>Builds the DETERMINISTIC carve list one <see cref="SdfBenchWorkload.Carves"/> rung bites its subject with
    /// (golden-angle clustered / R2-scattered placement, no RNG) — the SHARED source of truth both this emitter and the
    /// bench's carve-bake planner (<see cref="SdfBenchScene"/>) read, so the planner's binning matches the emission. The
    /// count is clamped to <see cref="SdfDebugScene.MaxCarves"/>.</summary>
    /// <param name="family">The placement family (clustered / scattered / smooth).</param>
    /// <param name="count">The requested carve count (clamped to the pool cap).</param>
    /// <returns>The carve list, in deterministic index order.</returns>
    internal static IReadOnlyList<SdfCarve> BuildBenchCarves(SdfBenchCarveFamily family, int count) {
        var n = Math.Clamp(value: count, min: 0, max: SdfDebugScene.MaxCarves);
        var smooth = (family == SdfBenchCarveFamily.Smooth);
        var carves = new List<SdfCarve>(capacity: n);

        for (var index = 0; (index < n); index++) {
            var center = ((family == SdfBenchCarveFamily.Scattered) ? ScatteredCarveCenter(index: index) : ClusteredCarveCenter(index: index, count: n));

            carves.Add(item: new SdfCarve(Center: center, Radius: BenchCarveRadius, Smooth: smooth, SmoothK: BenchCarveSmoothK));
        }

        return carves;
    }

    // A carve center ON the subject surface via the Fibonacci (golden-angle) sphere — a deterministic even spread. At
    // high counts the carves densely overlap (footprint sum >> the subject's surface area), so many share the same
    // screen tiles: the honest views-cost worst case (every overlapping carve is evaluated for each covered tile).
    private static Vector3 ClusteredCarveCenter(int index, int count) {
        const float goldenAngle = 2.399963f; // π · (3 − √5)
        var t = ((index + 0.5f) / MathF.Max(x: 1f, y: count));
        var y = (1f - (2f * t));
        var ring = MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (y * y))));
        var phi = (index * goldenAngle);
        var direction = new Vector3(x: (ring * MathF.Cos(x: phi)), y: y, z: (ring * MathF.Sin(x: phi)));

        return (direction * BenchCarveSubjectRadius);
    }

    // A carve center in a large cube (empty space + the floor) via the R2 low-discrepancy sequence (an additive
    // recurrence with plastic-number alphas) — deterministic, hash-free, evenly spread with no clumping. Most land far from
    // the ~2-unit subject and mask out (max(acc, −sphere) = acc where nothing is near), so views stays flat while the
    // beam's per-tile instance scan grows O(n): the beam-wall control.
    private static Vector3 ScatteredCarveCenter(int index) {
        // Fractional parts of 1/plastic^k (k = 1..3) — the canonical 3D R2 basis.
        const float a1 = 0.8191725f;
        const float a2 = 0.6710436f;
        const float a3 = 0.5497005f;
        var i = (index + 1);
        var x = Frac(value: (0.5f + (a1 * i)));
        var y = Frac(value: (0.5f + (a2 * i)));
        var z = Frac(value: (0.5f + (a3 * i)));

        return new Vector3(x: ((x - 0.5f) * BenchScatterExtent), y: ((y - 0.5f) * BenchScatterExtent), z: ((z - 0.5f) * BenchScatterExtent));
    }
    private static float Frac(float value) => (value - MathF.Floor(x: value));

    // Compact per-shape params so an instanced copy's bound stays under InstanceBoundRadius (no neighbour overlap at
    // InstanceSpacing). The lifted 2D family uses a small revolve offset (set at the call site).
    private static float[] InstanceParams(SdfDebugShapeKind kind) {
        return kind switch {
            SdfDebugShapeKind.Sphere => [0.4f],
            SdfDebugShapeKind.Box => [0.3f, 0.3f, 0.3f, 0.03f],
            SdfDebugShapeKind.Torus => [0.3f, 0.1f],
            SdfDebugShapeKind.Capsule => [0.22f, 0.12f],
            SdfDebugShapeKind.Cylinder => [0.28f, 0.3f],
            SdfDebugShapeKind.Ellipsoid => [0.4f, 0.3f, 0.25f],
            SdfDebugShapeKind.Vesica => [0.4f, 0.18f],
            SdfDebugShapeKind.RoundCone => [0.28f, 0.12f, 0.4f],
            SdfDebugShapeKind.RoundedRect => [0.35f, 0.25f, 0.08f],
            SdfDebugShapeKind.Polygon => [6f, 0.35f],
            SdfDebugShapeKind.Star => [5f, 0.38f, 2.6f],
            SdfDebugShapeKind.Trapezoid => [0.32f, 0.18f, 0.3f],
            SdfDebugShapeKind.Ellipse => [0.4f, 0.28f],
            _ => [0.4f],
        };
    }
}

/// <summary>Adapts <see cref="SdfDebugMode"/> — the composition facade that already owns the "which takeover is live"
/// dispatch (a bench workload, a gallery exhibit, or the plain debug subject — see <see cref="SdfDebugMode.Emit"/>) —
/// onto the <see cref="ISdfSceneEmitter"/> contract, so a composition host can register the whole SDF-debug surface as
/// ONE emitter without re-deriving that dispatch. The gallery/bench emitters
/// (<see cref="SdfDebugRenderer.EmitGallery"/>/<see cref="SdfDebugRenderer.EmitBench"/>) stay ordinary methods
/// <see cref="SdfDebugMode.Emit"/> calls internally — this type adds nothing beyond the two probe branches and the
/// dynamic-transform/revision plumbing every emitter needs.
/// <para>
/// A TAKEOVER, NOT A COMPOSABLE LAYER: the debug mode REPLACES the rest of a scene while active (see
/// <see cref="SdfDebugMode.Active"/>) — a composition host swaps this emitter into an ALTERNATE emitter list for the
/// takeover rather than mixing it into the room's own list (see <see cref="ISdfSceneEmitter"/>'s takeover remarks).
/// </para></summary>
/// <param name="mode">The debug mode facade this emitter wraps.</param>
public sealed class SdfDebugEmitter(SdfDebugMode mode) : ISdfSceneEmitter {
    private readonly SdfDebugMode m_mode = (mode ?? throw new ArgumentNullException(paramName: nameof(mode)));

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        if (context.Probe) {
            m_mode.EmitProbe(builder: builder);

            return;
        }

        m_mode.Emit(builder: builder);
    }

    /// <inheritdoc/>
    public int DynamicSlotCount => m_mode.WorstCaseDynamicTransformCapacity;

    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
        if (!m_mode.TryPackBenchDynamicTransforms(transforms: out var transforms)) {
            return; // Not a storm-motion bench rung: every reserved slot stays parked (the composition host already
                    // fills the whole shared buffer with SdfEmitContext.ParkPosition before any emitter packs).
        }

        var count = Math.Min(val1: transforms.Count, val2: (slots.Length - context.SlotBase));

        for (var index = 0; (index < count); index++) {
            slots[(context.SlotBase + index)] = transforms[index];
        }
    }

    /// <inheritdoc/>
    public int Revision => m_mode.Revision;
}
