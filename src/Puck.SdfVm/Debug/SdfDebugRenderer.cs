using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm.Debug;

/// <summary>
/// Emits the fullscreen SDF debug subject into an <see cref="SdfProgramBuilder"/>. The subject is world-level (no
/// instance, no dynamic transform) — one pair needs neither. The <see cref="SdfDebugScene.Scope"/> toggle chooses
/// between the two emission orders below; their contrast is the debugging tool for the scoped field accumulator.
/// <para>
/// Common to both: point-class ops (stack order) warp shape 1's evaluation point and emit first — the op stack is
/// shape 1's (shape 2 re-anchors with its own ResetPoint). Shape 2 (optional) is translated by its offset and blended
/// against the accumulator, which at that moment is exactly shape 1's field, so even the intersection/xor families
/// compose exactly the authored pair ("author an intersection pair first, against the empty accumulator" holds by
/// construction — the pair is the whole subject so far). The ground plane is always a plain Union.
/// </para>
/// <para>
/// Scoped (default, <see cref="SdfDebugScene.Scope"/> on): point ops → <c>PushField(Union)</c> → shape 1 → field-class
/// ops (onion/dilate/displace) → shape 2 → <c>PopField</c> → floor. The field ops sit inside the scope, so they shell
/// the subject alone; the floor, emitted after the Pop, stays solid. This is the correct authoring form — a field op
/// bounded to its subtree.
/// </para>
/// <para>
/// Flat (<see cref="SdfDebugScene.Scope"/> off): point ops → shape 1 → shape 2 → floor → field-class ops last. Every
/// field op runs on the one shared accumulator after the whole scene (subject and floor) is assembled, so an onion
/// doubles the floor's zero-contour and a dilate fattens it — the "a field op shells the whole scene" pathology the
/// scoped accumulator exists to fix. (Onion/Dilate are point-independent; a flat-mode Displace reads the floor's
/// point context — an accepted debug quirk, since the point of flat mode is to exhibit the flat behavior, and the
/// scoped path is the correct authoring form.)
/// </para>
/// Carves (the runtime subtraction pool) always emit last, after the subject/floor and any flat-mode field ops — one
/// static <see cref="SdfBlendOp.Subtraction"/> (or <see cref="SdfBlendOp.SmoothSubtraction"/>) instance per carve, so
/// each bites the already-unioned subject+floor. <see cref="EmitProbe"/> emits the worst case (max stack + the two
/// wordiest shapes + floor + scoped Push/Pop + a full pool of <see cref="SdfDebugScene.MaxCarves"/> carves) so the
/// capacity probe covers any live state.
/// </summary>
public sealed class SdfDebugRenderer {
    // A pleasant off-white subject with modest specular, so the lit / normals views read form; the floor is a dimmer
    // neutral so it never competes with the subject. Full-bright debug room (the frame source pins Ambient/Sun to 1).
    private static readonly Vector3 SubjectAlbedo = new(
        x: 0.78f,
        y: 0.75f,
        z: 0.70f
    );
    // The second shape's albedo — a muted teal, clearly distinct from shape 1's off-white so a hard-blend pair reads
    // as two bodies in the lit view (a smooth/chamfer seam still shades by material winner per pixel).
    private static readonly Vector3 Shape2Albedo = new(
        x: 0.35f,
        y: 0.62f,
        z: 0.60f
    );
    private static readonly Vector3 FloorAlbedo = new(
        x: 0.28f,
        y: 0.30f,
        z: 0.34f
    );
    // The carve cavity walls: a dark interior tone so a subtracted cavity reads as an exposed hollow against the
    // off-white subject (the carve sphere is the cutter — its material shades the newly-exposed inner surface).
    private static readonly Vector3 CarveAlbedo = new(
        x: 0.20f,
        y: 0.18f,
        z: 0.22f
    );

    // The ground plane sits a little below the subject (its surface at y = -FloorDrop), so a ~1-unit shape rests on it.
    // Internal: the meteor shower (SdfDebugScene.TickMeteor) lands floor craters relative to this surface height.
    internal const float FloorDrop = 1.3f;

