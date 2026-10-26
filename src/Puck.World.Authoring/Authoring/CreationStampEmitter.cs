using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.Text;

namespace Puck.World.Authoring;

/// <summary>A reflection plane in a creation stamp's local frame.</summary>
/// <param name="Normal">The unit plane normal.</param>
/// <param name="Offset">The signed plane offset along <paramref name="Normal"/>.</param>
public readonly record struct CreationStampPlane(Vector3 Normal, float Offset);
/// <summary>A creation stamp's primitive transform prefix.</summary>
/// <param name="Origin">The stamp origin in world space.</param>
/// <param name="Rotation">The stamp orientation.</param>
/// <param name="Scale">The uniform stamp scale.</param>
/// <param name="ReflectionNormal">The optional unit local normal that reflects the creation geometry.</param>
/// <param name="DynamicSlot">An optional local geometry asset's root transform slot.</param>
public readonly record struct CreationStampTransform(
    Vector3 Origin,
    Quaternion Rotation,
    float Scale,
    Vector3? ReflectionNormal,
    int? DynamicSlot = null
);
/// <summary>A document-neutral two-axis placement pattern.</summary>
/// <param name="StepA">The first placement-local step.</param>
/// <param name="CountA">The declared copy count along the first step.</param>
/// <param name="StepB">The second placement-local step.</param>
/// <param name="CountB">The declared copy count along the second step.</param>
public readonly record struct CreationStampPattern(Vector3 StepA, int CountA, Vector3 StepB, int CountB);
/// <summary>One materialized creation stamp instance.</summary>
/// <param name="Origin">The instance origin in world space.</param>
/// <param name="ReflectionNormal">The optional unit local normal that reflects the creation geometry.</param>
public readonly record struct CreationStampInstance(Vector3 Origin, Vector3? ReflectionNormal);
/// <summary>One materialized creation stamp instance, in the deterministic fixed-point domain.</summary>
/// <param name="Origin">The instance origin in world space.</param>
/// <param name="ReflectionNormal">The optional unit local normal that reflects the creation geometry.</param>
public readonly record struct FixedCreationStampInstance(FixedVector3 Origin, FixedVector3? ReflectionNormal);
/// <summary>A creation stamp's primitive transform prefix, in the deterministic fixed-point domain.</summary>
/// <param name="Origin">The stamp origin in world space.</param>
/// <param name="Rotation">The stamp orientation; normalized on entry.</param>
/// <param name="Scale">The uniform stamp scale.</param>
/// <param name="ReflectionNormal">The optional local normal that reflects the creation geometry; normalized on entry.</param>
public readonly record struct FixedCreationStampTransform(
    FixedVector3 Origin,
    FixedQuaternion Rotation,
    FixedQ4816 Scale,
    FixedVector3? ReflectionNormal
);
/// <summary>One primitive copy after a fixed-point stamp transform has been applied.</summary>
/// <param name="Shape">The authored shape.</param>
/// <param name="Center">The primitive's bound center, shared by its world-axis and oriented bounds.</param>
/// <param name="HalfExtents">The primitive's world-axis bound half-extents.</param>
/// <param name="UniformScale">The primitive's world scale when it is isotropic; zero otherwise.</param>
/// <param name="PlaneNormal">The unit world normal for an unbounded plane; zero for a finite primitive.</param>
/// <param name="Rotation">The unit rotation that turns the primitive's own axes into world space.</param>
/// <param name="LocalHalfExtents">The primitive's bound half-extents along its own axes, scaled: with
/// <paramref name="Rotation"/> and <paramref name="Center"/>, the oriented box that encloses it, never larger than
/// the world-axis bound.</param>
public readonly record struct FixedCreationStampPrimitiveCopy(ShapeDocument Shape, FixedVector3 Center, FixedVector3 HalfExtents, FixedQ4816 UniformScale, FixedVector3 PlaneNormal, FixedQuaternion Rotation, FixedVector3 LocalHalfExtents);
/// <summary>
/// Emits and expands <c>puck.creation.v1</c> shape geometry under one materialized stamp transform.
/// </summary>
public static class CreationStampEmitter {
    // The degeneracy floor on a scale axis, SdfSolidGeometry.MinimumScale in the fixed domain. Q48.16 resolves 1/65536,
    // so the floor lands on the nearest representable value above it rather than exactly — this is a guard against a
    // zero-scale axis collapsing the frame, never a value a result is read off, so the quantization is immaterial. A
    // product of two floors still underflows to zero, which yields a zero-extent (inert) collider rather than a
    // vanishingly thin one.
    private static readonly FixedQ4816 MinimumTransformExtent = FixedQ4816.FromDouble(value: SdfSolidGeometry.MinimumScale);

    // One stamp's transform constants, derived once in fixed point from either public transform record, so the render
    // program and the contact program encode the same origin, rotation, scale and mirror alignment: the render path
    // hands the builder ToVector3()/ToQuaternion() of these rather than a second, single-precision derivation.
    // FacetScale is the authored stamp scale as the render-only facets (a blend radius, a dilation, a text em) read it;
    // it is the same number the stamper laid the creation's text out with.
    private readonly record struct StampFrame(FixedVector3 Origin, FixedQuaternion Rotation, FixedQ4816 Scale, float FacetScale, FixedVector3? ReflectionNormal, FixedQuaternion? ReflectionAlignment) {
        public int? DynamicSlot { get; init; }

        public static StampFrame Of(CreationStampTransform transform) =>
            Of(
                facetScale: transform.Scale,
                normal: ((transform.ReflectionNormal is { } normal)
                ? FixedVector3.FromVector3(value: normal)
                : (FixedVector3?)null),
                origin: FixedVector3.FromVector3(value: transform.Origin),
                rotation: FixedQuaternion.FromQuaternion(value: transform.Rotation),
                scale: FixedQ4816.FromDouble(value: transform.Scale)
            ) with { DynamicSlot = transform.DynamicSlot };
        public static StampFrame Of(FixedCreationStampTransform transform) =>
            Of(
                facetScale: ((float)transform.Scale),
                normal: transform.ReflectionNormal,
                origin: transform.Origin,
                rotation: transform.Rotation,
                scale: transform.Scale
            );

        private static StampFrame Of(FixedVector3 origin, FixedQuaternion rotation, FixedQ4816 scale, float facetScale, FixedVector3? normal) {
            var unitNormal = normal?.Normalize();

            return new StampFrame(
                Origin: origin,
                Rotation: rotation.Normalize(),
                Scale: FixedQ4816.Max(
                    x: FixedQ4816.Abs(value: scale),
                    y: MinimumTransformExtent
                ),
                FacetScale: facetScale,
                ReflectionNormal: unitNormal,
                ReflectionAlignment: ((unitNormal is { } facing)
                ? CreationStampEmitter.ReflectionAlignment(unitNormal: facing)
                : (FixedQuaternion?)null)
            );
        }
    }

