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
public readonly record struct CreationStampTransform(
    Vector3 Origin,
    Quaternion Rotation,
    float Scale,
    Vector3? ReflectionNormal
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
/// <param name="Center">The primitive's world-axis bound center.</param>
/// <param name="HalfExtents">The primitive's world-axis bound half-extents.</param>
/// <param name="UniformScale">The primitive's world scale when it is isotropic; zero otherwise.</param>
/// <param name="PlaneNormal">The unit world normal for an unbounded plane; zero for a finite primitive.</param>
public readonly record struct FixedCreationStampPrimitiveCopy(ShapeDocument Shape, FixedVector3 Center, FixedVector3 HalfExtents, FixedQ4816 UniformScale, FixedVector3 PlaneNormal);
/// <summary>
/// Emits and expands <c>puck.creation.v1</c> shape geometry under one materialized stamp transform.
/// </summary>
public static class CreationStampEmitter {
    private const float MinimumTransformExtent = 0.0001f;

    private static FixedVector3 EffectiveFixedScale(Vector3 value) =>
        new(
            X: FixedQ4816.Max(
                x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.X)),
                y: MinimumTransformExtentFixed
            ),
            Y: FixedQ4816.Max(
                x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.Y)),
                y: MinimumTransformExtentFixed
            ),
            Z: FixedQ4816.Max(
                x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.Z)),
                y: MinimumTransformExtentFixed
            )
        );
    private static Vector3 EffectiveScale(Vector3 value) => Vector3.Max(
        value1: Vector3.Abs(value: value),
        value2: new Vector3(value: MinimumTransformExtent)
    );
    private static FixedVector3 ReflectFixed(FixedVector3 value, FixedVector3 normal) {
        var projection = FixedVector3.Dot(
            left: value,
            right: normal
        );

        return (value - (normal * (projection + projection)));
    }
    private static Vector3 ReflectVector(Vector3 value, Vector3 normal) => (value - ((2f * Vector3.Dot(
        vector1: value,
        vector2: normal
    )) * normal));
    private static (Vector3 Position, Quaternion Rotation) ReflectedShapeTransform(ShapeDocument shape, Vector3? normal) {
        if (normal is not { } authoredNormal) {
            return (Position: shape.Position, Rotation: shape.Rotation);
        }

        var unitNormal = Vector3.Normalize(value: authoredNormal);
        var rotation = Quaternion.Normalize(value: shape.Rotation);
        var axisX = -ReflectVector(
            value: Vector3.Transform(
                value: Vector3.UnitX,
                rotation: rotation
            ),
            normal: unitNormal
        );
        var axisY = ReflectVector(
            value: Vector3.Transform(
                value: Vector3.UnitY,
                rotation: rotation
            ),
            normal: unitNormal
        );
        var axisZ = ReflectVector(
            value: Vector3.Transform(
                value: Vector3.UnitZ,
                rotation: rotation
            ),
            normal: unitNormal
        );
        var reflectedRotation = Quaternion.Normalize(value: Quaternion.CreateFromRotationMatrix(matrix: new Matrix4x4(
            m11: axisX.X,
            m12: axisX.Y,
            m13: axisX.Z,
            m14: 0f,
            m21: axisY.X,
            m22: axisY.Y,
            m23: axisY.Z,
            m24: 0f,
            m31: axisZ.X,
            m32: axisZ.Y,
            m33: axisZ.Z,
            m34: 0f,
            m41: 0f,
            m42: 0f,
            m43: 0f,
            m44: 1f
        )));

        return (Position: ReflectVector(
            value: shape.Position,
            normal: unitNormal
        ), Rotation: reflectedRotation);
    }
    private static (FixedVector3 Position, FixedQuaternion Rotation) ReflectedShapeTransformFixed(ShapeDocument shape, FixedVector3? normal) {
        var position = FixedVector3.FromVector3(value: shape.Position);
        var rotation = FixedQuaternion.FromQuaternion(value: shape.Rotation).Normalize();

        if (normal is not { } unitNormal) {
            return (Position: position, Rotation: rotation);
        }

        // Let H(n) be reflection across the plane with normal n. Negating the reflected X axis turns the improper
        // reflected basis back into the proper frame the float emitter has always authored. If b = R*x, that frame is
        // H(n) R H(x) = H(n) H(b) R. The product of the two reflections H(n)H(b) is the unit quaternion
        // (b x n, b . n), so no matrix-to-quaternion reconstruction (and therefore no platform sqrt/libm) is needed.
        var reflectedXAxis = rotation.Rotate(vector: UnitX).Normalize();
        var reflectionPairVector = FixedVector3.Cross(
            left: reflectedXAxis,
            right: unitNormal
        );
        var reflectionPair = new FixedQuaternion(
            X: reflectionPairVector.X,
            Y: reflectionPairVector.Y,
            Z: reflectionPairVector.Z,
            W: FixedVector3.Dot(
                left: reflectedXAxis,
                right: unitNormal
            )
        ).Normalize();

        return (
            Position: ReflectFixed(
            normal: unitNormal,
            value: position
        ),
            Rotation: (reflectionPair * rotation).Normalize()
        );
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

    /// <summary>Emits a creation's shape list under one materialized stamp transform.</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <param name="materialFor">Resolves each shape's material id.</param>
    /// <param name="contactMargin">An optional per-shape signed contact margin. Null emits the raw render stream;
    /// a nonzero value scopes each primitive so dilation applies before its authored blend.</param>
    /// <param name="inScope">Whether the caller already holds an open field scope around this emission (the
    /// whole-creation stamp of <see cref="RequiresScope"/>). A scope nests at most one deep, so an eccentric shape
    /// then rides the caller's scope instead of opening its own.</param>
    public static void Emit(SdfProgramBuilder builder, CreationDocument document, CreationStampTransform transform, Func<ShapeDocument, int> materialFor, float? contactMargin = null, bool inScope = false) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(materialFor);

        foreach (var shape in (document.Shapes ?? [])) {
            EmitShapeChain(
                builder: builder,
                contactMargin: contactMargin,
                inScope: inScope,
                material: materialFor(arg: shape),
                panelMaterial: ((shape.Panel is { } panel)
                ? materialFor(arg: shape with { Material = panel.Material })
                : (int?)null),
                shape: shape,
                transform: transform
            );
            EmitTrims(
                builder: builder,
                contactMargin: contactMargin,
                document: document,
                inScope: inScope,
                materialFor: materialFor,
                shape: shape,
                transform: transform
            );
        }
    }

    // panelMaterial is the panel copy's already-resolved program material id (null only when the shape has no panel;
    // a panelled shape without one is a caller defect and throws). Render path only
    // (contactMargin null): the deterministic fixed-point contact evaluator (EmitFixed/VisitFixedPrimitiveCopies)
    // never calls this with a panelled shape's second material, so a solid placement's collider reads the plain
    // plate regardless of Panel.
    private static void EmitShapeChain(SdfProgramBuilder builder, ShapeDocument shape, CreationStampTransform transform, int material, float? contactMargin, bool inScope = false, int? panelMaterial = null) {
        var (shapePosition, shapeRotation) = ReflectedShapeTransform(
            shape: shape,
            normal: transform.ReflectionNormal
        );
        var shapeScale = EffectiveScale(value: shape.Scale);

        SdfProgramBuilder BuildTransformChain() {
            var prefix = ShapeDomainOps.Apply(
                chain: builder
                    .ResetPoint()
                    .Translate(offset: transform.Origin)
                    .Rotate(rotation: transform.Rotation)
                    .Scale(scale: new Vector3(value: transform.Scale)),
                domain: shape.Domain
            )
                .Translate(offset: shapePosition)
                .Rotate(rotation: shapeRotation);

            // Creation-unit lengths, like the rest of this chain past the Scale(transform.Scale) op above — no
            // explicit placement-scale multiply needed here (unlike WorldStampPool.EmitShape's twist/bend/flare
            // prefix, which has no such chain-level Scale and so must bake placementScale in by hand).
            var flared = ((shape.Flare is { } flare)
                ? prefix.AxialProfile(amount: flare.Amount, bulge: flare.Bulge, top: (flare.Top ?? 0f), span: flare.Span, axis: flare.Axis, startScale: flare.StartScale)
                : prefix);
            var sheared = ((shape.Shear is { } shear)
                ? flared.Shear(linear: shear.Linear, quadratic: shear.Quadratic, cubic: shear.Cubic, target: shear.Target, driver: shear.Driver)
                : flared);

            foreach (var bump in (shape.Bumps ?? [])) {
                sheared = sheared.GaussianPush(center: bump.Center, radii: bump.Radii, push: bump.Push);
            }

            return sheared;
        }

        var chain = BuildTransformChain();
        // Per-shape lane-driven erosion (KEEP IN SYNC with WorldStampPool.EmitShape's mirrored prefix): the reach is
        // this shape's own SdfSolidGeometry.Reach, taken in WORLD units like the rest of this chain past the Scale
        // op above. Chained immediately before whichever AppendScaledPrimitive call emits this shape's ShapeBlend —
        // ordinary point ops (twist/bend/flare/shear/bumps, already applied above) land on the point as usual.
        var erodeReach = ((shape.Erode is not null)
            ? (SdfSolidGeometry.Reach(
                lift: (shape.Lift ?? SdfLift.Extrude),
                scale: shapeScale,
                type: shape.Type
            ) * transform.Scale)
            : 0f);

        SdfProgramBuilder ApplyErode(SdfProgramBuilder target) =>
            ((shape.Erode is { } erode)
                ? target.LaneErode(
                    from: erode.From,
                    lane: erode.Lane,
                    noiseScale: (erode.Noise ?? 1f),
                    reach: erodeReach,
                    to: erode.To
                )
                : target);

        var blend = (shape.Blend ?? SdfBlendOp.Union);
        // The blend radius and the field ops below act on the running WORLD-space accumulator directly — never
        // re-multiplied by the chain's own Scale(transform.Scale) op the way a primitive's baked-local rounding/
        // chamfer is (AppendScaledPrimitive, above) — so they take the stamp scale explicitly here.
        var smooth = ((shape.Smooth ?? 0f) * transform.Scale);
        var dilate = ((shape.Dilate ?? 0f) * transform.Scale);
        var onion = ((shape.Onion ?? 0f) * transform.Scale);
        var wantsDilate = (dilate != 0f);
        var wantsOnion = (onion != 0f);

        if (
            (contactMargin is not { } margin) ||
            (margin == 0f)
        ) {
            var panel = shape.Panel;
            // An eccentric primitive (a non-uniformly scaled sphere baked as an ellipsoid) outside any scope would fold
            // its Lipschitz factor into the whole program's step scale, and every march in the frame would pay it. Its
            // own scope clamps that factor onto its candidate at the pop instead (SdfProgram.AnalyzeLipschitz); inside
            // a caller's scope (one-deep by contract) the caller's pop already does. KEEP IN SYNC with the static
            // stamper's per-shape probe reservation (Client.WorldPlacementStamper.EmitProbe), which reserves the pair.
            // Dilate/Onion need the same isolation for a different reason — unscoped, a field op would inflate or
            // hollow every shape emitted before it in the whole program, not just this one. A panel ALSO needs its
            // own scope: its subtraction/union must bite only this shape's own candidate, never a sibling composed
            // before it — validation refuses a panel wherever inScope would be true here (RequiresScope/Group), so
            // this branch is unreachable for a panelled shape.
            // A flare/shear/bump/erode (each a warp, or an erosion whose noise term folds the same way — see
            // SdfProgram.Lipschitz.cs's LaneErode case) joins the eccentric case for the same reason: unscoped,
            // its Lipschitz factor would fold into the whole program's step scale.
            var ownScope = (
                !inScope &&
                (wantsDilate || wantsOnion ||
                (SdfSolidGeometry.StepFactor(
                    scale: shapeScale,
                    type: shape.Type
                ) > 1f) || (panel is not null) || (shape.Flare is not null) || (shape.Shear is not null) || (shape.Bumps is { Count: > 0 }) || (shape.Erode is not null) || (shape.Cells is not null))
            );

            if (ownScope) {
                var scoped = SdfSolidGeometry.AppendScaledPrimitive(
                    chain: ApplyErode(chain.PushField(
                        compose: blend,
                        smooth: smooth
                    )),
                    type: shape.Type, taper: shape.Taper ?? 0.5f, profile: shape.Profile,
                    lift: (shape.Lift ?? SdfLift.Extrude), rounding: (shape.Rounding ?? 0f), chamfer: (shape.Chamfer ?? 0f), exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
                    curve: shape.Curve?.Parameters(),
                    scale: shapeScale,
                    material: material,
                    detail: (shape.Detail ?? false)
                ).MarkSecondary(secondary: (shape.Secondary ?? true));

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
                    _ = builder.ResetPoint()
                        .Translate(transform.Origin + Vector3.Transform(shapePosition * transform.Scale, transform.Rotation))
                        .Rotate(Quaternion.Normalize(transform.Rotation * shapeRotation))
                        .CellDisplace(cells.Frequency / transform.Scale, cells.Amplitude * transform.Scale,
                            cells.Seed, cells.Mode, cells.Randomness);
                }
                _ = chain.PopField();

                return;
            }

            var afterShape = SdfSolidGeometry.AppendScaledPrimitive(
                chain: chain,
                type: shape.Type, taper: shape.Taper ?? 0.5f, profile: shape.Profile,
                    lift: (shape.Lift ?? SdfLift.Extrude), rounding: (shape.Rounding ?? 0f), chamfer: (shape.Chamfer ?? 0f), exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
                    curve: shape.Curve?.Parameters(),
                scale: shapeScale,
                material: material,
                blend: blend,
                smooth: smooth,
                detail: (shape.Detail ?? false)
            ).MarkSecondary(secondary: (shape.Secondary ?? true));

            if (wantsDilate) {
                afterShape = afterShape.Dilate(radius: dilate);
            }

            if (wantsOnion) {
                _ = afterShape.Onion(thickness: onion);
            }

            return;
        }

        chain = SdfSolidGeometry.AppendScaledPrimitive(
            chain: ApplyErode(chain.PushField(
                compose: blend,
                smooth: smooth
            )),
            type: shape.Type, taper: shape.Taper ?? 0.5f, profile: shape.Profile,
                    lift: (shape.Lift ?? SdfLift.Extrude), rounding: (shape.Rounding ?? 0f), chamfer: (shape.Chamfer ?? 0f), exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
                    curve: shape.Curve?.Parameters(),
            scale: shapeScale,
            material: material,
            detail: (shape.Detail ?? false)
        ).MarkSecondary(secondary: (shape.Secondary ?? true)).Dilate(radius: margin);
        _ = chain.PopField();
    }
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

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            chain: baseChain.Translate(offset: (placement.FaceAxis * placement.Offset)),
            type: shape.Type, taper: shape.Taper ?? 0.5f, profile: shape.Profile,
            lift: lift, rounding: (shape.Rounding ?? 0f), chamfer: (shape.Chamfer ?? 0f), exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
            curve: shape.Curve?.Parameters(),
            scale: placement.ErodedScale,
            material: material,
            blend: placement.Blend,
            smooth: 0f
        );
    }

    // Render path only (mirrors EmitPanelCopy's own guard) — the deterministic fixed-point contact evaluator never
    // reads Trims, so a solid placement's collider is unchanged by them. Runs after EmitShapeChain has fully closed
    // the host's own emission (including any scope panel/dilate/onion/eccentricity opened), so each trim's own
    // PushField/PopField pair is sequential with that scope, never nested inside it. inScope true means the CALLER
    // already holds the whole-creation scope RequiresScope forces — ValidateTrims refuses Trims on that document, so
    // this is a defensive no-op, never reached by a validated document.
    private static void EmitTrims(SdfProgramBuilder builder, CreationDocument document, ShapeDocument shape, CreationStampTransform transform, Func<ShapeDocument, int> materialFor, float? contactMargin, bool inScope) {
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
                material: materialFor(arg: shape with { Material = trim.Material }),
                reference: reference,
                shape: shape,
                transform: transform,
                trim: trim
            );
        }
    }
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
    private static void EmitTrim(SdfProgramBuilder builder, ShapeDocument shape, ShapeDocument reference, CreationStampTransform transform, ShapeTrimDocument trim, int material) {
        var (hostPosition, hostRotation) = ReflectedShapeTransform(
            shape: shape,
            normal: transform.ReflectionNormal
        );
        var hostChain = builder
            .ResetPoint()
            .Translate(offset: transform.Origin)
            .Rotate(rotation: transform.Rotation)
            .Scale(scale: new Vector3(value: transform.Scale))
            .Translate(offset: hostPosition)
            .Rotate(rotation: hostRotation);

        var eroded = SdfSolidGeometry.AppendScaledPrimitive(
            chain: hostChain.PushField(compose: SdfBlendOp.Union),
            type: shape.Type, taper: shape.Taper ?? 0.5f, profile: shape.Profile,
            lift: (shape.Lift ?? SdfLift.Extrude), rounding: (shape.Rounding ?? 0f), chamfer: (shape.Chamfer ?? 0f), exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
            curve: shape.Curve?.Parameters(),
            scale: EffectiveScale(value: shape.Scale),
            material: material,
            blend: SdfBlendOp.Union,
            smooth: 0f
        ).Dilate(radius: (trim.Inset * transform.Scale));

        var (referencePosition, referenceRotation) = ReflectedShapeTransform(
            shape: reference,
            normal: transform.ReflectionNormal
        );
        var referenceChain = eroded
            .ResetPoint()
            .Translate(offset: transform.Origin)
            .Rotate(rotation: transform.Rotation)
            .Scale(scale: new Vector3(value: transform.Scale))
            .Translate(offset: referencePosition)
            .Rotate(rotation: referenceRotation);

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            chain: referenceChain,
            type: reference.Type, taper: reference.Taper ?? 0.5f, profile: reference.Profile,
            lift: (reference.Lift ?? SdfLift.Extrude), rounding: (reference.Rounding ?? 0f), chamfer: (reference.Chamfer ?? 0f), exponent: (reference.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
            scale: ShapeTrimDocument.DilatedScale(scale: EffectiveScale(value: reference.Scale), width: trim.Width),
            material: material,
            blend: SdfBlendOp.Intersection,
            smooth: 0f
        );

        _ = builder.PopField();
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
        ArgumentOutOfRangeException.ThrowIfZero(value: paletteIds.Length, paramName: nameof(paletteIds));

        var shape = document.Shapes![shapeIndex];

        EmitShapeChain(
            builder: builder,
            contactMargin: null,
            material: material,
            panelMaterial: ((shape.Panel is { } panel)
            ? paletteIds[Math.Clamp(value: panel.Material, max: (paletteIds.Length - 1), min: 0)]
            : (int?)null),
            shape: shape,
            transform: transform
        );
        EmitTrims(
            builder: builder,
            contactMargin: null,
            document: document,
            inScope: false,
            materialFor: candidate => paletteIds[Math.Clamp(value: (candidate.Material ?? 0), max: (paletteIds.Length - 1), min: 0)],
            shape: shape,
            transform: transform
        );
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

        var (shapePosition, _) = ReflectedShapeTransform(
            shape: shape,
            normal: transform.ReflectionNormal
        );
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
        ));
        var warpedReach = ShapeWarpReach.Expand(primitiveReach + (shape.Cells?.Parameters.OutwardReach ?? 0f) * ShapeFlareDocument.ReachFactor(shape.Flare), shape.Flare, shape.Shear, ShapeBumpDocument.ReachExtra(shape.Bumps));

        return (
            (transform.Origin + Vector3.Transform(
                value: (shapePosition * transform.Scale),
                rotation: transform.Rotation
            )),
            ((warpedReach + (shape.Dilate ?? 0f) + (shape.Onion ?? 0f)) * transform.Scale)
        );
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

        foreach (var shape in (document.Shapes ?? [])) {
            var blend = (shape.Blend ?? SdfBlendOp.Union);

            if (blend is not (SdfBlendOp.Union or SdfBlendOp.SmoothUnion)) {
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

        _ = builder
            .ResetPoint()
            .Translate(offset: transform.Origin)
            .Rotate(rotation: transform.Rotation)
            .Scale(scale: new Vector3(value: transform.Scale))
            .NoiseDisplace(
                amplitude: noise.Amplitude,
                frequency: noise.Frequency,
                gain: (noise.Gain ?? 0.5f),
                lacunarity: (noise.Lacunarity ?? 2f),
                octaves: (noise.Octaves ?? 4),
                seed: (noise.Seed ?? 0u)
            );
    }
    /// <summary>Emits a creation's shape list under one materialized stamp transform, deriving every transform
    /// constant in deterministic fixed point before the SDF program's single-precision encoding boundary.</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The fixed-point stamp transform.</param>
    /// <param name="materialFor">Resolves each shape's material id.</param>
    /// <param name="contactMargin">An optional per-shape signed contact margin. Null emits the raw render stream;
    /// a nonzero value scopes each primitive so dilation applies before its authored blend.</param>
    /// <remarks>This is the collision-field sibling of <see cref="Emit"/>. In particular, a mirrored shape's
    /// orientation is composed from its two reflection planes as a fixed quaternion; it never visits
    /// <see cref="Matrix4x4"/>, <see cref="Quaternion.CreateFromRotationMatrix"/>, or a floating-point normalize.</remarks>
    public static void EmitFixed(SdfProgramBuilder builder, CreationDocument document, FixedCreationStampTransform transform, Func<ShapeDocument, int> materialFor, float? contactMargin = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(materialFor);

        var stampRotation = transform.Rotation.Normalize();
        var stampScale = FixedQ4816.Max(
            x: FixedQ4816.Abs(value: transform.Scale),
            y: MinimumTransformExtentFixed
        );
        var reflectionNormal = transform.ReflectionNormal?.Normalize();

        foreach (var shape in (document.Shapes ?? [])) {
            // A detail shape is SHADING-ONLY — it never reaches this contact compiler at all, so it neither carves
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
                frames: out var frames,
                refusal: out var refusal
            )) {
                throw new ArgumentException(
                    message: $"A contact-emitted creation shape carries {refusal}, so its copies have no contact geometry.",
                    paramName: nameof(document)
                );
            }

            var (shapePosition, shapeRotation) = ReflectedShapeTransformFixed(
                normal: reflectionNormal,
                shape: shape
            );
            var local = new SdfRigidFrame(
                Mirrored: (reflectionNormal is not null),
                Position: shapePosition,
                Rotation: shapeRotation
            );
            var shapeScale = EffectiveFixedScale(value: shape.Scale).ToVector3();
            var blend = (shape.Blend ?? SdfBlendOp.Union);
            var smooth = (shape.Smooth ?? 0f);

            foreach (var frame in frames) {
                var placed = frame.Compose(inner: local);
                var chain = builder
                    .ResetPoint()
                    .Translate(offset: transform.Origin.ToVector3())
                    .Rotate(rotation: stampRotation)
                    .Scale(scale: new Vector3(value: ((float)((double)stampScale))))
                    .Translate(offset: placed.Position.ToVector3())
                    .Rotate(rotation: placed.Rotation.ToQuaternion());

                if (
                    (contactMargin is not { } margin) ||
                    (margin == 0f)
                ) {
                    _ = SdfSolidGeometry.AppendScaledPrimitive(
                        chain: chain,
                        type: shape.Type, taper: shape.Taper ?? 0.5f, profile: shape.Profile,
                    lift: (shape.Lift ?? SdfLift.Extrude), rounding: (shape.Rounding ?? 0f), chamfer: (shape.Chamfer ?? 0f), exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
                    curve: shape.Curve?.Parameters(),
                        scale: shapeScale,
                        material: materialFor(arg: shape),
                        blend: blend,
                        smooth: smooth
                    );

                    continue;
                }

                chain = SdfSolidGeometry.AppendScaledPrimitive(
                    chain: chain.PushField(
                        compose: blend,
                        smooth: smooth
                    ),
                    type: shape.Type, taper: shape.Taper ?? 0.5f, profile: shape.Profile,
                    lift: (shape.Lift ?? SdfLift.Extrude), rounding: (shape.Rounding ?? 0f), chamfer: (shape.Chamfer ?? 0f), exponent: (shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
                    curve: shape.Curve?.Parameters(),
                    scale: shapeScale,
                    material: materialFor(arg: shape)
                ).Dilate(radius: margin);
                _ = chain.PopField();
            }
        }
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
                    value: (run.Position * EffectiveScale(value: shape.Scale)),
                    rotation: shape.Rotation
                )),
                Quaternion.Normalize(value: (shape.Rotation.Value * run.Rotation.Value))
            );
        }

        return (run.Position, run.Rotation);
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

        var runs = (document.TextRuns ?? []);

        for (var index = 0; (index < runs.Count); index++) {
            var run = runs[index];

            var (position, rotation) = RunFrame(
                document: document,
                run: run
            );
            var localRight = Vector3.Transform(
                value: Vector3.UnitX,
                rotation: rotation
            );
            var localUp = Vector3.Transform(
                value: Vector3.UnitY,
                rotation: rotation
            );

            if (transform.ReflectionNormal is { } authoredNormal) {
                var normal = Vector3.Normalize(value: authoredNormal);

                position = ReflectVector(
                    normal: normal,
                    value: position
                );
                localRight = ReflectVector(
                    normal: normal,
                    value: localRight
                );
                localUp = ReflectVector(
                    normal: normal,
                    value: localUp
                );
            }

            _ = builder.Text(
                atlas: fontFor(arg: run.Font),
                text: run.Text,
                origin: (transform.Origin + Vector3.Transform(
                    value: (position * transform.Scale),
                    rotation: transform.Rotation
                )),
                right: Vector3.Transform(
                    value: localRight,
                    rotation: transform.Rotation
                ),
                up: Vector3.Transform(
                    value: localUp,
                    rotation: transform.Rotation
                ),
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
    /// <summary>Measures the render-time bounding-sphere radius of a creation after resolving its authored fonts and
    /// layout options. Shape geometry uses <see cref="SdfSolidGeometry.Reach(SdfSolidPrimitive, Vector3, SdfLift)"/>; text geometry
    /// is measured from the exact laid-out glyph cells the SDF builder emits, including whitespace advance, wrapping,
    /// alignment, tracking, line spacing, atlas padding, and extrusion.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="scale">The uniform stamp scale.</param>
    /// <param name="fontFor">Resolves a run's optional font name through the owning catalog, or
    /// <see langword="null"/> when this render path omits text.</param>
    /// <param name="textLayouts">Per-run layouts already computed by <see cref="LayoutTextRuns"/> for this same
    /// (<paramref name="document"/>, <paramref name="scale"/>, <paramref name="fontFor"/>) — indexed like
    /// <see cref="CreationDocument.TextRuns"/>, reused instead of laying each run out again;
    /// <see langword="null"/> lays every run out fresh, exactly as before this parameter existed.</param>
    /// <returns>The radius, in the builder's current coordinate space.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not finite and greater than zero.</exception>
    public static float RenderReach(CreationDocument document, float scale, Func<string?, FontAtlas>? fontFor, IReadOnlyList<TextLayoutResult>? textLayouts = null) {
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

        foreach (var shape in (document.Shapes ?? [])) {
            // A domain fold carries the shape across its lattice, so the fold's displacement bound is charged
            // beside the shape's own position; Dilate/Onion move the outer surface outward by their own value — all
            // in creation units, scaled with them.
            // A flare grows the primitive's own reach by up to max(s) (ShapeFlareDocument.ReachFactor). A Sweep
            // carries no SdfSolidGeometry.Reach unit-scale law (its own control points already carry creation-unit
            // dimensions — see SdfSolidPrimitive.Sweep's remarks); its own uniform scale bakes on directly.
            var primitiveReach = ((shape.Type == SdfSolidPrimitive.Sweep)
                ? ((shape.Curve?.Reach() ?? 0f) * shape.Scale.X)
                : SdfSolidGeometry.Reach(
                type: shape.Type,
                scale: shape.Scale,
                lift: (shape.Lift ?? SdfLift.Extrude)
            ));
            var warpedReach = ShapeWarpReach.Expand(primitiveReach + (shape.Cells?.Parameters.OutwardReach ?? 0f) * ShapeFlareDocument.ReachFactor(shape.Flare), shape.Flare, shape.Shear, ShapeBumpDocument.ReachExtra(shape.Bumps));

            reach = MathF.Max(
                x: reach,
                y: (((shape.Position.Length() + warpedReach + ShapeDomainOps.Reach(domain: shape.Domain) + (shape.Dilate ?? 0f) + (shape.Onion ?? 0f)) * scale) + (document.Noise?.Amplitude ?? 0f))
            );
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

            if (layout.Placements.Count > 0) {
                reach = MathF.Max(
                    x: reach,
                    y: ((run.Position * scale).Length() + runReach)
                );
                any = true;
            }
        }

        return (any
            ? reach
            : (0.6f * scale)
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

        var stampRotation = transform.Rotation.Normalize();
        var stampScale = FixedQ4816.Max(
            x: FixedQ4816.Abs(value: transform.Scale),
            y: MinimumTransformExtentFixed
        );
        var reflectionNormal = transform.ReflectionNormal?.Normalize();

        foreach (var shape in (document.Shapes ?? [])) {
            // A detail shape is SHADING-ONLY — it never reaches this contact compiler at all, so it neither carves
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
                frames: out var frames,
                refusal: out var refusal
            )) {
                throw new ArgumentException(
                    message: $"A contact-emitted creation shape carries {refusal}, so its copies have no contact geometry.",
                    paramName: nameof(document)
                );
            }

            var bounds = SdfSolidGeometry.GetLocalBounds(type: shape.Type);
            var boundsCenter = FixedVector3.FromVector3(value: bounds.Center);
            var boundsHalfExtents = FixedVector3.FromVector3(value: bounds.HalfExtents);
            var shapeScale = EffectiveFixedScale(value: shape.Scale);

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
                var shapeAxisX = placed.Rotation.Rotate(vector: UnitX);
                var shapeAxisY = placed.Rotation.Rotate(vector: UnitY);
                var shapeAxisZ = placed.Rotation.Rotate(vector: UnitZ);
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
                    PlaneNormal: (bounds.IsUnbounded
                    ? axisY.Normalize()
                    : FixedVector3.Zero),
                    Shape: shape,
                    UniformScale: uniformScale
                ));
            }
        }
    }

    // The degeneracy floor in the fixed domain. Q48.16 resolves 1/65536, so 0.0001 lands on the nearest representable
    // value above it rather than exactly — this is a guard against a zero-scale axis collapsing the frame, never a
    // value a result is read off, so the quantization is immaterial. A product of two floors still underflows to zero,
    // which yields a zero-extent (inert) collider rather than the float path's vanishingly thin one.
    private static readonly FixedQ4816 MinimumTransformExtentFixed = FixedQ4816.FromDouble(value: MinimumTransformExtent);
    private static readonly FixedVector3 UnitX = new(
        X: FixedQ4816.One,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedVector3 UnitY = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedVector3 UnitZ = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.One
    );
}
/// <summary>Materializes the same placement pattern and reflected copies consumed by creation stamp emission.</summary>
public static class CreationStampLattice {
    /// <summary>Visits pattern copies in A-major, then B-major order, followed immediately by each reflected copy —
    /// the deterministic counterpart to <see cref="ForEachInstance"/>, in the same order. When
    /// <paramref name="sampledOffsets"/> is supplied (a resolved <see cref="CreationStampSampling"/> Noise/Scatter
    /// offset set), those offsets replace the regular pattern grid entirely — <paramref name="pattern"/> is ignored —
    /// while mirroring composes identically either way.</summary>
    /// <param name="origin">The placement origin.</param>
    /// <param name="rotation">The placement rotation.</param>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    /// <param name="sampledOffsets">Precomputed placement-local offsets from a hash-sampled Noise/Scatter region, or
    /// <see langword="null"/> to use <paramref name="pattern"/>.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="visitor">Receives each materialized instance.</param>
    /// <remarks>The pattern and mirror are AUTHORED single-precision records, so they enter the contract through
    /// <see cref="FixedVector3.FromVector3"/> here rather than at each caller. The step accumulation is a scaled
    /// index rather than a running sum, exactly as the single-precision body computes it, so copy <c>n</c> does not
    /// inherit <c>n−1</c> roundings.</remarks>
    public static void ForEachFixedInstance(FixedVector3 origin, FixedQuaternion rotation, CreationStampPattern? pattern, IReadOnlyList<FixedVector3>? sampledOffsets, CreationStampPlane? mirror, Action<FixedCreationStampInstance> visitor) {
        ArgumentNullException.ThrowIfNull(visitor);

        var planeNormal = ((mirror is { } authoredPlane)
            ? FixedVector3.FromVector3(value: authoredPlane.Normal).Normalize()
            : (FixedVector3?)null
        );
        var planeOffset = FixedQ4816.FromDouble(value: (mirror?.Offset ?? 0f));

        if (sampledOffsets is { Count: > 0 } offsets) {
            for (var index = 0; (index < offsets.Count); index++) {
                VisitLocal(local: offsets[index]);
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
                    VisitLocal(local: ((stepA * FixedQ4816.FromInteger(value: indexA)) + (stepB * FixedQ4816.FromInteger(value: indexB))));
                }
            }
        }