    // The carve-ceiling exhibit's fixed scene: a ~2-unit sphere at the origin (radius 1.6 → ~3.2 across) plus a floor
    // below it, bitten by a golden-angle cluster of hard carves at the live default radius.
    private const int CarveCeilingCount = 256;
    private const float CarveCeilingFloorDrop = 2.2f;
    private const float CarveCeilingSubjectRadius = 1.6f;
    private const float SubjectRoughness = 0.613f; // = 1 - sqrt((40 - 2) / 254), the roughness whose exponent is 40
    private const float SubjectSpecular = 0.35f;

    // Appends ONE primitive from the shared catalog: `kind`/`parameters` select it (shape 1 or shape 2 — same
    // catalog), `lift`/`liftAmount` supply the shared 2D-family lift, and `blend`/`smooth` ride the primitive
    // instruction (shape 1 passes the default Union-against-empty; shape 2 passes the authored pair blend).
    internal static SdfProgramBuilder AppendShape(SdfProgramBuilder builder, SdfDebugShapeKind kind, IReadOnlyList<float> parameters, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        var p = parameters;

        float At(int index, float fallback) => ((index < p.Count)
            ? p[index]
            : fallback
        );

        return kind switch {
            SdfDebugShapeKind.Sphere => builder.Sphere(
            radius: At(
                fallback: 1f,
                index: 0
            ),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Box => builder.Box(
            halfExtents: new Vector3(
                x: At(
                    fallback: 0.8f,
                    index: 0
                ),
                y: At(
                    fallback: 0.8f,
                    index: 1
                ),
                z: At(
                    fallback: 0.8f,
                    index: 2
                )
            ),
            round: At(
                fallback: 0.05f,
                index: 3
            ),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Torus => builder.Torus(
            majorRadius: At(
                fallback: 1f,
                index: 0
            ),
            minorRadius: At(
                fallback: 0.35f,
                index: 1
            ),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Capsule => builder.Capsule(
            endpoint: new Vector3(
                x: 0f,
                y: (2f * At(
                    fallback: 1f,
                    index: 0
                )),
                z: 0f
            ),
            radius: At(
                fallback: 0.35f,
                index: 1
            ),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Cylinder => builder.Cylinder(
            radius: At(
                fallback: 0.7f,
                index: 0
            ),
            halfHeight: At(
                fallback: 1f,
                index: 1
            ),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Ellipsoid => builder.Superellipsoid(
            exponent: SdfProgramBuilder.MinSuperellipsoidExponent,
            radii: new Vector3(
                x: At(
                    fallback: 1f,
                    index: 0
                ),
                y: At(
                    fallback: 0.7f,
                    index: 1
                ),
                z: At(
                    fallback: 0.5f,
                    index: 2
                )
            ),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Vesica => builder.Vesica(
            radius: At(
                fallback: 1f,
                index: 0
            ),
            halfSeparation: At(
                fallback: 0.5f,
                index: 1
            ),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.RoundCone => builder.RoundCone(
            lowerRadius: At(
                fallback: 0.7f,
                index: 0
            ),
            upperRadius: At(
                fallback: 0.3f,
                index: 1
            ),
            height: At(
                fallback: 1.2f,
                index: 2
            ),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.RoundedRect => builder.RoundedRectangle(
            halfWidth: At(
                fallback: 0.8f,
                index: 0
            ),
            halfHeight: At(
                fallback: 0.5f,
                index: 1
            ),
            cornerRadius: At(
                fallback: 0.15f,
                index: 2
            ),
            lift: lift,
            liftAmount: liftAmount,
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Polygon => builder.RegularPolygon(
            sides: ((int)At(
                fallback: 6f,
                index: 0
            )),
            radius: At(
                fallback: 0.9f,
                index: 1
            ),
            lift: lift,
            liftAmount: liftAmount,
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Star => builder.Star(
            points: ((int)At(
                fallback: 5f,
                index: 0
            )),
            radius: At(
                fallback: 0.9f,
                index: 1
            ),
            sharpness: At(
                fallback: 2.6f,
                index: 2
            ),
            lift: lift,
            liftAmount: liftAmount,
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Trapezoid => builder.Trapezoid(
            bottomHalfWidth: At(
                fallback: 0.8f,
                index: 0
            ),
            topHalfWidth: At(
                fallback: 0.4f,
                index: 1
            ),
            halfHeight: At(
                fallback: 0.7f,
                index: 2
            ),
            lift: lift,
            liftAmount: liftAmount,
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfDebugShapeKind.Ellipse => builder.Ellipse(
            semiX: At(
                fallback: 0.9f,
                index: 0
            ),
            semiY: At(
                fallback: 0.6f,
                index: 1
            ),
            lift: lift,
            liftAmount: liftAmount,
            material: material,
            blend: blend,
            smooth: smooth
        ),
            _ => builder.Sphere(
            blend: blend,
            material: material,
            radius: 1f,
            smooth: smooth
        ),
        };
    }
    internal static void EmitCarve(SdfProgramBuilder builder, SdfCarve carve, int material) {
        var blend = (carve.Smooth
            ? SdfBlendOp.SmoothSubtraction
            : SdfBlendOp.Subtraction
        );

        builder.BeginInstance(
            boundCenter: carve.Center,
            boundRadius: carve.Radius
        );
        _ = builder.ResetPoint().Translate(offset: carve.Center).Sphere(
            radius: carve.Radius,
            material: material,
            blend: blend,
            smooth: (carve.Smooth
            ? carve.SmoothK
            : 0f)
        );
        builder.EndInstance();
    }
    // Emits each carve as a STATIC world-level instance: a bounding sphere at the carve, holding one Sphere baked to the
    // carve position (ResetPoint + Translate) that SUBTRACTS from the running accumulator (hard Subtraction, or
    // SmoothSubtraction with k when Smooth). Carves don't move, so the instance is STATIC (no dynamic slot). Bound =
    // carve radius EXACTLY — the packer adds float-safety padding and the smooth halo, so passing the radius alone is
    // correct (double-inflating would over-cull the cavity's seam tiles). Shared by the carve-ceiling exhibit
    // (EmitCarveCeiling) and folded worst-case by EmitProbe.
    internal static void EmitCarves(SdfProgramBuilder builder, IReadOnlyList<SdfCarve> carves, int material) {
        foreach (var carve in carves) {
            EmitCarve(
                builder: builder,
                carve: carve,
                material: material
            );
        }
    }

    private static SdfProgramBuilder ApplyFieldOp(SdfProgramBuilder builder, SdfDebugOp op) {
        return op.Kind switch {
            SdfDebugOpKind.Onion => builder.Onion(thickness: op.A),
            SdfDebugOpKind.Dilate => builder.Dilate(radius: op.A),
            SdfDebugOpKind.Displace => builder.Displace(
            frequency: op.V0,
            amplitude: op.A
        ),
            _ => builder,
        };
    }
    // Applies every FIELD-class op in emission order (shared by the scoped and flat paths in Emit — the two orders
    // differ only in WHERE this runs, never how). builder === the fluent chain, so the returned builder is just the
    // running chain after each op.
    private static SdfProgramBuilder ApplyFieldOps(SdfProgramBuilder builder, IEnumerable<SdfDebugOp> ops) {
        var chain = builder;

        foreach (var op in ops) {
            if (SdfDebugScene.IsFieldOp(kind: op.Kind)) {
                chain = ApplyFieldOp(
                    builder: chain,
                    op: op
                );
            }
        }

        return chain;
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
            SdfDebugOpKind.RepeatLimited => builder.RepeatLimited(
            spacing: op.V0,
            limit: op.V1
        ),
            SdfDebugOpKind.Polar => builder.RepeatPolar(
            count: op.I0,
            axis: ((SdfPolarAxis)op.I1),
            mirror: op.Flag
        ),
            SdfDebugOpKind.Symmetry => builder.SymmetryPlane(
            normal: op.V0,
            offset: op.A
        ),
            SdfDebugOpKind.LogSphere => builder.LogSphere(
            shellRatio: op.A,
            twist: op.B
        ),
            SdfDebugOpKind.CellJitter => builder.CellJitter(
            spacing: op.V0,
            jitter: op.A,
            seed: ((uint)op.I0),
            tumble: op.B
        ),
            SdfDebugOpKind.DomainWarp => builder.DomainWarp(
            frequency: op.V0,
            amplitude: op.A
        ),
            _ => builder,
        };
    }
    // A carve center ON the subject surface via the Fibonacci (golden-angle) sphere — a deterministic even spread. At
    // high counts the carves densely overlap (footprint sum >> the subject's surface area), so many share the same
    // screen tiles.
    private static Vector3 ClusteredCarveCenter(int index, int count) {
        const float GoldenAngle = 2.399963f; // π · (3 − √5)
        var t = ((index + 0.5f) / MathF.Max(
            x: 1f,
            y: count
        ));
        var y = (1f - (2f * t));
        var ring = MathF.Sqrt(x: MathF.Max(
            x: 0f,
            y: (1f - (y * y))
        ));
        var phi = (index * GoldenAngle);
        var direction = new Vector3(
            x: (ring * MathF.Cos(x: phi)),
            y: y,
            z: (ring * MathF.Sin(x: phi))
        );

        return (direction * CarveCeilingSubjectRadius);
    }
    // The carve-ceiling exhibit: a world-level subject sphere + floor, then CarveCeilingCount hard carves clustered on
    // the subject surface as static analytic instances. Placement is golden-angle (no RNG), so the exhibit reproduces.
    private static void EmitCarveCeiling(SdfProgramBuilder builder, int material) {
        _ = builder.ResetPoint().Sphere(
            radius: CarveCeilingSubjectRadius,
            material: material
        );
        _ = builder.ResetPoint().Plane(
            normal: Vector3.UnitY,
            offset: CarveCeilingFloorDrop,
            material: material
        );

        for (var index = 0; (index < CarveCeilingCount); index++) {
            EmitCarve(
                builder: builder,
                carve: new SdfCarve(
                    Center: ClusteredCarveCenter(
                        count: CarveCeilingCount,
                        index: index
                    ),
                    Radius: SdfDebugScene.DefaultCarveRadius,
                    Smooth: false,
                    SmoothK: SdfDebugScene.DefaultCarveSmoothK
                ),
                material: material
            );
        }
    }
    // The gallery's ground plane (its own dimmer neutral material), at the same drop the debug subject's floor uses.
    private void EmitGalleryFloor(SdfProgramBuilder builder) {
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: FloorAlbedo));

        _ = builder.ResetPoint().Plane(
            normal: Vector3.UnitY,
            offset: FloorDrop,
            material: floorMaterial
        );
    }
    // Eight spheres in a row, each folded into the accumulator with an ALTERNATING SmoothUnion/ChamferUnion blend — a
    // long chain whose per-blend LSB rounding accumulates down its length (the scoped accumulator bounds its reach).
    private static void EmitSmoothChain(SdfProgramBuilder builder, int material) {
        const int Links = 8;

        var chain = builder.ResetPoint().Translate(offset: new Vector3(
            x: -2.1f,
            y: 0f,
            z: 0f
        )).Sphere(
            radius: 0.45f,
            material: material
        );

        for (var index = 1; (index < Links); index++) {
            var blend = (((index & 1) == 0)
                ? SdfBlendOp.SmoothUnion
                : SdfBlendOp.ChamferUnion
            );
            var x = (-2.1f + (index * 0.6f));
            var y = (0.15f * MathF.Sin(x: (index * 1.1f)));

            chain = chain.ResetPoint().Translate(offset: new Vector3(
                x: x,
                y: y,
                z: 0f
            )).Sphere(
                blend: blend,
                material: material,
                radius: 0.45f,
                smooth: 0.3f
            );
        }
    }

    /// <summary>Emits the debug subject (+ optional floor) for a live render.</summary>
    /// <param name="builder">The program builder (the program is only this subject while the mode is up).</param>
    /// <param name="scene">The debug scene state.</param>
    public void Emit(SdfProgramBuilder builder, SdfDebugScene scene) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(scene);

        var subjectMaterial = builder.AddMaterial(material: new SdfMaterial(
            Albedo: SubjectAlbedo,
            Specular: SubjectSpecular,
            Roughness: SubjectRoughness
        ));
        var scoped = scene.Scope;
        var chain = builder.ResetPoint();

        // POINT-class ops (folds/warps/transforms) apply BEFORE shape 1, in push order — the stack is SHAPE 1's. They
        // warp only the point; the field scope (opened next in scoped mode) reseeds only the FIELD, so they sit ahead
        // of the PushField either way.
        foreach (var op in scene.Ops) {
            if (!SdfDebugScene.IsFieldOp(kind: op.Kind)) {
                chain = ApplyPointOp(
                    builder: chain,
                    op: op
                );
            }
        }

        // SCOPED mode: open the subject scope so shape 1, its field ops, and shape 2 accumulate into a FRESH field.
        if (scoped) {
            chain = chain.PushField(compose: SdfBlendOp.Union);
        }

        chain = AppendShape(
            builder: chain,
            kind: scene.Shape,
            parameters: scene.Params,
            lift: scene.Lift,
            liftAmount: scene.LiftAmount,
            material: subjectMaterial
        );

        // FIELD-class ops (shell/inflate/relief). SCOPED: emit them HERE, inside the scope, so they shell the subject
        // (shape 1) alone. FLAT: DEFER them to the end of the program (below), so they shell the whole accumulated
        // field — subject AND floor — which is the flat-accumulator pathology the toggle exists to contrast.
        if (scoped) {
            chain = ApplyFieldOps(
                builder: chain,
                ops: scene.Ops
            );
        }

        // SHAPE 2 (optional): re-anchor the point (ResetPoint drops shape 1's point ops), translate to its offset,
        // and blend against the accumulator — which IS shape 1's field here (scoped: the fresh scope field; flat: the
        // shared field, still just shape 1), so the intersection/xor families compose exactly the authored pair.
        if (scene.Shape2 is { } second) {
            var secondMaterial = builder.AddMaterial(material: new SdfMaterial(
                Albedo: Shape2Albedo,
                Specular: SubjectSpecular,
                Roughness: SubjectRoughness
            ));

            chain = chain.ResetPoint().Translate(offset: scene.Offset2);
            chain = AppendShape(
                builder: chain,
                kind: second,
                parameters: scene.Params2,
                lift: scene.Lift,
                liftAmount: scene.LiftAmount,
                material: secondMaterial,
                blend: scene.Blend,
                smooth: scene.BlendSmooth
            );
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

            _ = builder.ResetPoint().Plane(
                normal: Vector3.UnitY,
                offset: FloorDrop,
                material: floorMaterial
            );
        }

        // FLAT mode: apply the field ops LAST, on the ONE shared field (subject + floor) — an onion doubles the floor's
        // zero-contour, a dilate fattens the whole scene. (builder === chain — the fluent builder returns this — so the
        // discarded return just runs the op on the shared accumulator; the point context is the floor's, an accepted
        // flat-mode Displace quirk noted in the class remarks.)
        if (!scoped) {
            _ = ApplyFieldOps(
                builder: builder,
                ops: scene.Ops
            );
        }

        // CARVES (the subtraction pool) emit LAST — after the subject, the floor, AND any flat-mode field ops — so each
        // carve has a higher segment index than everything it bites. By here the accumulator holds subject UNION floor
        // (a scoped subject was already popped with a Union compose, so its field is folded in), so ONE static instance
        // per carve subtracts from BOTH at once. Subtraction is FAR-NEUTRAL (max(acc, -sphere) = acc beyond the sphere),
        // so each carve packs a finite bound (its radius) and stays cullable — the smooth variant's k halo is added by
        // the packer (MaxSmoothBlendRadius); do NOT inflate it here.
        if (scene.Carves.Count > 0) {
            var carveMaterial = builder.AddMaterial(material: new SdfMaterial(
                Albedo: CarveAlbedo,
                Specular: SubjectSpecular,
                Roughness: SubjectRoughness
            ));

            // Route through the scene's carve-bake planner: it emits one SampledRegion per adopted
            // bin and analytic instances for the rest — and, with the switch off or nothing baked, a byte-identical
            // analytic emission (the same instructions the raw EmitCarves loop produces).
            scene.CarvePlanner.Emit(
                builder: builder,
                carves: scene.Carves,
                material: carveMaterial
            );
        }
    }
    // ── SDF gallery (the torture museum) exhibit emitters ───────────────────────────────────────────────────────────
    // Each emits ONE hand-authored known-nasty scene (a takeover, like the debug subject), reusing the
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

        var material = builder.AddMaterial(material: new SdfMaterial(
            Albedo: SubjectAlbedo,
            Specular: SubjectSpecular,
            Roughness: SubjectRoughness
        ));

        switch (exhibit) {
            case SdfGalleryExhibit.LiarSpiral:
                // A thin blade twisted HARD (rate 3): the field over-estimates distance where the twist shears space, so
                // it breaks 1-Lipschitz — the Lipschitz clamp's whole reason. Pairs with world.debug-view overshoot.
                _ = builder.ResetPoint().TwistY(rate: 3f).Box(
                    halfExtents: new Vector3(
                        x: 0.18f,
                        y: 1.4f,
                        z: 0.9f
                    ),
                    round: 0.02f,
                    material: material
                );

                break;
            case SdfGalleryExhibit.DrosteTunnel:
                // LogSphere shellRatio 2 — a discontinuous log-polar fold that stresses cross-backend parity.
                _ = builder.ResetPoint().LogSphere(shellRatio: 2f).Sphere(
                    radius: 1f,
                    material: material
                );

                break;
            case SdfGalleryExhibit.CellJitterCreases:
                // A CONTAINED jittered prototype (jitter/2 + radius = 0.4 + 0.5 <= spacing/2 = 1.0) that STILL seams: the
                // round fold picks each point's own cell, not the nearest copy.
                _ = builder.ResetPoint().CellJitter(
                    spacing: new Vector3(
                        x: 2f,
                        y: 2f,
                        z: 2f
                    ),
                    jitter: 0.8f,
                    seed: 1u,
                    tumble: 0f
                ).Sphere(
                    radius: 0.5f,
                    material: material
                );

                break;
            case SdfGalleryExhibit.NotchHorizon:
                // A ground plane stretching to the horizon plus two grounded reference boxes — the far-ground silhouette
                // against the sky is the notch (grazed by the exhibit's low-pitch pose).
                EmitGalleryFloor(builder: builder);
                _ = builder.ResetPoint().Translate(offset: new Vector3(
                    x: -1.2f,
                    y: (0.4f - FloorDrop),
                    z: -2.5f
                )).Box(
                    halfExtents: new Vector3(
                        x: 0.4f,
                        y: 0.4f,
                        z: 0.4f
                    ),
                    round: 0.03f,
                    material: material
                );
                _ = builder.ResetPoint().Translate(offset: new Vector3(
                    x: 1.4f,
                    y: (0.6f - FloorDrop),
                    z: -4f
                )).Box(
                    halfExtents: new Vector3(
                        x: 0.5f,
                        y: 0.6f,
                        z: 0.5f
                    ),
                    round: 0.03f,
                    material: material
                );

                break;
            case SdfGalleryExhibit.SmoothChain:
                EmitSmoothChain(
                    builder: builder,
                    material: material
                );

                break;
            case SdfGalleryExhibit.WallpaperP4G:
                // P4G renders as p4 (KNOWN DEFECT) — an ASYMMETRIC tile reveals the dropped mirror classes. Tiles XZ.
                _ = builder.ResetPoint().WallpaperFold(
                    group: SdfWallpaperGroup.P4G,
                    cell: new Vector2(
                        x: 2f,
                        y: 2f
                    ),
                    limit: new Vector2(
                        x: 3f,
                        y: 3f
                    ),
                    plane: SdfWallpaperPlane.XZ
                ).Box(
                    halfExtents: new Vector3(
                        x: 0.55f,
                        y: 0.3f,
                        z: 0.22f
                    ),
                    round: 0.03f,
                    material: material
                );

                break;
            case SdfGalleryExhibit.CarveCeiling:
                // ~256 clustered hard carves on a subject sphere + floor — the honest destruction budget made visible.
                // Watch with world.debug-view mask.
                EmitCarveCeiling(
                    builder: builder,
                    material: material
                );

                break;
            case SdfGalleryExhibit.LogSphereRunDoc:
                // Aggressive LogSphere (shellRatio 2.8 + twist) over a floor — validator-legal, marcher-breaking when the
                // camera sits DOWN INSIDE the fold (the pose the scene supplies). Pairs with overshoot + termination.
                _ = builder.ResetPoint().LogSphere(
                    shellRatio: 2.8f,
                    twist: 0.6f
                ).Sphere(
                    radius: 1.2f,
                    material: material
                );
                EmitGalleryFloor(builder: builder);

                break;
            default:
                _ = builder.ResetPoint().Sphere(
                    radius: 1f,
                    material: material
                );

                break;
        }
    }
    /// <summary>Emits the worst-case debug program (a full <see cref="SdfDebugScene.MaxOps"/> stack of single-word ops
    /// plus the wordiest shapes, the floor, and the scoped Push/Pop pair — the wordier of the two emission orders) so
    /// the frame source's capacity probe reserves an envelope every live push fits inside. Never rendered.
    /// <para>This probe also bounds every gallery exhibit (<see cref="EmitGallery"/>): the largest instance exhibit is
    /// the carve-ceiling (a subject sphere + floor + 256 carve instances), and 256 &lt; the full <see cref="SdfDebugScene.MaxCarves"/>
    /// (4096) carve pool this probe already reserves; the largest word exhibit is the drift monolith
    /// (<see cref="SdfDriftMonolith"/>, ~50 world-level instructions across ten materials, zero instances), still far
    /// under the 12-op stack + two lifted Stars + scope + 4096-carve pool below — so no exhibit can outgrow the frozen
    /// envelope and none needs its own probe.</para></summary>
    /// <param name="builder">The probe builder (already carrying the room's own worst case).</param>
    public void EmitProbe(SdfProgramBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        var subjectMaterial = builder.AddMaterial(material: new SdfMaterial(
            Albedo: SubjectAlbedo,
            Specular: SubjectSpecular,
            Roughness: SubjectRoughness
        ));
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: FloorAlbedo));

        _ = builder.ResetPoint().Plane(
            normal: Vector3.UnitY,
            offset: FloorDrop,
            material: floorMaterial
        );

        var chain = builder.ResetPoint();

        // Every op is a single instruction, so MaxOps of any op is the worst instruction count regardless of the
        // point/field split — a full stack of Repeat covers it. The wordiest shape (a lifted Star bakes the most
        // constants) closes the subject; the SECOND shape's worst case (ResetPoint + Translate + another lifted Star
        // with a smooth blend — the blend rides the shape instruction, zero extra words) follows, so a live pair with
        // a full stack always fits the frozen envelope. The second material joins the probe's table too.
        for (var index = 0; (index < SdfDebugScene.MaxOps); index++) {
            chain = chain.Repeat(spacing: new Vector3(
                x: 4f,
                y: 4f,
                z: 4f
            ));
        }

        // Wrap the pair in the scoped Push/Pop (two extra words): scoped mode is strictly wordier than flat (flat adds
        // no scope instructions), so reserving for it covers both. The scope holds two shapes, satisfying PopField's
        // at-least-one-shape rule.
        chain = chain.PushField(compose: SdfBlendOp.Union);
        chain = chain.Star(
            points: 5,
            radius: 0.9f,
            sharpness: 2.6f,
            lift: SdfLift.Revolve,
            liftAmount: 0.5f,
            material: subjectMaterial
        );

        var secondMaterial = builder.AddMaterial(material: new SdfMaterial(
            Albedo: Shape2Albedo,
            Specular: SubjectSpecular,
            Roughness: SubjectRoughness
        ));

        chain = chain.ResetPoint().Translate(offset: new Vector3(
            x: 1.2f,
            y: 0f,
            z: 0f
        )).Star(
            blend: SdfBlendOp.SmoothUnion,
            lift: SdfLift.Revolve,
            liftAmount: 0.5f,
            material: secondMaterial,
            points: 5,
            radius: 0.9f,
            sharpness: 2.6f,
            smooth: 0.25f
        );

        _ = chain.PopField();

        // CARVES: the live subject can carry up to MaxCarves carves, each a static subtraction instance emitted after
        // the subject/floor (see Emit + EmitCarves). Fold the worst FORM — SMOOTH subtraction — into the probe; a hard
        // carve is word/instruction-identical (the smooth halo is a packer BOUND inflation, not extra words), so smooth
        // vs. hard costs the same envelope. Full pool of MaxCarves, all at the origin (position is irrelevant to size).
        //
        // ENVELOPE MATH. Each carve = 1 instance (BeginInstance/EndInstance) + 3 instructions (ResetPoint, Translate,
        // Sphere) + one instance-directory entry (2 vectors = 8 words). The debug subject + floor are WORLD-level (0
        // instances), so the LIVE debug program tops out at MaxCarves = 4096 instances — well inside MaxInstances =
        // 65536. This probe over-covers by folding the 4096 carves ON TOP OF the room's own instances (a few dozen), so
        // the subject probe is (room + 4096) << 65536.
        var carveMaterial = builder.AddMaterial(material: new SdfMaterial(
            Albedo: CarveAlbedo,
            Specular: SubjectSpecular,
            Roughness: SubjectRoughness
        ));
        var worstCarves = new List<SdfCarve>(capacity: SdfDebugScene.MaxCarves);

        for (var index = 0; (index < SdfDebugScene.MaxCarves); index++) {
            worstCarves.Add(item: new SdfCarve(
                Center: Vector3.Zero,
                Radius: SdfDebugScene.DefaultCarveRadius,
                Smooth: true,
                SmoothK: SdfDebugScene.DefaultCarveSmoothK
            ));
        }

        EmitCarves(
            builder: builder,
            carves: worstCarves,
            material: carveMaterial
        );

        // CARVE-BAKE: the worst MIXED case adds MaxBricks full-resolution SampledRegion instances ON TOP of the full
        // analytic pool — over-covering (a baked bin's carves are NOT also emitted analytic), but
        // it keeps the frozen envelope safe for any settle state, brick or analytic.
        SdfCarveBakePlanner.EmitWorstCaseBricks(
            builder: builder,
            material: carveMaterial
        );
    }
}
/// <summary>Adapts <see cref="SdfDebugMode"/> — the composition facade that already owns the "which takeover is live"
/// dispatch (a gallery exhibit or the plain debug subject — see <see cref="SdfDebugMode.Emit"/>) — onto the
/// <see cref="ISdfSceneEmitter"/> contract, so a composition host can register the whole SDF-debug surface as one
/// emitter without re-deriving that dispatch. The gallery emitter (<see cref="SdfDebugRenderer.EmitGallery"/>) stays an
/// ordinary method <see cref="SdfDebugMode.Emit"/> calls internally — this type adds nothing beyond the probe branch
/// and the revision plumbing. Every takeover program is static, so the emitter owns no dynamic-transform slots.
/// <para>
/// A takeover, not a composable layer: the debug mode replaces the rest of a scene while active (see
/// <see cref="SdfDebugMode.Active"/>) — a composition host swaps this emitter into an alternate emitter list for the
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
    public void WriteRevision(Span<int> destination) => m_mode.WriteRevision(destination: destination);

    /// <inheritdoc/>
    public int RevisionComponentCount => SdfDebugMode.RevisionComponentCount;
}