    // The contact program's per-axis shape scale: the authored magnitudes floored at the degeneracy guard, in fixed
    // point. The render program hands SdfSolidGeometry the authored scale as is; its own MinimumScale floor is the
    // same rule, applied once inside every primitive, reach and half-extent it answers.
    private static FixedVector3 EffectiveScale(Vector3 value) =>
        new(
            X: FixedQ4816.Max(
                x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.X)),
                y: MinimumTransformExtent
            ),
            Y: FixedQ4816.Max(
                x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.Y)),
                y: MinimumTransformExtent
            ),
            Z: FixedQ4816.Max(
                x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.Z)),
                y: MinimumTransformExtent
            )
        );
    // The one place a shape's authored primitive facets (taper, profile, lift, rounding, chamfer, exponent, curve)
    // reach SdfSolidGeometry: every emission of a shape's primitive, plate copy, trim host and trim reference goes
    // through here, so no two emissions can disagree on a default. A trim's reference re-reads only the primitive
    // geometry, never its sweep curve (ShapeTrimDocument), which `curve: false` leaves out.
    private static SdfProgramBuilder AppendPrimitive(SdfProgramBuilder chain, ShapeDocument shape, Vector3 scale, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, bool detail = false, bool curve = true) =>
        SdfSolidGeometry.AppendScaledPrimitive(
            chain: chain,
            type: shape.Type,
            taper: (shape.Taper ?? 0.5f),
            profile: shape.Profile,
            lift: (shape.Lift ?? SdfLift.Extrude),
            rounding: (shape.Rounding ?? 0f),
            chamfer: (shape.Chamfer ?? 0f),
            exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
            curve: (curve
            ? shape.Curve?.Parameters()
            : null),
            scale: scale,
            material: material,
            blend: blend,
            smooth: smooth,
            detail: detail
        );
    // The panel copy: the same primitive re-emitted from a fresh transform chain (its own ResetPoint/Translate/
    // Rotate/Scale prefix, matching the plate's — chaining after the plate's shape would inherit whatever persistent
    // Scale op its emission left), placed by ShapePanelDocument.Resolve and composed against the plate's own
    // accumulator. Caller-owned scope: this must run inside the PushField/PopField pair the plate's emission opened.
    // Inset/Depth are creation units, the same units the chain's shape-local ops read.
    private static void EmitPanelCopy(SdfProgramBuilder baseChain, ShapeDocument shape, Vector3 scale, int material, ShapePanelDocument panel) {
        var lift = (shape.Lift ?? SdfLift.Extrude);
        var placement = ShapePanelDocument.Resolve(
            depth: panel.Depth,
            faceAxis: ((panel.Face is { } face)
            ? face.Value
            : ShapePanelDocument.DefaultFace),
            inset: panel.Inset,
            lift: lift,
            scale: scale,
            type: shape.Type
        );

        _ = AppendPrimitive(
            chain: baseChain.Translate(offset: (placement.FaceAxis * placement.Offset)),
            shape: shape,
            scale: placement.ErodedScale,
            material: material,
            blend: placement.Blend
        );
    }
    // panelMaterial is the panel copy's already-resolved program material id (null only when the shape has no panel;
    // a panelled shape without one is a caller defect and throws). Render path only
    // (contactMargin null): the deterministic fixed-point contact evaluator (EmitFixed/VisitFixedPrimitiveCopies)
    // never calls this with a panelled shape's second material, so a solid placement's collider reads the plain
    // plate regardless of Panel.
    private static void EmitShapeChain(SdfProgramBuilder builder, ShapeDocument shape, in StampFrame frame, int material, float? contactMargin, bool inScope = false, int? panelMaterial = null) {
        var shapeScale = shape.Scale;
        var stampScale = frame.FacetScale;
        var stampFrame = frame;

        SdfProgramBuilder BuildTransformChain() {
            var prefix = ShapeDomainOps.Apply(
                chain: StampPrefix(
                    builder: builder,
                    frame: stampFrame
                ),
                domain: shape.Domain,
                subject: shape.Position
            )
                .Translate(offset: shape.Position)
                .Rotate(rotation: shape.Rotation);

            // Creation-unit lengths, like the rest of this chain past the Scale(stamp scale) op above — no
            // explicit placement-scale multiply needed here (unlike WorldCreationInstances.EmitShape's twist/bend/flare
            // prefix, which has no such chain-level Scale and so must bake placementScale in by hand).
            var flared = ((shape.Flare is { } flare)
                ? prefix.AxialProfile(
                    amount: flare.Amount,
                    bulge: flare.Bulge,
                    top: (flare.Top ?? 0f),
                    span: flare.Span,
                    axis: ((SdfAxis)flare.Axis),
                    startScale: flare.StartScale
                )
                : prefix
            );
            var sheared = ((shape.Shear is { } shear)
                ? flared.Shear(
                    linear: shear.Linear,
                    quadratic: shear.Quadratic,
                    cubic: shear.Cubic,
                    target: ((SdfAxis)shear.Target),
                    driver: ((SdfAxis)shear.Driver)
                )
                : flared
            );

            foreach (var bump in (shape.Bumps ?? [])) {
                sheared = sheared.GaussianPush(
                    center: bump.Center,
                    radii: bump.Radii,
                    push: bump.Push
                );
            }

            // The warp and the elongation read creation units like the rest of this chain (KEEP IN SYNC with
            // WorldCreationInstances.EmitShape, whose chain without a Scale op converts them by hand).
            if (shape.Warp is { } warp) {
                sheared = sheared.DomainWarp(amplitude: warp.Amplitude, frequency: warp.Frequency);
            }
            if (shape.Elongate is { } elongate) {
                sheared = sheared.Elongate(extents: elongate);
            }

            return sheared;
        }

        var chain = BuildTransformChain();
        // Per-shape lane-driven erosion (KEEP IN SYNC with WorldCreationInstances.EmitShape's mirrored prefix): the reach is
        // this shape's own SdfSolidGeometry.Reach, taken in WORLD units like the rest of this chain past the Scale
        // op above. Chained immediately before whichever AppendPrimitive call emits this shape's ShapeBlend —
        // ordinary point ops (twist/bend/flare/shear/bumps, already applied above) land on the point as usual.
        var erodeReach = ((shape.Erode is not null)
            ? (SdfSolidGeometry.Reach(
                lift: (shape.Lift ?? SdfLift.Extrude),
                scale: shapeScale,
                type: shape.Type
            ) * stampScale)
            : 0f
        );

        SdfProgramBuilder ApplyErode(SdfProgramBuilder target) =>
            ((shape.Erode is { } erode)
                ? target.LaneErode(
                    from: erode.From,
                    lane: erode.Lane,
                    noiseScale: (erode.Noise ?? 1f),
                    reach: erodeReach,
                    to: erode.To
                )
                : target
            );

        var blend = (shape.Blend ?? SdfBlendOp.Union);
        // The blend radius and the field ops below act on the running WORLD-space accumulator directly — never
        // re-multiplied by the chain's own Scale(stamp scale) op the way a primitive's baked-local rounding/
        // chamfer is (AppendPrimitive, above) — so they take the stamp scale explicitly here.
        var smooth = ((shape.Smooth ?? 0f) * stampScale);
        var dilate = ((shape.Dilate ?? 0f) * stampScale);
        var onion = ((shape.Onion ?? 0f) * stampScale);
        var wantsDilate = (dilate != 0f);
        var wantsOnion = (onion != 0f);

        if (
            (contactMargin is not { } margin) ||
            (margin == 0f)
        ) {
            var panel = shape.Panel;
            // Dilate/Onion need their own scope — unscoped, a field op would inflate or hollow every shape emitted
            // before it in the whole program, not just this one; inside a caller's shared scope the
            // caller's pop already isolates it. KEEP IN SYNC with the static stamper's per-shape probe reservation
            // (Client.WorldPlacementStamper.EmitProbe), which reserves the pair. A panel ALSO needs its own scope: its
            // subtraction/union must bite only this shape's own candidate, never a sibling composed before it —
            // validation refuses a panel wherever inScope would be true here (RequiresScope/Group), so this branch is
            // unreachable for a panelled shape.
            // A flare/shear/bump/erode/cells (each a warp, or a relief whose term folds the same way — see
            // SdfProgram.Lipschitz.cs's LaneErode case) needs its own scope for a different reason: unscoped, its
            // Lipschitz factor would fold into the whole program's step scale. A primitive never does — every one is
            // 1-Lipschitz, a non-uniformly scaled sphere or ellipsoid included (the exact exponent-2 superellipsoid
            // gauge), so no primitive opens a scope for its shape alone.
            // A pop-only blend (morph, stairs) composes at a scope's pop alone, so its shape opens its own scope even
            // inside the creation's.
            var ownScope = (
                (!inScope &&
                (wantsDilate || wantsOnion || (panel is not null) || (shape.Flare is not null) || (shape.Shear is not null) || (shape.Bumps is { Count: > 0 }) || (shape.Erode is not null) || (shape.Cells is not null) || (shape.Warp is not null) || (shape.Displace is not null))) ||
                ShapeScope.IsPopOnly(blend: blend)
            );

            if (ownScope) {
                var scoped = AppendPrimitive(
                    chain: ApplyErode(target: ShapeScope.Open(
                        blend: blend,
                        chain: chain,
                        morph: shape.Morph,
                        smooth: smooth,
                        steps: shape.Steps
                    )),
                    shape: shape,
                    scale: shapeScale,
                    material: material,
                    detail: (shape.Detail ?? false)
                );

                if (shape.Displace is not null) {
                    EmitDisplace(builder: builder, frame: in frame, shape: shape, stampScale: stampScale);
                }

                if (wantsDilate) {
                    scoped = scoped.Dilate(radius: dilate);
                }

                if (wantsOnion) {
                    scoped = scoped.Onion(thickness: onion);
                }

                if (panel is not null) {
                    // Never composed with this shape's own Dilate/Onion, which apply to the plate's own field only
                    // (ShapePanelDocument) — the panel copy rides its own fresh chain rather than `scoped`.
                    EmitPanelCopy(
                        baseChain: BuildTransformChain(),
                        material: (panelMaterial ?? throw new InvalidOperationException(message: "A panelled shape was emitted without its panel material resolved.")),
                        panel: panel,
                        scale: shapeScale,
                        shape: shape
                    );
                }

                if (shape.Cells is { } cells) {
                    // Relief samples the shape's rigid frame, independently of the primitive's residual
                    // scale or panel offset. Frequency and amplitude convert inversely, preserving its bound.
                    _ = ReliefChain(builder: builder, frame: in frame, shape: shape)
                        .CellDisplace(
                        (cells.Frequency / stampScale),
                        (cells.Amplitude * stampScale),
                        cells.Seed,
                        cells.Mode,
                        cells.Randomness
                    );
                }
                _ = chain.PopField();

                return;
            }

            var afterShape = AppendPrimitive(
                chain: ApplyErode(target: chain),
                shape: shape,
                scale: shapeScale,
                material: material,
                blend: blend,
                smooth: smooth,
                detail: (shape.Detail ?? false)
            );

            if (shape.Displace is not null) {
                EmitDisplace(builder: builder, frame: in frame, shape: shape, stampScale: stampScale);
            }

            if (wantsDilate) {
                afterShape = afterShape.Dilate(radius: dilate);
            }

            if (wantsOnion) {
                _ = afterShape.Onion(thickness: onion);
            }

            return;
        }

        EmitMarginedPrimitive(
            chain: ApplyErode(target: chain.PushField(
                compose: blend,
                smooth: smooth
            )),
            detail: (shape.Detail ?? false),
            margin: margin,
            material: material,
            shape: shape,
            shapeScale: shapeScale
        );
    }
    // A relief's own chain: the shape's rigid frame under the stamp, in world units, independently of the primitive's
    // residual scale or panel offset. A mirrored copy samples the relief through the stamp's reflection, like the shape
    // it rides.
    private static SdfProgramBuilder ReliefChain(SdfProgramBuilder builder, in StampFrame frame, ShapeDocument shape) {
        var origin = builder.ResetPoint()
            .Translate(offset: StampedPosition(
            frame: frame,
            local: MirroredShapePosition(
                frame: frame,
                shape: shape
            )
        ).ToVector3());

        return ((frame.ReflectionAlignment is { } alignment)
            ? Reflect(
                alignment: alignment,
                chain: origin.Rotate(rotation: frame.Rotation)
            ).Rotate(rotation: shape.Rotation)
            : origin.Rotate(rotation: (frame.Rotation * FixedQuaternion.FromQuaternion(value: shape.Rotation).Normalize()).Normalize()));
    }
    // The sine relief on the shape's rigid frame (KEEP IN SYNC with WorldCreationInstances.EmitShape's relief): frequency and
    // amplitude convert inversely with the stamp scale, like a cellular relief's.
    private static void EmitDisplace(SdfProgramBuilder builder, in StampFrame frame, ShapeDocument shape, float stampScale) {
        var displace = shape.Displace!;

        _ = ReliefChain(builder: builder, frame: in frame, shape: shape)
            .Displace(amplitude: (displace.Amplitude * stampScale), frequency: (displace.Frequency.Value / stampScale));
    }
    // A primitive dilated by its contact margin inside the field scope the caller just opened, so the dilation applies
    // before the authored blend; pops that scope.
    private static void EmitMarginedPrimitive(SdfProgramBuilder chain, ShapeDocument shape, Vector3 shapeScale, int material, float margin, bool detail) =>
        _ = AppendPrimitive(
            chain: chain,
            shape: shape,
            scale: shapeScale,
            material: material,
            detail: detail
        )
            .Dilate(radius: margin)
            .PopField();
    // The trim's own scope pops via Union into the SAME accumulator the host's own (unmodified) real shape already
    // folded into moments earlier in program order — so the scope's own candidate wins only where it undercuts that
    // already-present value. A genuine erosion (Dilate at a negative radius) can never do that: max(a, b) >= a, so
    // an eroded-then-intersected candidate is bounded below by "host eroded by Inset", which is STRICTLY WORSE
    // (more positive) than the host's own unmodified distance by exactly Inset — an erosion is provably always
    // beaten by the plain host and the trim would never render. A slight OUTWARD offset (Dilate at a POSITIVE
    // radius, Inset's magnitude) instead makes the host copy narrowly closer than the plain host — losing to it
    // everywhere by default (Inset is tiny, 0.003 by convention), but WINNING wherever the Intersection with the
    // reference's own dilated copy does not additionally worsen it, i.e. near the reference. Inset therefore reads
    // as "how far inside the reference's own influence the seam sits" rather than a literal inward erosion of the
    // host's geometry, which stays imperceptibly proud (its whole purpose is avoiding a coincident-surface z-fight,
    // not a visible step). Both copies ride a fresh ResetPoint/Translate/Rotate prefix rather than chaining off the
    // host's own shape instruction, whose emission may leave a persistent Scale op behind.
    private static void EmitTrim(SdfProgramBuilder builder, ShapeDocument shape, ShapeDocument reference, in StampFrame frame, ShapeTrimDocument trim, int material) {
        var hostChain = StampPrefix(
            builder: builder,
            frame: frame
        )
            .Translate(offset: shape.Position)
            .Rotate(rotation: shape.Rotation);

        var eroded = AppendPrimitive(
            chain: hostChain.PushField(compose: SdfBlendOp.Union),
            shape: shape,
            scale: shape.Scale,
            material: material
        ).Dilate(radius: (trim.Inset * frame.FacetScale));

        var referenceChain = StampPrefix(
            builder: eroded,
            frame: frame
        )
            .Translate(offset: reference.Position)
            .Rotate(rotation: reference.Rotation);

        _ = AppendPrimitive(
            chain: referenceChain,
            shape: reference,
            scale: ShapeTrimDocument.DilatedScale(
                scale: reference.Scale,
                width: trim.Width
            ),
            material: material,
            blend: SdfBlendOp.Intersection,
            curve: false
        );

        _ = builder.PopField();
    }
    // Render path only (mirrors EmitPanelCopy's own guard) — the deterministic fixed-point contact evaluator never
    // reads Trims, so a solid placement's collider is unchanged by them. Runs after EmitShapeChain has fully closed
    // the host's own emission (including any scope a panel/dilate/onion/warp opened), so each trim's own
    // PushField/PopField pair is sequential with that scope, never nested inside it. inScope true means the CALLER
    // already holds the whole-creation scope RequiresScope forces — ValidateTrims refuses Trims on that document, so
    // this is a defensive no-op, never reached by a validated document.
    private static void EmitTrims(SdfProgramBuilder builder, CreationDocument document, ShapeDocument shape, in StampFrame frame, Func<ShapeDocument, int> materialFor, float? contactMargin, bool inScope) {
        if (
            inScope ||
            ((contactMargin is { } margin) && (margin != 0f)) ||
            (shape.Trims is not { Count: > 0 } trims)
        ) {
            return;
        }

        var shapes = (document.Shapes ?? []);

        foreach (var trim in trims) {
            ShapeDocument? reference = null;

            foreach (var candidate in shapes) {
                if (string.Equals(
                    a: candidate.Name?.Value,
                    b: trim.Shape,
                    comparisonType: StringComparison.Ordinal
                )) {
                    reference = candidate;

                    break;
                }
            }

            if (reference is null) {
                continue; // Validation refuses an undeclared reference before this ever runs.
            }

            EmitTrim(
                builder: builder,
                frame: frame,
                material: materialFor(arg: shape with { Material = trim.Material }),
                reference: reference,
                shape: shape,
                trim: trim
            );
        }
    }
    // The point's mirror image across the plane through the origin with the given unit normal.
    private static FixedVector3 Reflect(FixedVector3 value, FixedVector3 unitNormal) {
        var projection = FixedVector3.Dot(
            left: value,
            right: unitNormal
        );

        return (value - (unitNormal * (projection + projection)));
    }
    // A mirrored stamp reflects the point once, across the plane through its origin with its normal, after the stamp's
    // own scale and before anything the creation authors: every domain op, shape pose, shape-local op and primitive of
    // the copy then reads the original's field at the mirrored point, so the copy is the original's true mirror image
    // whatever its shapes' symmetry. The reflection is H(n) = A H(x) A^-1 with A the shortest rotation carrying local X
    // onto the normal, spelled as that rotation, SdfProgramBuilder.MirrorX, and the rotation back. A mesh placement
    // applies the same H(n) in the same place: after the stamp's scale, before the stamp's rotation.
    private static SdfProgramBuilder StampPrefix(SdfProgramBuilder builder, in StampFrame frame) {
        var chain = builder.ResetPoint();

        if (frame.DynamicSlot is { } slot) { chain = chain.TransformDynamic(slot); }
        chain = chain
            .Translate(offset: frame.Origin.ToVector3())
            .Rotate(rotation: frame.Rotation)
            .Scale(scale: new Vector3(value: ((float)frame.Scale)));

        return ((frame.ReflectionAlignment is { } alignment)
            ? Reflect(
                alignment: alignment,
                chain: chain
            )
            : chain
        );
    }
    // Where a shape's origin lands in the stamp's scaled frame: its authored position, reflected on a mirrored stamp.
    private static FixedVector3 MirroredShapePosition(ShapeDocument shape, in StampFrame frame) {
        var position = FixedVector3.FromVector3(value: shape.Position);

        return ((frame.ReflectionNormal is { } normal)
            ? Reflect(
                unitNormal: normal,
                value: position
            )
            : position
        );
    }
    // A creation-local point carried into world space by the stamp: scaled, rotated and offset to the stamp origin.
    private static FixedVector3 StampedPosition(in StampFrame frame, FixedVector3 local) =>
        (frame.Origin + frame.Rotation.Rotate(vector: (local * frame.Scale)));
    private static SdfProgramBuilder Reflect(SdfProgramBuilder chain, FixedQuaternion alignment) => chain
        .Rotate(rotation: alignment)
        .MirrorX()
        .Rotate(rotation: alignment.Conjugate());
    // The shortest rotation carrying local X onto the plane's normal. The plane's two normals name one reflection, so
    // the one facing +X is taken and the rotor never exceeds a quarter turn. Fixed point, so a collider's program
    // does not depend on the platform's float arithmetic.
    private static FixedQuaternion ReflectionAlignment(FixedVector3 unitNormal) =>
        FixedQuaternion.FromTo(
            from: FixedVector3.UnitX,
            to: ((unitNormal.X < FixedQ4816.Zero)
            ? -unitNormal
            : unitNormal)
        );
    private static (FixedVector3 Position, FixedQuaternion Rotation) ReflectedShapeTransformFixed(ShapeDocument shape, FixedVector3? normal) {
        var position = FixedVector3.FromVector3(value: shape.Position);
        var rotation = FixedQuaternion.FromQuaternion(value: shape.Rotation).Normalize();

        if (normal is not { } unitNormal) {
            return (Position: position, Rotation: rotation);
        }

        // Let H(n) be reflection across the plane with normal n. Negating the reflected X axis turns the improper
        // reflected basis back into the proper frame the render emitter authors. If b = R*x, that frame is
        // H(n) R H(x) = H(n) H(b) R, and the product of the two reflections is SdfDomainExpansion.MirrorRemainder.
        var reflectedXAxis = rotation.Rotate(vector: FixedVector3.UnitX).Normalize();

        return (
            Position: Reflect(
            unitNormal: unitNormal,
            value: position
        ),
            Rotation: (SdfDomainExpansion.MirrorRemainder(
                axis: reflectedXAxis,
                unitNormal: unitNormal
            ) * rotation).Normalize()
        );
    }
    // A run's creation-space frame: authored directly, or a riding run's unit-local frame carried by its shape.
    private static (Vector3 Position, Quaternion Rotation) RunFrame(CreationDocument document, TextRunDocument run) {
        if (run.ShapeId is not { } shapeId) {
            return (run.Position, run.Rotation);
        }

        foreach (var shape in (document.Shapes ?? [])) {
            if (shape.Id != shapeId) {
                continue;
            }

            return (
                (shape.Position + Vector3.Transform(
                value: (run.Position * EffectiveScale(value: shape.Scale).ToVector3()),
                rotation: shape.Rotation
            )),
                Quaternion.Normalize(value: (shape.Rotation.Value * run.Rotation.Value))
            );
        }

        return (run.Position, run.Rotation);
    }
    // A run's document layout facets mapped to layout options: the wrap width scales with the stamp (it is authored
    // in creation units, and the layout works in world units); tracking (em) and line spacing (a multiplier) are
    // scale-free. Null when the run authors none, so an option-free run stays on the default layout path.
    private static TextLayoutOptions? RunLayoutOptions(TextRunDocument run, float scale) {
        if (
            (run.MaxWidth is null) &&
            (run.Align is null) &&
            (run.Tracking is null) &&
            (run.LineSpacing is null)
        ) {
            return null;
        }

        return new TextLayoutOptions(
            MaxLineWidth: (run.MaxWidth * scale),
            Alignment: (string.Equals(
                a: run.Align,
                b: TextRunDocument.AlignCenter,
                comparisonType: StringComparison.Ordinal
            )
            ? TextAlignment.Center
            : (string.Equals(
                    a: run.Align,
                    b: TextRunDocument.AlignRight,
                    comparisonType: StringComparison.Ordinal
                )
                ? TextAlignment.Right
                : TextAlignment.Left)),
            Tracking: (run.Tracking ?? 0f),
            LineHeightScale: (run.LineSpacing ?? 1f)
        );
    }

    /// <summary>Returns whether a creation's parts compose against each other — a shape blend other than Union, or an
    /// engraved text run. A stamp of such a creation must be emitted inside one field scope
    /// (<see cref="SdfProgramBuilder.PushField"/>/<see cref="SdfProgramBuilder.PopField"/>): its carves then bite only
    /// the creation's own field, and the result unions into the world, instead of biting whatever the program emitted
    /// before the stamp.</summary>
    /// <param name="document">The creation document.</param>
    public static bool ComposesInternally(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var shape in (document.Shapes ?? [])) {
            if ((shape.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union) {
                return true;
            }
        }

        foreach (var run in (document.TextRuns ?? [])) {
            if (string.Equals(
                a: run.Mode,
                b: TextRunDocument.ModeEngrave,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Emits a creation's shape list under one materialized stamp transform.</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <param name="materialFor">Resolves each shape's material id.</param>
    /// <param name="contactMargin">An optional per-shape signed contact margin. Null emits the raw render stream;
    /// a nonzero value scopes each primitive so dilation applies before its authored blend.</param>
    /// <param name="inScope">Whether the caller already holds an open field scope around this emission (the
    /// whole-creation stamp of <see cref="RequiresScope"/>). The creation emitter shares this scope: a shape whose field
    /// op or warp would otherwise open its own scope then rides the caller's instead.</param>
    /// <param name="hidden">The shapes, by index, that emit nothing (a presentation's shape isolation), or
    /// <see langword="null"/> to emit every shape.</param>
    public static void Emit(SdfProgramBuilder builder, CreationDocument document, CreationStampTransform transform, Func<ShapeDocument, int> materialFor, float? contactMargin = null, bool inScope = false, IReadOnlyList<bool>? hidden = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(materialFor);

        var frame = StampFrame.Of(transform: transform);
        var shapes = (document.Shapes ?? []);

        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];

            if ((hidden is not null) && hidden[index]) {
                continue;
            }

            EmitShapeChain(
                builder: builder,
                contactMargin: contactMargin,
                frame: frame,
                inScope: inScope,
                material: materialFor(arg: shape),
                panelMaterial: ((shape.Panel is { } panel)
                ? materialFor(arg: shape with { Material = panel.Material })
                : (int?)null),
                shape: shape
            );
            EmitTrims(
                builder: builder,
                contactMargin: contactMargin,
                document: document,
                frame: frame,
                inScope: inScope,
                materialFor: materialFor,
                shape: shape
            );
        }
    }
    /// <summary>Emits a creation's shape list under one materialized stamp transform, deriving every transform
    /// constant in deterministic fixed point before the SDF program's single-precision encoding boundary.</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The fixed-point stamp transform.</param>
    /// <param name="materialFor">Resolves each shape's material id.</param>
    /// <param name="contactMargin">An optional per-shape signed contact margin. Null emits the raw render stream;
    /// a nonzero value scopes each primitive so dilation applies before its authored blend.</param>
    /// <remarks>This is the collision-field sibling of <see cref="Emit"/>: the same stamp prefix and mirror alignment,
    /// with every domain fold expanded to its rigid copies and the render-only facets (panels, trims, warps, relief,
    /// text, detail shapes, sweeps) left out. A mirrored shape's orientation is composed from its two reflection
    /// planes as a fixed quaternion; it never visits <see cref="Matrix4x4"/>,
    /// <see cref="Quaternion.CreateFromRotationMatrix"/>, or a floating-point normalize.</remarks>
    public static void EmitFixed(SdfProgramBuilder builder, CreationDocument document, FixedCreationStampTransform transform, Func<ShapeDocument, int> materialFor, float? contactMargin = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(materialFor);

        var frame = StampFrame.Of(transform: transform);

        foreach (var shape in (document.Shapes ?? [])) {
            // A detail shape never reaches this contact compiler at all, so it neither carves
            // nor pads the collider it would otherwise emit.
            if (shape.Detail == true) {
                continue;
            }

            // A Sweep is not a closed solid a body can stand on (SdfSolidPrimitive.Sweep's remarks) — its field is
            // "exact enough" for rendering, not a sound collider, so it contributes no contact geometry, exactly
            // like a Detail shape.
            if (shape.Type == SdfSolidPrimitive.Sweep) {
                continue;
            }

            if (!ShapeDomainOps.TryExpand(
                domain: shape.Domain,
                subject: shape.Position,
                frames: out var frames,
                refusal: out var refusal
            )) {
                throw new ArgumentException(
                    message: $"A contact-emitted creation shape carries {refusal}, so its copies have no contact geometry.",
                    paramName: nameof(document)
                );
            }

            var (shapePosition, shapeRotation) = ReflectedShapeTransformFixed(
                normal: null,
                shape: shape
            );
            var local = new SdfRigidFrame(
                Mirrored: false,
                Position: shapePosition,
                Rotation: shapeRotation
            );
            var shapeScale = EffectiveScale(value: shape.Scale).ToVector3();
            var blend = (shape.Blend ?? SdfBlendOp.Union);
            var smooth = (shape.Smooth ?? 0f);

            foreach (var copy in frames) {
                var placed = copy.Compose(inner: local);
                var posed = StampPrefix(
                    builder: builder,
                    frame: frame
                )
                    .Translate(offset: placed.Position.ToVector3())
                    .Rotate(rotation: placed.Rotation.ToQuaternion());
                // A mirrored domain copy (a symmetry plane, a polar mirror) is the proper frame followed by H(x):
                // the frame's rotation alone would mirror only primitives symmetric in their local X.
                var chain = (placed.Mirrored
                    ? posed.MirrorX()
                    : posed
                );

                if (
                    (contactMargin is not { } margin) ||
                    (margin == 0f)
                ) {
                    _ = AppendPrimitive(
                        chain: chain,
                        shape: shape,
                        scale: shapeScale,
                        material: materialFor(arg: shape),
                        blend: blend,
                        smooth: smooth
                    );

                    continue;
                }

                EmitMarginedPrimitive(
                    chain: chain.PushField(
                        compose: blend,
                        smooth: smooth
                    ),
                    detail: false,
                    margin: margin,
                    material: materialFor(arg: shape),
                    shape: shape,
                    shapeScale: shapeScale
                );
            }
        }
    }
    /// <summary>Emits a creation's noise-relief facet against the field accumulated so far — one
    /// <see cref="SdfProgramBuilder.NoiseDisplace"/> sampled in the creation's own frame, so every stamped copy
    /// carries identical relief and the pattern rides the stamp's rotation and scale.</summary>
    /// <param name="builder">The target program builder, inside the stamp's open field scope.</param>
    /// <param name="noise">The creation's normalized noise facet.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <remarks>The caller owns the surrounding <see cref="SdfProgramBuilder.PushField"/>/<see cref="SdfProgramBuilder.PopField"/>
    /// pair — an unscoped field op would displace the whole composed world field. Amplitude is world units
    /// (un-scaled by the stamp); the sampling point rides the stamp's frame.</remarks>
    public static void EmitNoise(SdfProgramBuilder builder, CreationNoiseDocument noise, CreationStampTransform transform) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(noise);

        _ = StampPrefix(
            builder: builder,
            frame: StampFrame.Of(transform: transform)
        )
            .NoiseDisplace(
            amplitude: noise.Amplitude,
            frequency: noise.Frequency,
            gain: (noise.Gain ?? 0.5f),
            lacunarity: (noise.Lacunarity ?? 2f),
            octaves: (noise.Octaves ?? 4),
            seed: (noise.Seed ?? 0u)
        );
    }
    /// <summary>Emits ONE shape of a creation under the stamp transform — the per-shape-instance form of
    /// <see cref="Emit"/>, for a stamper that gives each shape its own tight cull bound
    /// (<see cref="ShapeStampBound"/>) instead of one whole-creation instance.</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="shapeIndex">The shape's index in <see cref="CreationDocument.Shapes"/>.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <param name="material">The shape's resolved program material id.</param>
    /// <param name="paletteIds">The creation's resolved palette (see
    /// <c>Client.WorldPlacementStamper.RegisterPalette</c>), indexed like the creation's own palette slots — the seam
    /// a panelled shape's second material resolves through. At least one entry.</param>
    public static void EmitShapeStamp(SdfProgramBuilder builder, CreationDocument document, int shapeIndex, CreationStampTransform transform, int material, int[] paletteIds) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(paletteIds);
        ArgumentOutOfRangeException.ThrowIfZero(
            value: paletteIds.Length,
            paramName: nameof(paletteIds)
        );

        var shape = document.Shapes![shapeIndex];
        var frame = StampFrame.Of(transform: transform);

        EmitShapeChain(
            builder: builder,
            contactMargin: null,
            frame: frame,
            material: material,
            panelMaterial: ((shape.Panel is { } panel)
            ? paletteIds[Math.Clamp(
                    value: panel.Material,
                    max: (paletteIds.Length - 1),
                    min: 0
                )]
            : (int?)null),
            shape: shape
        );
        if (shape.Trims is { Count: > 0 }) {
            EmitPaletteTrims(
                builder: builder,
                document: document,
                frame: frame,
                paletteIds: paletteIds,
                shape: shape
            );
        }
    }

    // A shape's trims against its creation's palette. The material delegate captures the palette, so it lives here, where
    // only a shape that carries trims pays for it, rather than in EmitShapeStamp, whose every call would.
    private static void EmitPaletteTrims(SdfProgramBuilder builder, CreationDocument document, ShapeDocument shape, in StampFrame frame, int[] paletteIds) =>
        EmitTrims(
            builder: builder,
            contactMargin: null,
            document: document,
            frame: frame,
            inScope: false,
            materialFor: candidate => paletteIds[Math.Clamp(
                value: (candidate.Material ?? 0),
                max: (paletteIds.Length - 1),
                min: 0
            )],
            shape: shape
        );

    /// <summary>Emits every authored text run under the same stamp transform as <see cref="Emit"/>.</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <param name="fontFor">Resolves a run's optional font name through the owning catalog.</param>
    /// <param name="materialFor">Resolves a run's material id.</param>
    /// <param name="textLayouts">Per-run layouts already computed by <see cref="LayoutTextRuns"/> for this same
    /// (<paramref name="document"/>, <c>transform.Scale</c>, <paramref name="fontFor"/>) — indexed like
    /// <see cref="CreationDocument.TextRuns"/>, reused instead of laying each run out again;
    /// <see langword="null"/> lays every run out fresh, exactly as before this parameter existed.</param>
    public static void EmitText(SdfProgramBuilder builder, CreationDocument document, CreationStampTransform transform, Func<string?, FontAtlas> fontFor, Func<TextRunDocument, int> materialFor, IReadOnlyList<TextLayoutResult>? textLayouts = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fontFor);
        ArgumentNullException.ThrowIfNull(materialFor);

        var frame = StampFrame.Of(transform: transform);
        var runs = (document.TextRuns ?? []);

        for (var index = 0; (index < runs.Count); index++) {
            var run = runs[index];

            var (authoredPosition, rotation) = RunFrame(
                document: document,
                run: run
            );
            // The run's frame rides the stamp like a shape's: reflected on a mirrored stamp, then scaled, rotated and
            // offset in fixed point, the same arithmetic the shapes' prefix encodes.
            var position = FixedVector3.FromVector3(value: authoredPosition);
            var localRight = FixedVector3.FromVector3(value: Vector3.Transform(
                value: Vector3.UnitX,
                rotation: rotation
            ));
            var localUp = FixedVector3.FromVector3(value: Vector3.Transform(
                value: Vector3.UnitY,
                rotation: rotation
            ));

            if (frame.ReflectionNormal is { } normal) {
                position = Reflect(
                    unitNormal: normal,
                    value: position
                );
                localRight = Reflect(
                    unitNormal: normal,
                    value: localRight
                );
                localUp = Reflect(
                    unitNormal: normal,
                    value: localUp
                );
            }

            _ = builder.Text(
                atlas: fontFor(arg: run.Font),
                text: run.Text,
                origin: StampedPosition(
                    frame: frame,
                    local: position
                ).ToVector3(),
                right: frame.Rotation.Rotate(vector: localRight).ToVector3(),
                up: frame.Rotation.Rotate(vector: localUp).ToVector3(),
                worldEmHeight: (run.EmHeight * transform.Scale),
                material: materialFor(arg: run),
                blend: (string.Equals(
                    a: run.Mode,
                    b: TextRunDocument.ModeEngrave,
                    comparisonType: StringComparison.Ordinal
                )
                ? SdfBlendOp.Subtraction
                : SdfBlendOp.Union),
                extrudeHalfDepth: ((run.Depth ?? 0.02f) * transform.Scale),
                layout: RunLayoutOptions(
                    run: run,
                    scale: transform.Scale
                ),
                precomputedLayout: textLayouts?[index]
            );
        }
    }
    /// <summary>Emits every authored text run against a dynamic-transform slot, so the whole block rides the slot's
    /// per-frame pose — the replay-pool sibling of <see cref="EmitText"/>. Run positions and the wrap width are laid
    /// out in the slot's local frame, scaled by <paramref name="scale"/> exactly as the static path scales by its
    /// stamp transform; reflection is not represented (the pool has no mirror facet).</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="dynamicSlot">The dynamic-transform slot every glyph chain rides (the registration's root).</param>
    /// <param name="scale">The uniform placement scale baked into the local layout.</param>
    /// <param name="fontFor">Resolves a run's optional font name through the owning catalog.</param>
    /// <param name="materialFor">Resolves a run's material id.</param>
    /// <param name="textLayouts">Per-run layouts already computed by <see cref="LayoutTextRuns"/> for this same
    /// (<paramref name="document"/>, <paramref name="scale"/>, <paramref name="fontFor"/>) — indexed like
    /// <see cref="CreationDocument.TextRuns"/>, reused instead of laying each run out again;
    /// <see langword="null"/> lays every run out fresh, exactly as before this parameter existed.</param>
    public static void EmitTextDynamic(SdfProgramBuilder builder, CreationDocument document, int dynamicSlot, float scale, Func<string?, FontAtlas> fontFor, Func<TextRunDocument, int> materialFor, IReadOnlyList<TextLayoutResult>? textLayouts = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fontFor);
        ArgumentNullException.ThrowIfNull(materialFor);

        var runs = (document.TextRuns ?? []);

        for (var index = 0; (index < runs.Count); index++) {
            var run = runs[index];

            var (position, rotation) = RunFrame(
                document: document,
                run: run
            );

            _ = builder.Text(
                atlas: fontFor(arg: run.Font),
                text: run.Text,
                origin: (position * scale),
                right: Vector3.Transform(
                    value: Vector3.UnitX,
                    rotation: rotation
                ),
                up: Vector3.Transform(
                    value: Vector3.UnitY,
                    rotation: rotation
                ),
                worldEmHeight: (run.EmHeight * scale),
                material: materialFor(arg: run),
                blend: (string.Equals(
                    a: run.Mode,
                    b: TextRunDocument.ModeEngrave,
                    comparisonType: StringComparison.Ordinal
                )
                ? SdfBlendOp.Subtraction
                : SdfBlendOp.Union),
                extrudeHalfDepth: ((run.Depth ?? 0.02f) * scale),
                layout: RunLayoutOptions(
                    run: run,
                    scale: scale
                ),
                dynamicSlot: dynamicSlot,
                precomputedLayout: textLayouts?[index]
            );
        }
    }
    /// <summary>Whether a shape's effective per-axis scale is isotropic after the builder's magnitude and nonzero
    /// normalization.</summary>
    /// <param name="shape">The authored shape.</param>
    public static bool IsIsotropicallyScaled(ShapeDocument shape) {
        ArgumentNullException.ThrowIfNull(shape);

        var scale = EffectiveScale(value: shape.Scale);

        return (
            (scale.X == scale.Y) &&
            (scale.Y == scale.Z)
        );
    }
    /// <summary>Measures one shape's worst-case reach from the creation's origin, in creation units: its position,
    /// its primitive's reach grown by its cells padding, warps (flare, shear, bumps), domain fold, dilation and
    /// onion. The one per-shape reach every creation bound is built from (<see cref="RenderReach"/>,
    /// <see cref="CreationGeometry.Reach"/>); a Sweep carries no <see cref="SdfSolidGeometry.Reach(SdfSolidPrimitive, Vector3, SdfLift)"/>
    /// unit-scale law (its own control points already carry creation-unit dimensions — see
    /// <see cref="SdfSolidPrimitive.Sweep"/>'s remarks), so its own uniform scale bakes on directly.</summary>
    /// <param name="shape">The authored shape.</param>
    /// <returns>The reach in creation units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static float ShapeReach(ShapeDocument shape) {
        ArgumentNullException.ThrowIfNull(shape);

        var primitiveReach = ((shape.Type == SdfSolidPrimitive.Sweep)
            ? ((shape.Curve?.Reach() ?? 0f) * shape.Scale.X)
            : SdfSolidGeometry.Reach(
                type: shape.Type,
                scale: shape.Scale,
                lift: (shape.Lift ?? SdfLift.Extrude)
            )
        );
        var warpedReach = ShapeWarpReach.Expand(
            (primitiveReach + (shape.Cells?.PrimitiveReachPadding(
                shape.Scale,
                shape.Flare
            ) ?? 0f)),
            shape.Flare,
            shape.Shear,
            ShapeWarpReach.PointReach(shape: shape)
        );

        // A domain fold carries the shape across its lattice, so the fold's displacement bound is charged beside the
        // shape's own position; Dilate/Onion move the outer surface outward by their own value.
        return ((((shape.Position.Length() + warpedReach) + ShapeDomainOps.Reach(domain: shape.Domain)) + (shape.Dilate ?? 0f)) + (shape.Onion ?? 0f));
    }
    /// <summary>Lays out every one of a creation's authored text runs once, in document order — the same input
    /// <see cref="RenderReach"/> and <see cref="EmitText"/>/<see cref="EmitTextDynamic"/> each independently derive
    /// from (<paramref name="document"/>, <paramref name="scale"/>, <paramref name="fontFor"/>) per run. A caller
    /// measuring reach and then emitting the same runs passes the returned array to both through their
    /// <c>textLayouts</c> parameter, so <see cref="TextLayout.Layout(FontAtlas, string, TextLayoutOptions, float)"/>
    /// — the per-glyph kerning/wrap/align walk — runs once per run per call instead of twice, or once per
    /// pattern/scatter instance for a repeated placement.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="scale">The uniform stamp scale.</param>
    /// <param name="fontFor">Resolves a run's optional font name through the owning catalog.</param>
    /// <returns>One <see cref="TextLayoutResult"/> per entry of <see cref="CreationDocument.TextRuns"/>, in the same
    /// order; empty when the document authors no text runs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="fontFor"/> is
    /// <see langword="null"/>.</exception>
    public static TextLayoutResult[] LayoutTextRuns(CreationDocument document, float scale, Func<string?, FontAtlas> fontFor) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fontFor);

        var runs = (document.TextRuns ?? []);

        if (runs.Count == 0) {
            return [];
        }

        var results = new TextLayoutResult[runs.Count];
        var layout = new TextLayout();

        for (var index = 0; (index < runs.Count); index++) {
            var run = runs[index];

            results[index] = layout.Layout(
                atlas: fontFor(arg: run.Font),
                options: (RunLayoutOptions(
                    run: run,
                    scale: scale
                ) ?? TextLayoutOptions.Default),
                scale: (run.EmHeight * scale),
                text: run.Text
            );
        }

        return results;
    }
    /// <summary>The per-shape probe instances one static stamp of the creation reserves: its
    /// <see cref="CreationDocument.StampShapeCount"/> when the stamp is scope-free and text-free
    /// (<see cref="RequiresScope"/>) — one per shape, two for a panelled shape, whose one live instance carries a
    /// second full transform chain and shape for the panel copy and so needs two probe chains' words — else one for
    /// the whole creation. A live copy emits at most this many instances. KEEP IN SYNC with the static stamper's
    /// emission split and per-shape probe chain (<c>Client.WorldPlacementStamper</c>).</summary>
    /// <param name="document">The creation document.</param>
    /// <returns>The per-copy reservation count (at least 1).</returns>
    public static int PerCopyInstanceCount(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (
            RequiresScope(document: document) ||
            (document.TextRuns is { Count: > 0 })
        ) {
            return 1;
        }

        return Math.Max(
            val1: document.StampShapeCount(),
            val2: 1
        );
    }
    /// <summary>Measures the render-time bounding-sphere radius of a creation after resolving its authored fonts and
    /// layout options. Shape geometry uses <see cref="SdfSolidGeometry.Reach(SdfSolidPrimitive, Vector3, SdfLift)"/>; text geometry
    /// is measured from the exact laid-out glyph cells the SDF builder emits, including whitespace advance, wrapping,
    /// alignment, tracking, line spacing, atlas padding, and extrusion, each run at the frame it is emitted in (on its
    /// shape, for a run anchored to one), as <see cref="TextReach"/> measures it.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="scale">The uniform stamp scale.</param>
    /// <param name="fontFor">Resolves a run's optional font name through the owning catalog, or
    /// <see langword="null"/> when this render path omits text.</param>
    /// <param name="textLayouts">Per-run layouts already computed by <see cref="LayoutTextRuns"/> for this same
    /// (<paramref name="document"/>, <paramref name="scale"/>, <paramref name="fontFor"/>) — indexed like
    /// <see cref="CreationDocument.TextRuns"/>, reused instead of laying each run out again;
    /// <see langword="null"/> lays every run out fresh, exactly as before this parameter existed.</param>
    /// <param name="composeBlends">Whether the shapes' bounds fold through their blends
    /// (<see cref="SdfBoundAlgebra.Compose"/>: an intersection takes the smaller bound, so an unbounded shape clipped by
    /// a finite one is finite; a subtraction keeps its subject's) instead of taking the largest. Only an instance
    /// holding the whole creation, as one scope, may compose: an instance holding a subset of the shapes has not
    /// seen the blends that bound them.</param>
    /// <returns>The radius, in the builder's current coordinate space, or <see cref="SdfBoundAlgebra.Unbounded"/> when
    /// the creation's composed bound has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not finite and greater than zero.</exception>
    public static float RenderReach(CreationDocument document, float scale, Func<string?, FontAtlas>? fontFor, IReadOnlyList<TextLayoutResult>? textLayouts = null, bool composeBlends = false) {
        ArgumentNullException.ThrowIfNull(document);

        if (
            !float.IsFinite(f: scale) ||
            (scale <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(scale),
                message: "A creation render scale must be finite and greater than zero."
            );
        }

        // Noise relief grows the composed surface outward by up to its amplitude (world units, un-scaled), so the
        // bound charges it once up front.
        var reach = (document.Noise?.Amplitude ?? 0f);
        var any = false;
        var shapes = (document.Shapes ?? []);

        for (var shapeIndex = 0; (shapeIndex < shapes.Count); shapeIndex++) {
            var shape = shapes[shapeIndex];
            // The shape's creation-unit reach, scaled with the stamp; the noise amplitude is world units, un-scaled.
            var shapeReach = ((ShapeReach(shape: shape) * scale) + (document.Noise?.Amplitude ?? 0f));

            reach = ((composeBlends && any)
                ? SdfBoundAlgebra.Compose(
                    accumulated: reach,
                    blend: (shape.Blend ?? SdfBlendOp.Union),
                    operand: shapeReach
                )
                : MathF.Max(
                    x: reach,
                    y: shapeReach
                ));
            any = true;
        }

        if (fontFor is null) {
            return (any
                ? reach
                : (0.6f * scale)
            );
        }

        var runs = (document.TextRuns ?? []);

        for (var index = 0; (index < runs.Count); index++) {
            var run = runs[index];

            if (RunGlyphReach(fontFor: fontFor, index: index, run: run, scale: scale, textLayouts: textLayouts) is { } runReach) {
                reach = MathF.Max(
                    x: reach,
                    y: ((RunFrame(document: document, run: run).Position * scale).Length() + runReach)
                );
                any = true;
            }
        }

        return (any
            ? reach
            : (0.6f * scale)
        );
    }
    /// <summary>Measures how far a creation's text runs' glyphs reach from the creation's origin at a uniform stamp scale,
    /// each run at the frame it is emitted in (on its shape, for a run anchored to one): the radius a bound about that
    /// origin holding only the text needs, whatever the creation's orientation.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="scale">The uniform stamp scale.</param>
    /// <param name="fontFor">Resolves a run's optional font name through the owning catalog.</param>
    /// <param name="textLayouts">Per-run layouts already computed by <see cref="LayoutTextRuns"/> for this same
    /// (<paramref name="document"/>, <paramref name="scale"/>, <paramref name="fontFor"/>), indexed like
    /// <see cref="CreationDocument.TextRuns"/>; <see langword="null"/> lays every run out fresh.</param>
    /// <returns>The radius, in world units at <paramref name="scale"/>; zero when no run lays out a glyph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="fontFor"/> is
    /// <see langword="null"/>.</exception>
    public static float TextReach(CreationDocument document, float scale, Func<string?, FontAtlas> fontFor, IReadOnlyList<TextLayoutResult>? textLayouts = null) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fontFor);

        var reach = 0f;
        var runs = (document.TextRuns ?? []);

        for (var index = 0; (index < runs.Count); index++) {
            var run = runs[index];

            if (RunGlyphReach(fontFor: fontFor, index: index, run: run, scale: scale, textLayouts: textLayouts) is { } runReach) {
                reach = MathF.Max(
                    x: reach,
                    y: ((RunFrame(document: document, run: run).Position * scale).Length() + runReach)
                );
            }
        }

        return reach;
    }

    // How far a run's glyphs, extruded by its relief, reach from the run's origin in world units at the stamp's scale, or
    // null when the run lays out no glyph.
    private static float? RunGlyphReach(TextRunDocument run, int index, float scale, Func<string?, FontAtlas> fontFor, IReadOnlyList<TextLayoutResult>? textLayouts) {
        var atlas = fontFor(arg: run.Font);
        var emHeight = (run.EmHeight * scale);
        var layout = ((textLayouts is not null)
            ? textLayouts[index]
            : new TextLayout().Layout(
                atlas: atlas,
                options: (RunLayoutOptions(
                    run: run,
                    scale: scale
                ) ?? TextLayoutOptions.Default),
                scale: emHeight,
                text: run.Text
            )
        );
        var worldPerTexel = (emHeight / atlas.Size);
        var depth = (MathF.Abs(x: (run.Depth ?? 0.02f)) * scale);
        var runReach = 0f;

        foreach (var placement in layout.Placements) {
            var atlasBounds = placement.AtlasBounds;
            var planeBounds = placement.PlaneBounds;
            var halfWidth = ((0.5f * MathF.Abs(x: (atlasBounds.Right - atlasBounds.Left))) * worldPerTexel);
            var halfHeight = ((0.5f * MathF.Abs(x: (atlasBounds.Bottom - atlasBounds.Top))) * worldPerTexel);
            var centerX = (0.5f * (planeBounds.Left + planeBounds.Right));
            var centerY = (0.5f * (planeBounds.Bottom + planeBounds.Top));
            var farX = (MathF.Abs(x: centerX) + halfWidth);
            var farY = (MathF.Abs(x: centerY) + halfHeight);
            var glyphReach = MathF.Sqrt(x: (((farX * farX) + (farY * farY)) + (depth * depth)));

            runReach = MathF.Max(
                x: runReach,
                y: glyphReach
            );
        }

        return ((layout.Placements.Count > 0) ? runReach : null);
    }

    /// <summary>Returns whether a creation's stamp needs its own field scope: a blend outside the union family
    /// (whose accumulator compose is order-local — see the accumulator rule), an engraving text run, or a noise
    /// facet. A scope-free creation's shapes may each ride their own tight instance
    /// (<see cref="EmitShapeStamp"/>) — a masked-out union-family member is bit-identical to skipping it, and
    /// <c>SdfProgram.PackInstances</c> inflates each bound by its smooth radius.</summary>
    /// <param name="document">The creation document.</param>
    /// <returns><see langword="true"/> when the stamp must emit as one scoped instance.</returns>
    public static bool RequiresScope(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Noise is not null) {
            return true;
        }

        // Indexed rather than foreach over the lists' interface, which would box an enumerator on every stamp.
        var shapes = (document.Shapes ?? []);

        for (var index = 0; (index < shapes.Count); index++) {
            var blend = (shapes[index].Blend ?? SdfBlendOp.Union);

            if (blend is not (SdfBlendOp.Union or SdfBlendOp.SmoothUnion)) {
                return true;
            }
        }

        var runs = (document.TextRuns ?? []);

        for (var index = 0; (index < runs.Count); index++) {
            if (string.Equals(
                a: runs[index].Mode,
                b: TextRunDocument.ModeEngrave,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Measures one shape's world-space cull bound under the stamp transform: its reflected, stamped
    /// position and its primitive reach — the tight per-shape sibling of <see cref="RenderReach"/>. Only sound for a
    /// DOMAIN-FREE shape (a fold's copies leave any shape-local sphere); callers keep domain-bearing shapes on the
    /// whole-creation bound. A raised panel (<see cref="ShapePanelDocument.Depth"/> negative) widens the bound by
    /// <c>|Depth|</c>; a recess needs no widening (subtraction only removes). <see cref="ShapeDocument.Dilate"/> and
    /// <see cref="ShapeDocument.Onion"/> move the outer surface outward by their own creation-unit value, scaled the
    /// same way; the smooth-blend halo is the packer's own concern (<see cref="RequiresScope"/>).</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="shapeIndex">The shape's index in <see cref="CreationDocument.Shapes"/>.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <returns>The bound center (world space) and radius.</returns>
    public static (Vector3 Center, float Radius) ShapeStampBound(CreationDocument document, int shapeIndex, CreationStampTransform transform) {
        ArgumentNullException.ThrowIfNull(document);

        var shape = document.Shapes![shapeIndex];
        var frame = StampFrame.Of(transform: transform);
        var panelRaise = ((shape.Panel is { Depth: < 0f } panel)
            ? -panel.Depth
            : 0f
        );

        // Inverse warp bounds compose in reverse point-evaluation order (bumps, shear, profile) — the position term is untouched since every warp
        // applies after the shape's own translate. A Sweep carries no SdfSolidGeometry.Reach unit-scale law (see
        // RenderReach's identical Sweep branch); a panel is refused on it, so panelRaise never applies.
        var primitiveReach = ((shape.Type == SdfSolidPrimitive.Sweep)
            ? ((shape.Curve?.Reach() ?? 0f) * shape.Scale.X)
            : SdfSolidGeometry.Reach(
                type: shape.Type,
                scale: shape.Scale,
                lift: (shape.Lift ?? SdfLift.Extrude),
                panelRaise: panelRaise
            )
        );
        var warpedReach = ShapeWarpReach.Expand(
            (primitiveReach + (shape.Cells?.PrimitiveReachPadding(
                shape.Scale,
                shape.Flare
            ) ?? 0f)),
            shape.Flare,
            shape.Shear,
            ShapeWarpReach.PointReach(shape: shape)
        );

        return (
            StampedPosition(
                frame: frame,
                local: MirroredShapePosition(
                    frame: frame,
                    shape: shape
                )
            ).ToVector3(),
            (((warpedReach + (shape.Dilate ?? 0f)) + (shape.Onion ?? 0f)) * transform.Scale)
        );
    }
    /// <summary>Visits every primitive represented by one materialized stamp transform, computed entirely in fixed
    /// point.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <param name="visitor">Receives world-axis bounds for each primitive.</param>
    /// <remarks>
    /// <para>Every value here reaches SIMULATION STATE — a collider position decides where a body stops — so the whole
    /// body is integer arithmetic. Authored floats enter through <see cref="FixedVector3.FromVector3"/> and
    /// <see cref="FixedQuaternion.FromQuaternion"/>, the one door each, so the rounding is not a per-caller decision.</para>
    /// <para>A shape carrying domain ops visits once per expanded copy (<see cref="SdfDomainExpansion"/>); a fold has
    /// no meaning to a consumer that places geometry rather than transforming a point. A chain with no rigid-copy
    /// expansion throws, naming the op — the world validator refuses such a document before this runs.</para>
    /// </remarks>
    public static void VisitFixedPrimitiveCopies(CreationDocument document, FixedCreationStampTransform transform, Action<FixedCreationStampPrimitiveCopy> visitor) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(visitor);

        var stampFrame = StampFrame.Of(transform: transform);
        var stampRotation = stampFrame.Rotation;
        var stampScale = stampFrame.Scale;
        var reflectionNormal = stampFrame.ReflectionNormal;

        ReadOnlySpan<SdfRigidFrame> identity = [SdfRigidFrame.Identity];
        var shapes = (document.Shapes ?? []);

        for (var shapeIndex = 0; (shapeIndex < shapes.Count); shapeIndex++) {
            var shape = shapes[shapeIndex];
            // A detail shape never reaches this contact compiler at all, so it neither carves
            // nor pads the collider it would otherwise emit.
            if (shape.Detail == true) {
                continue;
            }

            // A Sweep is not a closed solid a body can stand on (SdfSolidPrimitive.Sweep's remarks) — its field is
            // "exact enough" for rendering, not a sound collider, so it contributes no contact geometry, exactly
            // like a Detail shape.
            if (shape.Type == SdfSolidPrimitive.Sweep) {
                continue;
            }

            var frames = identity;
            var expanded = Array.Empty<SdfRigidFrame>();

            if ((shape.Domain is { Count: > 0 }) && !ShapeDomainOps.TryExpand(
                domain: shape.Domain,
                subject: shape.Position,
                frames: out expanded,
                refusal: out var refusal
            )) {
                throw new ArgumentException(
                    message: $"A contact-emitted creation shape carries {refusal}, so its copies have no contact geometry.",
                    paramName: nameof(document)
                );
            }
            if (shape.Domain is { Count: > 0 }) { frames = expanded; }

            var bounds = SdfSolidGeometry.GetLocalBounds(type: shape.Type);
            var boundsCenter = FixedVector3.FromVector3(value: bounds.Center);
            var boundsHalfExtents = FixedVector3.FromVector3(value: bounds.HalfExtents);
            var shapeScale = EffectiveScale(value: shape.Scale);

            var (shapePosition, shapeRotation) = ReflectedShapeTransformFixed(
                normal: reflectionNormal,
                shape: shape
            );
            var local = new SdfRigidFrame(
                Mirrored: (reflectionNormal is not null),
                Position: shapePosition,
                Rotation: shapeRotation
            );
            var uniformScale = (IsIsotropicallyScaled(shape: shape)
                ? (stampScale * shapeScale.X)
                : FixedQ4816.Zero
            );

            foreach (var frame in frames) {
                var placed = frame.Compose(inner: local);
                // A rotation applied to a vector IS the scaled sum of its transformed unit axes, so the primitive's
                // world-axis extent falls out of the three axis images without ever forming a matrix.
                var shapeAxisX = placed.Rotation.Rotate(vector: FixedVector3.UnitX);
                var shapeAxisY = placed.Rotation.Rotate(vector: FixedVector3.UnitY);
                var shapeAxisZ = placed.Rotation.Rotate(vector: FixedVector3.UnitZ);
                var localBoundsCenter = (placed.Position + (
                    ((shapeAxisX * (boundsCenter.X * shapeScale.X))
                    + (shapeAxisY * (boundsCenter.Y * shapeScale.Y)))
                    + (shapeAxisZ * (boundsCenter.Z * shapeScale.Z))
                ));
                var axisX = stampRotation.Rotate(vector: ((shapeAxisX * shapeScale.X) * stampScale));
                var axisY = stampRotation.Rotate(vector: ((shapeAxisY * shapeScale.Y) * stampScale));
                var axisZ = stampRotation.Rotate(vector: ((shapeAxisZ * shapeScale.Z) * stampScale));

                visitor(obj: new FixedCreationStampPrimitiveCopy(
                    Center: (transform.Origin + stampRotation.Rotate(vector: (localBoundsCenter * stampScale))),
                    HalfExtents: new FixedVector3(
                        X: (((FixedQ4816.Abs(value: axisX.X) * boundsHalfExtents.X) + (FixedQ4816.Abs(value: axisY.X) * boundsHalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.X) * boundsHalfExtents.Z)),
                        Y: (((FixedQ4816.Abs(value: axisX.Y) * boundsHalfExtents.X) + (FixedQ4816.Abs(value: axisY.Y) * boundsHalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Y) * boundsHalfExtents.Z)),
                        Z: (((FixedQ4816.Abs(value: axisX.Z) * boundsHalfExtents.X) + (FixedQ4816.Abs(value: axisY.Z) * boundsHalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Z) * boundsHalfExtents.Z))
                    ),
                    LocalHalfExtents: new FixedVector3(
                        X: ((boundsHalfExtents.X * shapeScale.X) * stampScale),
                        Y: ((boundsHalfExtents.Y * shapeScale.Y) * stampScale),
                        Z: ((boundsHalfExtents.Z * shapeScale.Z) * stampScale)
                    ),
                    PlaneNormal: (bounds.IsUnbounded
                    ? axisY.Normalize()
                    : FixedVector3.Zero),
                    Rotation: (stampRotation * placed.Rotation).Normalize(),
                    Shape: shape,
                    UniformScale: uniformScale
                ));
            }
        }
    }
}
/// <summary>Receives each materialized instance of a lattice walk as a struct, so a walk
/// (<see cref="CreationStampLattice.ForEachInstance{TVisitor}"/>) allocates no delegate and no closure.</summary>
public interface ICreationStampVisitor {
    /// <summary>Receives one materialized instance.</summary>
    /// <param name="instance">The instance.</param>
    void Visit(CreationStampInstance instance);
}
/// <summary>Receives each materialized instance of a fixed-point lattice walk as a struct, so a walk
/// (<see cref="CreationStampLattice.ForEachFixedInstance{TVisitor}"/>) allocates no delegate and no closure.</summary>
public interface IFixedCreationStampVisitor {
    /// <summary>Receives one materialized instance.</summary>
    /// <param name="instance">The instance.</param>
    void Visit(FixedCreationStampInstance instance);
}
/// <summary>Materializes the same placement pattern and reflected copies consumed by creation stamp emission: one
/// fixed-point walk, which the render path reads through <see cref="ForEachInstance"/> as single-precision
/// conversions of the same instances.</summary>
public static class CreationStampLattice {
    /// <summary>Visits pattern copies in A-major, then B-major order, followed immediately by each reflected copy.
    /// When <paramref name="sampledOffsets"/> is supplied (a resolved <see cref="CreationStampSampling"/>
    /// Noise/Scatter offset set), those offsets replace the regular pattern grid entirely — <paramref name="pattern"/>
    /// is ignored — while mirroring composes identically either way.</summary>
    /// <param name="origin">The placement origin.</param>
    /// <param name="rotation">The placement rotation; a unit quaternion.</param>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    /// <param name="sampledOffsets">Precomputed placement-local offsets from a hash-sampled Noise/Scatter region, or
    /// <see langword="null"/> to use <paramref name="pattern"/>.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="visitor">Receives each materialized instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="visitor"/> is <see langword="null"/>.</exception>
    public static void ForEachFixedInstance(FixedVector3 origin, FixedQuaternion rotation, CreationStampPattern? pattern, IReadOnlyList<FixedVector3>? sampledOffsets, CreationStampPlane? mirror, Action<FixedCreationStampInstance> visitor) {
        ArgumentNullException.ThrowIfNull(visitor);

        var adapter = new FixedActionVisitor(action: visitor);

        ForEachFixedInstance(
            mirror: mirror,
            origin: origin,
            pattern: pattern,
            rotation: rotation,
            sampledOffsets: sampledOffsets,
            visitor: ref adapter
        );
    }
    /// <summary>Visits pattern copies in A-major, then B-major order, followed immediately by each reflected copy,
    /// through a struct visitor, so the walk allocates nothing. When <paramref name="sampledOffsets"/> is supplied (a
    /// resolved <see cref="CreationStampSampling"/> Noise/Scatter offset set), those offsets replace the regular
    /// pattern grid entirely — <paramref name="pattern"/> is ignored — while mirroring composes identically either
    /// way.</summary>
    /// <typeparam name="TVisitor">The visitor's type.</typeparam>
    /// <param name="origin">The placement origin.</param>
    /// <param name="rotation">The placement rotation; a unit quaternion.</param>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    /// <param name="sampledOffsets">Precomputed placement-local offsets from a hash-sampled Noise/Scatter region, or
    /// <see langword="null"/> to use <paramref name="pattern"/>.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="visitor">Receives each materialized instance.</param>
    /// <remarks>The pattern and mirror are AUTHORED single-precision records, so they enter the contract through
    /// <see cref="FixedVector3.FromVector3"/> here rather than at each caller. The step accumulation is a scaled
    /// index rather than a running sum, so copy <c>n</c> does not inherit <c>n−1</c> roundings.</remarks>
    public static void ForEachFixedInstance<TVisitor>(FixedVector3 origin, FixedQuaternion rotation, CreationStampPattern? pattern, IReadOnlyList<FixedVector3>? sampledOffsets, CreationStampPlane? mirror, ref TVisitor visitor) where TVisitor : struct, IFixedCreationStampVisitor, allows ref struct {
        var planeNormal = ((mirror is { } authoredPlane)
            ? FixedVector3.FromVector3(value: authoredPlane.Normal).Normalize()
            : (FixedVector3?)null
        );
        var planeOffset = FixedQ4816.FromDouble(value: (mirror?.Offset ?? 0f));

        if (sampledOffsets is { Count: > 0 } offsets) {
            for (var index = 0; (index < offsets.Count); index++) {
                VisitLocal(
                    local: offsets[index],
                    origin: origin,
                    planeNormal: planeNormal,
                    planeOffset: planeOffset,
                    rotation: rotation,
                    visitor: ref visitor
                );
            }
        } else {
            var countA = Math.Max(
                val1: (pattern?.CountA ?? 1),
                val2: 1
            );
            var countB = Math.Max(
                val1: (pattern?.CountB ?? 1),
                val2: 1
            );
            var stepA = FixedVector3.FromVector3(value: (pattern?.StepA ?? Vector3.Zero));
            var stepB = FixedVector3.FromVector3(value: (pattern?.StepB ?? Vector3.Zero));

            for (var indexA = 0; (indexA < countA); indexA++) {
                for (var indexB = 0; (indexB < countB); indexB++) {
                    VisitLocal(
                        local: ((stepA * FixedQ4816.FromInteger(value: indexA)) + (stepB * FixedQ4816.FromInteger(value: indexB))),
                        origin: origin,
                        planeNormal: planeNormal,
                        planeOffset: planeOffset,
                        rotation: rotation,
                        visitor: ref visitor
                    );
                }
            }
        }

        static void VisitLocal(ref TVisitor visitor, FixedVector3 origin, FixedQuaternion rotation, FixedVector3? planeNormal, FixedQ4816 planeOffset, FixedVector3 local) {
            Visit(
                local: local,
                origin: origin,
                reflectionNormal: null,
                rotation: rotation,
                visitor: ref visitor
            );

            if (planeNormal is { } reflectionNormal) {
                var signedDistance = (FixedVector3.Dot(
                    left: local,
                    right: reflectionNormal
                ) - planeOffset);

                Visit(
                    local: (local - (reflectionNormal * (signedDistance + signedDistance))),
                    origin: origin,
                    reflectionNormal: reflectionNormal,
                    rotation: rotation,
                    visitor: ref visitor
                );
            }
        }
        static void Visit(ref TVisitor visitor, FixedVector3 origin, FixedQuaternion rotation, FixedVector3 local, FixedVector3? reflectionNormal) {
            visitor.Visit(instance: new FixedCreationStampInstance(
                Origin: (origin + rotation.Rotate(vector: local)),
                ReflectionNormal: reflectionNormal
            ));
        }
    }
    /// <summary>Visits pattern copies in A-major, then B-major order, followed immediately by each reflected copy:
    /// the single-precision reading of <see cref="ForEachFixedInstance(FixedVector3, FixedQuaternion, CreationStampPattern?, IReadOnlyList{FixedVector3}?, CreationStampPlane?, Action{FixedCreationStampInstance})"/>,
    /// whose instances it converts for the render path.</summary>
    /// <param name="origin">The placement origin.</param>
    /// <param name="rotation">The placement rotation.</param>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    /// <param name="sampledOffsets">Precomputed placement-local offsets from a hash-sampled Noise/Scatter region
    /// (see <see cref="ForEachFixedInstance(FixedVector3, FixedQuaternion, CreationStampPattern?, IReadOnlyList{FixedVector3}?, CreationStampPlane?, Action{FixedCreationStampInstance})"/>), or <see langword="null"/> to use <paramref name="pattern"/>.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="visitor">Receives each materialized instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="visitor"/> is <see langword="null"/>.</exception>
    public static void ForEachInstance(Vector3 origin, Quaternion rotation, CreationStampPattern? pattern, IReadOnlyList<Vector3>? sampledOffsets, CreationStampPlane? mirror, Action<CreationStampInstance> visitor) {
        ArgumentNullException.ThrowIfNull(visitor);

        var adapter = new ActionVisitor(action: visitor);

        ForEachInstance(
            mirror: mirror,
            origin: origin,
            pattern: pattern,
            rotation: rotation,
            sampledOffsets: sampledOffsets,
            visitor: ref adapter
        );
    }
    /// <summary>Visits pattern copies in A-major, then B-major order, followed immediately by each reflected copy,
    /// through a struct visitor, so the walk allocates nothing beyond the fixed copy of
    /// <paramref name="sampledOffsets"/>: the single-precision reading of
    /// <see cref="ForEachFixedInstance{TVisitor}"/>, whose instances it converts for the render path.</summary>
    /// <typeparam name="TVisitor">The visitor's type.</typeparam>
    /// <param name="origin">The placement origin.</param>
    /// <param name="rotation">The placement rotation.</param>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    /// <param name="sampledOffsets">Precomputed placement-local offsets from a hash-sampled Noise/Scatter region
    /// (see <see cref="ForEachFixedInstance{TVisitor}"/>), or <see langword="null"/> to use <paramref name="pattern"/>.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="visitor">Receives each materialized instance.</param>
    public static void ForEachInstance<TVisitor>(Vector3 origin, Quaternion rotation, CreationStampPattern? pattern, IReadOnlyList<Vector3>? sampledOffsets, CreationStampPlane? mirror, ref TVisitor visitor) where TVisitor : struct, ICreationStampVisitor {
        var converting = new ConvertingVisitor<TVisitor>(inner: ref visitor);
        FixedVector3[]? fixedOffsets = null;

        if (sampledOffsets is { Count: > 0 } offsets) {
            fixedOffsets = new FixedVector3[offsets.Count];

            for (var index = 0; (index < fixedOffsets.Length); index++) {
                fixedOffsets[index] = FixedVector3.FromVector3(value: offsets[index]);
            }
        }

        ForEachFixedInstance(
            mirror: mirror,
            origin: FixedVector3.FromVector3(value: origin),
            pattern: pattern,
            rotation: FixedQuaternion.FromQuaternion(value: rotation).Normalize(),
            sampledOffsets: fixedOffsets,
            visitor: ref converting
        );
    }
    /// <summary>Returns the number of materialized render instances.</summary>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/>.</param>
    /// <param name="sampledCount">The resolved Noise/Scatter offset count, or <see langword="null"/> to count
    /// <paramref name="pattern"/>'s grid instead.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    public static int InstanceCount(CreationStampPattern? pattern, int? sampledCount, CreationStampPlane? mirror) {
        var copies = (sampledCount ?? checked((Math.Max(
            val1: (pattern?.CountA ?? 1),
            val2: 1
        ) * Math.Max(
            val1: (pattern?.CountB ?? 1),
            val2: 1
        ))));

        return ((mirror is null)
            ? copies
            : checked((copies * 2))
        );
    }
    /// <summary>Returns the materialized pattern-and-mirror copy count, saturated at <paramref name="ceiling"/>.</summary>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/>.</param>
    /// <param name="sampledCount">The resolved Noise/Scatter offset count, or <see langword="null"/> to count
    /// <paramref name="pattern"/>'s grid instead.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="ceiling">The largest returned value.</param>
    public static long MaterializedCopyCount(CreationStampPattern? pattern, long? sampledCount, CreationStampPlane? mirror, long ceiling = long.MaxValue) {
        var copies = ((sampledCount is { } sampled)
            ? Math.Min(
                val1: sampled,
                val2: ceiling
            )
            : MultiplySaturated(
                ceiling: ceiling,
                left: Math.Max(
                    val1: (pattern?.CountA ?? 1),
                    val2: 1
                ),
                right: Math.Max(
                    val1: (pattern?.CountB ?? 1),
                    val2: 1
                )
            )
        );

        return ((mirror is null)
            ? copies
            : MultiplySaturated(
                ceiling: ceiling,
                left: copies,
                right: 2L
            )
        );
    }
    /// <summary>Multiplies non-negative counts and saturates at <paramref name="ceiling"/>.</summary>
    /// <param name="left">The first count.</param>
    /// <param name="right">The second count.</param>
    /// <param name="ceiling">The largest returned value.</param>
    /// <returns>The product, <paramref name="ceiling"/> when the product exceeds it, and zero when either count is
    /// not positive.</returns>
    public static long MultiplySaturated(long left, long right, long ceiling) {
        if (
            (left <= 0L) ||
            (right <= 0L)
        ) {
            return 0L;
        }

        return Math.Min(
            val1: left.MultiplySaturating(right: right),
            val2: ceiling
        );
    }

    // Adapts a delegate visitor to the struct walk, so the delegate overload and the struct one share one walk.
    private readonly struct ActionVisitor(Action<CreationStampInstance> action) : ICreationStampVisitor {
        public void Visit(CreationStampInstance instance) => action(obj: instance);
    }
    private readonly struct FixedActionVisitor(Action<FixedCreationStampInstance> action) : IFixedCreationStampVisitor {
        public void Visit(FixedCreationStampInstance instance) => action(obj: instance);
    }
    // Hands a render visitor the fixed walk's instances as single precision, holding the caller's visitor by
    // reference so its state advances in place.
    private ref struct ConvertingVisitor<TInner> : IFixedCreationStampVisitor where TInner : struct, ICreationStampVisitor {
        private readonly ref TInner m_inner;

        public ConvertingVisitor(ref TInner inner) {
            m_inner = ref inner;
        }

        public void Visit(FixedCreationStampInstance instance) =>
            m_inner.Visit(instance: new CreationStampInstance(
                Origin: instance.Origin.ToVector3(),
                ReflectionNormal: instance.ReflectionNormal?.ToVector3()
            ));
    }
}