        void VisitLocal(FixedVector3 local) {
            Visit(
                local: local,
                reflectionNormal: null
            );

            if (planeNormal is { } reflectionNormal) {
                var signedDistance = (FixedVector3.Dot(
                    left: local,
                    right: reflectionNormal
                ) - planeOffset);

                Visit(
                    local: (local - (reflectionNormal * (signedDistance + signedDistance))),
                    reflectionNormal: reflectionNormal
                );
            }
        }
        void Visit(FixedVector3 local, FixedVector3? reflectionNormal) {
            visitor(obj: new FixedCreationStampInstance(
                Origin: (origin + rotation.Rotate(vector: local)),
                ReflectionNormal: reflectionNormal
            ));
        }
    }
    /// <summary>Visits pattern copies in A-major, then B-major order, followed immediately by each reflected copy.</summary>
    /// <param name="origin">The placement origin.</param>
    /// <param name="rotation">The placement rotation.</param>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    /// <param name="sampledOffsets">Precomputed placement-local offsets from a hash-sampled Noise/Scatter region
    /// (see <see cref="ForEachFixedInstance"/>), or <see langword="null"/> to use <paramref name="pattern"/>.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="visitor">Receives each materialized instance.</param>
    public static void ForEachInstance(Vector3 origin, Quaternion rotation, CreationStampPattern? pattern, IReadOnlyList<Vector3>? sampledOffsets, CreationStampPlane? mirror, Action<CreationStampInstance> visitor) {
        ArgumentNullException.ThrowIfNull(visitor);

        var plane = ((mirror is { } authoredPlane)
            ? new CreationStampPlane(
                Normal: Vector3.Normalize(value: authoredPlane.Normal),
                Offset: authoredPlane.Offset
            )
            : (CreationStampPlane?)null
        );

        if (sampledOffsets is { Count: > 0 } offsets) {
            for (var index = 0; (index < offsets.Count); index++) {
                VisitLocal(local: offsets[index]);
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
            var stepA = (pattern?.StepA ?? Vector3.Zero);
            var stepB = (pattern?.StepB ?? Vector3.Zero);

            for (var indexA = 0; (indexA < countA); indexA++) {
                for (var indexB = 0; (indexB < countB); indexB++) {
                    VisitLocal(local: ((stepA * indexA) + (stepB * indexB)));
                }
            }
        }

        void VisitLocal(Vector3 local) {
            Visit(
                local: local,
                reflectionNormal: null
            );

            if (plane is { } reflection) {
                var reflectedOrigin = (local - ((2f * (Vector3.Dot(
                    vector1: local,
                    vector2: reflection.Normal
                ) - reflection.Offset)) * reflection.Normal));

                Visit(
                    local: reflectedOrigin,
                    reflectionNormal: reflection.Normal
                );
            }
        }
        void Visit(Vector3 local, Vector3? reflectionNormal) {
            visitor(obj: new CreationStampInstance(
                Origin: (origin + Vector3.Transform(
                    rotation: rotation,
                    value: local
                )),
                ReflectionNormal: reflectionNormal
            ));
        }
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
            ? Math.Min(val1: sampled, val2: ceiling)
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
            ));

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
    public static long MultiplySaturated(long left, long right, long ceiling) {
        if (
            (left <= 0L) ||
            (right <= 0L)
        ) {
            return 0L;
        }

        return ((left > (ceiling / right))
            ? ceiling
            : Math.Min(
                val1: (left * right),
                val2: ceiling
            )
        );
    }
}
