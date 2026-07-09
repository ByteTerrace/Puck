using System.Numerics;
using Puck.SdfVm;

namespace Puck.Demo.SdfDebug;

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
    private static readonly Vector3 SubjectAlbedo = new(0.78f, 0.75f, 0.70f);
    // The second shape's albedo — a muted teal, clearly distinct from shape 1's off-white so a hard-blend pair reads
    // as two bodies in the lit view (a smooth/chamfer seam still shades by material winner per pixel).
    private static readonly Vector3 Shape2Albedo = new(0.35f, 0.62f, 0.60f);
    private const float SubjectSpecular = 0.35f;
    private const float SubjectShininess = 40f;
    private static readonly Vector3 FloorAlbedo = new(0.28f, 0.30f, 0.34f);
    // The carve cavity walls: a dark interior tone so a subtracted cavity reads as an exposed hollow against the
    // off-white subject (the carve sphere is the cutter — its material shades the newly-exposed inner surface).
    private static readonly Vector3 CarveAlbedo = new(0.20f, 0.18f, 0.22f);
    // The ground plane sits a little below the subject (its surface at y = -FloorDrop), so a ~1-unit shape rests on it.
    private const float FloorDrop = 1.3f;

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

            EmitCarves(builder: builder, carves: scene.Carves, material: carveMaterial);
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
    /// the frame source's capacity probe reserves an envelope every live push fits inside. Never rendered.</summary>
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
            chain = chain.Repeat(spacing: new Vector3(4f, 4f, 4f));
        }

        // Wrap the pair in the scoped Push/Pop (two extra words): scoped mode is strictly wordier than flat (flat adds
        // no scope instructions), so reserving for it covers both. The scope holds two shapes, satisfying PopField's
        // at-least-one-shape rule.
        chain = chain.PushField(compose: SdfBlendOp.Union);
        chain = chain.Star(points: 5, radius: 0.9f, sharpness: 2.6f, lift: SdfLift.Revolve, liftAmount: 0.5f, material: subjectMaterial);

        var secondMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: Shape2Albedo, Specular: SubjectSpecular, Shininess: SubjectShininess));

        chain = chain.ResetPoint().Translate(offset: new Vector3(1.2f, 0f, 0f)).Star(points: 5, radius: 0.9f, sharpness: 2.6f, lift: SdfLift.Revolve, liftAmount: 0.5f, material: secondMaterial, blend: SdfBlendOp.SmoothUnion, smooth: 0.25f);

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
            SdfDebugShapeKind.Sphere => builder.Sphere(radius: At(0, 1f), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Box => builder.Box(halfExtents: new Vector3(At(0, 0.8f), At(1, 0.8f), At(2, 0.8f)), round: At(3, 0.05f), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Torus => builder.Torus(majorRadius: At(0, 1f), minorRadius: At(1, 0.35f), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Capsule => builder.Capsule(endpoint: new Vector3(0f, (2f * At(0, 1f)), 0f), radius: At(1, 0.35f), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Cylinder => builder.Cylinder(radius: At(0, 0.7f), halfHeight: At(1, 1f), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Ellipsoid => builder.Ellipsoid(radii: new Vector3(At(0, 1f), At(1, 0.7f), At(2, 0.5f)), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Vesica => builder.Vesica(radius: At(0, 1f), halfSeparation: At(1, 0.5f), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.RoundCone => builder.RoundCone(lowerRadius: At(0, 0.7f), upperRadius: At(1, 0.3f), height: At(2, 1.2f), material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.RoundedRect => builder.RoundedRectangle(halfWidth: At(0, 0.8f), halfHeight: At(1, 0.5f), cornerRadius: At(2, 0.15f), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Polygon => builder.RegularPolygon(sides: (int)At(0, 6f), radius: At(1, 0.9f), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Star => builder.Star(points: (int)At(0, 5f), radius: At(1, 0.9f), sharpness: At(2, 2.6f), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Trapezoid => builder.Trapezoid(bottomHalfWidth: At(0, 0.8f), topHalfWidth: At(1, 0.4f), halfHeight: At(2, 0.7f), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            SdfDebugShapeKind.Ellipse => builder.Ellipse(semiX: At(0, 0.9f), semiY: At(1, 0.6f), lift: lift, liftAmount: liftAmount, material: material, blend: blend, smooth: smooth),
            _ => builder.Sphere(radius: 1f, material: material, blend: blend, smooth: smooth),
        };
    }

    // ── SDF perf-bench workload emitters ────────────────────────────────────────────────────────────────────────────
    // These emit one BENCH configuration's program (a takeover, like the debug subject) so the bench runner can measure
    // its per-pass GPU cost. World-level for shapes/ops (one always-evaluated subject); a real instance grid for the
    // instance workloads (BeginInstance/EndInstance with covering bounds, so the beam's tile cull is exercised).

    private static readonly Vector3 BenchAlbedo = new(0.78f, 0.75f, 0.70f);
    private const float BenchSpecular = 0.35f;
    private const float BenchShininess = 40f;
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
    /// replaced). Dispatched by <see cref="SdfBenchWorkload"/>.</summary>
    public void EmitBench(SdfProgramBuilder builder, SdfBenchConfig config) {
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
            case SdfBenchWorkload.Carves:
                EmitBenchCarves(builder: builder, family: config.CarveFamily, count: config.InstanceCount, material: material);

                break;
            case SdfBenchWorkload.Storm:
                if (config.StormMode == SdfBenchStormMode.Motion) {
                    // The MOTION rung: N DYNAMIC instances riding the per-frame transform buffer (the always-list cliff).
                    EmitStorm(builder: builder, count: config.InstanceCount, material: material);
                }
                else {
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

    /// <summary>Emits <paramref name="count"/> DYNAMIC instances of a compact sphere, each on its OWN dynamic-transform
    /// slot (instance i rides slot i), so all move per produced frame purely through the frame's dynamic-transform buffer
    /// — no program rebuild. The host bins only STATIC instances into the uniform grid, so these ride the beam's FLAT
    /// always-tested list by design: this is the workload that exposes the O(moving-n) beam/mask cliff. Its per-frame
    /// transforms come from <see cref="SdfBenchScene.TryPackStormTransforms"/> (deterministic: instance index +
    /// produced-frame counter). The count is clamped to the storm ceiling, which the render assembly reserves dynamic-
    /// transform capacity for (<see cref="SdfBenchScene.MaxStormInstances"/>).</summary>
    public void EmitStorm(SdfProgramBuilder builder, int count, int material) {
        ArgumentNullException.ThrowIfNull(builder);

        var n = Math.Clamp(value: count, min: 0, max: Math.Min(SdfBenchScene.MaxStormInstances, SdfProgramBuilder.MaxInstances));

        for (var index = 0; (index < n); index++) {
            // boundOffset zero: the whole orbit+bob displacement is baked into the slot's per-frame position, so the
            // instance's bound (center = slot position + offset) tracks the mover and need only cover the sphere.
            builder.BeginInstanceDynamic(slot: index, boundOffset: Vector3.Zero, boundRadius: SdfBenchScene.StormBoundRadius);
            _ = builder.ResetPoint().TransformDynamic(slot: index).Sphere(radius: SdfBenchScene.StormInstanceRadius, material: material);
            builder.EndInstance();
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
            SdfBenchOp.Elongate => chain.Elongate(extents: new Vector3(0.3f, 0f, 0f)),
            SdfBenchOp.Repeat => chain.Repeat(spacing: new Vector3(3f, 3f, 3f)),
            SdfBenchOp.RepeatLimited => chain.RepeatLimited(spacing: new Vector3(3f, 3f, 3f), limit: new Vector3(1f, 1f, 1f)),
            SdfBenchOp.Polar => chain.RepeatPolar(count: 6, axis: SdfPolarAxis.Y),
            SdfBenchOp.Symmetry => chain.SymmetryPlane(normal: Vector3.UnitX),
            SdfBenchOp.Wallpaper => chain.WallpaperFold(group: SdfWallpaperGroup.P4M, cell: new Vector2(2.5f, 2.5f), limit: new Vector2(2f, 2f), plane: SdfWallpaperPlane.XZ),
            SdfBenchOp.LogSphere => chain.LogSphere(shellRatio: 2f),
            SdfBenchOp.CellJitter => chain.CellJitter(spacing: new Vector3(3f, 3f, 3f), jitter: 0.5f),
            SdfBenchOp.DomainWarp => chain.DomainWarp(frequency: new Vector3(2f, 2f, 2f), amplitude: 0.15f),
            SdfBenchOp.Scale => chain.Scale(scale: new Vector3(0.8f, 0.8f, 0.8f)),
            _ => chain, // field ops apply after the shape (below) — no point fold
        };

        chain = AppendShape(builder: chain, kind: SdfDebugShapeKind.Torus, parameters: BenchTorusParams, lift: SdfLift.Revolve, liftAmount: 0.5f, material: material);

        // FIELD-class ops shell/inflate/displace the accumulated field AFTER the shape.
        _ = op switch {
            SdfBenchOp.Displace => chain.Displace(frequency: new Vector3(6f, 6f, 6f), amplitude: 0.08f),
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
                ((ix * SdfBenchScene.InstanceSpacing) - half),
                ((iy * SdfBenchScene.InstanceSpacing) - half),
                ((iz * SdfBenchScene.InstanceSpacing) - half)
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
    }

    /// <summary>Emits the CARVE bench workload: a fixed ~2-unit subject sphere + a floor (world-level), then
    /// <paramref name="count"/> carves in the given <paramref name="family"/> — <see cref="SdfBenchCarveFamily.Clustered"/>
    /// (packed on the subject surface, densely overlapping the same tiles: the honest views-cost worst case),
    /// <see cref="SdfBenchCarveFamily.Scattered"/> (spread through empty space + the floor, mostly masking out: the
    /// beam-wall control where beam grows O(n) while views stays flat), or <see cref="SdfBenchCarveFamily.Smooth"/>
    /// (clustered SmoothSubtraction — halo × mask-width pressure). Placement is DETERMINISTIC (golden-angle /
    /// low-discrepancy, no RNG), so a run reproduces bit-for-bit across sessions.</summary>
    public void EmitBenchCarves(SdfProgramBuilder builder, SdfBenchCarveFamily family, int count, int material) {
        ArgumentNullException.ThrowIfNull(builder);

        var n = Math.Clamp(value: count, min: 0, max: SdfDebugScene.MaxCarves);

        // The subject the carves bite — a large sphere at the origin unioned with a floor plane. Both are WORLD-level
        // (always evaluated); the carves are the instances the bench actually measures.
        _ = builder.ResetPoint().Sphere(radius: BenchCarveSubjectRadius, material: material);
        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: BenchCarveFloorDrop, material: material);

        var smooth = (family == SdfBenchCarveFamily.Smooth);
        var carves = new List<SdfCarve>(capacity: n);

        for (var index = 0; (index < n); index++) {
            var center = ((family == SdfBenchCarveFamily.Scattered) ? ScatteredCarveCenter(index: index) : ClusteredCarveCenter(index: index, count: n));

            carves.Add(item: new SdfCarve(Center: center, Radius: BenchCarveRadius, Smooth: smooth, SmoothK: BenchCarveSmoothK));
        }

        EmitCarves(builder: builder, carves: carves, material: material);
    }

    // A carve center ON the subject surface via the Fibonacci (golden-angle) sphere — a deterministic even spread. At
    // high counts the carves densely overlap (footprint sum >> the subject's surface area), so many share the same
    // screen tiles: the honest views-cost worst case (every overlapping carve is evaluated for each covered tile).
    private static Vector3 ClusteredCarveCenter(int index, int count) {
        const float goldenAngle = 2.399963f; // π · (3 − √5)
        var t = ((index + 0.5f) / MathF.Max(1f, count));
        var y = (1f - (2f * t));
        var ring = MathF.Sqrt(MathF.Max(0f, (1f - (y * y))));
        var phi = (index * goldenAngle);
        var direction = new Vector3((ring * MathF.Cos(x: phi)), y, (ring * MathF.Sin(x: phi)));

        return (direction * BenchCarveSubjectRadius);
    }

    // A carve center in a large cube (empty space + the floor) via the R2 low-discrepancy sequence (Roberts' additive
    // recurrence, plastic-number alphas) — deterministic, hash-free, evenly spread with no clumping. Most land far from
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

        return new Vector3(((x - 0.5f) * BenchScatterExtent), ((y - 0.5f) * BenchScatterExtent), ((z - 0.5f) * BenchScatterExtent));
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
