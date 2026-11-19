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
/// <param name="Center">The primitive's bound center (<see cref="SdfSolidGeometry.LocalBox"/>), shared by its world-axis
/// and oriented bounds: its own origin, except up its Y for a primitive that reaches farther up than down (a baked
/// Capsule's segment midpoint, a RoundCone's).</param>
/// <param name="HalfExtents">The primitive's world-axis bound half-extents.</param>
/// <param name="UniformScale">The primitive's world scale when it is isotropic; zero otherwise.</param>
/// <param name="PlaneNormal">The unit world normal for an unbounded plane; zero for a finite primitive.</param>
/// <param name="Rotation">The unit rotation that turns the primitive's own axes into world space.</param>
/// <param name="LocalHalfExtents">The primitive's bound half-extents along its own axes, scaled: with
/// <paramref name="Rotation"/> and <paramref name="Center"/>, the oriented box that encloses it, never larger than
/// the world-axis bound.</param>
/// <param name="StampScale">The stamp's own uniform scale, exact: a shape dimension in creation units times it is that
/// dimension in world units. A quotient of a scaled extent by the shape's scale would carry the extent's rounding.</param>
public readonly record struct FixedCreationStampPrimitiveCopy(ShapeDocument Shape, FixedVector3 Center, FixedVector3 HalfExtents, FixedQ4816 UniformScale, FixedVector3 PlaneNormal, FixedQuaternion Rotation, FixedVector3 LocalHalfExtents, FixedQ4816 StampScale);
/// <summary>
/// Emits and expands <c>puck.creation.v1</c> shape geometry under one materialized stamp transform.
/// </summary>
public static partial class CreationStampEmitter {
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
    // A stamp's frame for ShapeEmission: every chain starts at the stamp's prefix, a relief samples the shape's stamped
    // rigid frame in world units, and a panel's or trim's palette slot resolves through the caller's palette or delegate.
    private readonly struct StampShapeFrame(StampFrame frame, Func<ShapeDocument, int>? materialFor, int[]? paletteIds) : IShapeEmissionFrame {
        public float FacetScale => frame.FacetScale;

        public SdfProgramBuilder Prefix(SdfProgramBuilder builder) => StampPrefix(builder: builder, frame: frame);
        public SdfProgramBuilder PrefixOf(SdfProgramBuilder builder, int shapeIndex) => StampPrefix(builder: builder, frame: frame);
        public SdfProgramBuilder Relief(SdfProgramBuilder builder, ShapeDocument shape) => ReliefChain(builder: builder, frame: frame, shape: shape);
        public int MaterialOf(ShapeDocument shape, int paletteSlot) => ((paletteIds is { } palette)
            ? palette[Math.Clamp(value: paletteSlot, max: (palette.Length - 1), min: 0)]
            : materialFor!(arg: (shape with { Material = paletteSlot })));
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
    // A primitive dilated by its contact margin inside the field scope the caller just opened, so the dilation applies
    // before the authored blend; pops that scope.
    private static void EmitMarginedPrimitive(SdfProgramBuilder chain, ShapeDocument shape, Vector3 shapeScale, int material, float margin, bool detail) =>
        _ = ShapeEmission.AppendPrimitive(
            chain: chain,
            shape: shape,
            scale: shapeScale,
            material: material,
            detail: detail
        )
            .Dilate(radius: margin)
            .PopField();
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

        if (frame.DynamicSlot is { } slot) { chain = chain.TransformDynamic(slot: slot); }
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
            if (run.Engraves) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Emits a creation's shape list under one materialized stamp transform.</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <param name="materialFor">Resolves each shape's material id; a panel's or trim's palette slot resolves through it
    /// as the shape carrying that slot.</param>
    /// <param name="inScope">Whether the caller already holds an open field scope around this emission (the
    /// whole-creation stamp of <see cref="RequiresScope"/>). The creation emitter shares this scope: a shape whose field
    /// op or warp would otherwise open its own scope then rides the caller's instead.</param>
    /// <param name="hidden">The shapes, by index, that emit nothing (a presentation's shape isolation), or
    /// <see langword="null"/> to emit every shape.</param>
    public static void Emit(SdfProgramBuilder builder, CreationDocument document, CreationStampTransform transform, Func<ShapeDocument, int> materialFor, bool inScope = false, IReadOnlyList<bool>? hidden = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(materialFor);

        var frame = new StampShapeFrame(frame: StampFrame.Of(transform: transform), materialFor: materialFor, paletteIds: null);
        var shapes = (document.Shapes ?? []);

        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];

            if ((hidden is not null) && hidden[index]) {
                continue;
            }

            ShapeEmission.Emit(builder: builder, frame: in frame, inScope: inScope, material: materialFor(arg: shape), shape: shape, shapes: shapes);
        }
    }
    /// <summary>Returns whether a shape reaches contact geometry: neither a detail shape, which never reaches the contact
    /// compiler, so it neither carves nor pads a collider, nor a <see cref="SdfSolidPrimitive.Sweep"/>, which is no
    /// closed solid a body can stand on (its field is exact enough to render, not a sound collider).</summary>
    /// <param name="shape">The shape.</param>
    /// <returns><see langword="true"/> when <see cref="EmitFixed"/> emits the shape.</returns>
    public static bool EmitsContact(ShapeDocument shape) => ((shape.Detail != true) && (shape.Type != SdfSolidPrimitive.Sweep));
    /// <summary>Returns whether a creation has any shape that reaches contact geometry, so its contact emission opens a
    /// scope with something in it.</summary>
    /// <param name="document">The creation.</param>
    /// <returns><see langword="true"/> when <see cref="EmitFixed"/> emits at least one shape.</returns>
    public static bool EmitsContact(CreationDocument document) => (document.Shapes ?? []).Any(predicate: shape => EmitsContact(shape: shape));
    /// <summary>Emits a creation's shape list under one materialized stamp transform, deriving every transform
    /// constant in deterministic fixed point before the SDF program's single-precision encoding boundary.</summary>
    /// <param name="builder">The target program builder.</param>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The fixed-point stamp transform.</param>
    /// <param name="materialFor">Resolves each shape's material id.</param>
    /// <param name="contactMargin">An optional per-shape signed contact margin. Null emits the raw render stream;
    /// a nonzero value scopes each primitive so dilation applies before its authored blend.</param>
    /// <remarks>This is the collision-field sibling of <see cref="Emit"/>: the same stamp prefix and mirror alignment,
    /// with every domain fold expanded to its rigid copies, twist and bend through the shared point chain, and the render-only facets (panels, trims, other warps, per-shape
    /// relief, text, detail shapes, sweeps) left out. The creation's noise relief is the caller's to emit inside its
    /// scope, through the fixed-point <c>EmitNoise</c>. A mirrored shape's orientation is composed from its two reflection
    /// planes as a fixed quaternion; it never visits <see cref="Matrix4x4"/>,
    /// <see cref="Quaternion.CreateFromRotationMatrix"/>, or a floating-point normalize.</remarks>
    public static void EmitFixed(SdfProgramBuilder builder, CreationDocument document, FixedCreationStampTransform transform, Func<ShapeDocument, int> materialFor, float? contactMargin = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(materialFor);

        var frame = StampFrame.Of(transform: transform);

        foreach (var shape in (document.Shapes ?? [])) {
            if (!EmitsContact(shape: shape)) {
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
            var smooth = ((shape.Smooth ?? 0f) * frame.FacetScale);

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

                chain = ShapePointOps.Apply(chain, twist: (shape.Twist ?? 0f), bend: (shape.Bend ?? 0f));

                if (
                    (contactMargin is not { } margin) ||
                    (margin == 0f)
                ) {
                    _ = ShapeEmission.AppendPrimitive(
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
    /// pair — an unscoped field op would displace the whole composed world field. Amplitude and wavelength are
    /// creation units, scaled by the stamp; the sampling point rides the stamp's frame.</remarks>
    public static void EmitNoise(SdfProgramBuilder builder, CreationNoiseDocument noise, CreationStampTransform transform) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(noise);

        EmitNoise(builder: builder, frame: StampFrame.Of(transform: transform), noise: noise);
    }
    /// <summary>Emits a creation's noise-relief facet under a fixed-point stamp transform: the contact sibling of
    /// <see cref="EmitNoise(SdfProgramBuilder, CreationNoiseDocument, CreationStampTransform)"/>, whose frame constants
    /// are derived in fixed point before the program's single-precision encoding, as <see cref="EmitFixed"/>'s are.</summary>
    /// <param name="builder">The target program builder, inside the stamp's open field scope.</param>
    /// <param name="noise">The creation's normalized noise facet.</param>
    /// <param name="transform">The fixed-point stamp transform.</param>
    public static void EmitNoise(SdfProgramBuilder builder, CreationNoiseDocument noise, FixedCreationStampTransform transform) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(noise);

        EmitNoise(builder: builder, frame: StampFrame.Of(transform: transform), noise: noise);
    }

    // The relief's masks, then the relief. Each mask is a seeded scope of its region's shapes under the same stamp
    // (SdfProgramBuilder.BeginReliefMask), its band in world units, so a mirrored, scaled or slot-carried copy carries
    // its mask with the shapes it masks. Render and contact both reach this one emission, so a body stands on the masked
    // relief the bake draws. A region shape takes material 0: its scope composes nothing, so no material of it wins.
    private static void EmitNoise(SdfProgramBuilder builder, in StampFrame frame, CreationNoiseDocument noise) {
        foreach (var mask in (noise.Masks ?? [])) {
            _ = builder.BeginReliefMask(
                fade: (mask.Fade * frame.FacetScale),
                inside: (mask.Inside ?? 0f),
                outside: (mask.Outside ?? 1f),
                start: ((mask.Start ?? 0f) * frame.FacetScale)
            );

            foreach (var shape in mask.Shapes) {
                var (position, rotation) = ReflectedShapeTransformFixed(normal: null, shape: shape);
                var posed = StampPrefix(builder: builder, frame: frame)
                    .Translate(offset: position.ToVector3())
                    .Rotate(rotation: rotation.ToQuaternion());

                _ = ShapeEmission.AppendPrimitive(
                    chain: ShapePointOps.Apply(chain: posed, elongate: shape.Elongate),
                    material: 0,
                    scale: EffectiveScale(value: shape.Scale).ToVector3(),
                    shape: shape
                );
            }

            _ = builder.EndReliefMask();
        }

        _ = StampPrefix(
            builder: builder,
            frame: frame
        )
            .NoiseDisplace(
            amplitude: (noise.Amplitude * frame.FacetScale),
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

        var frame = new StampShapeFrame(frame: StampFrame.Of(transform: transform), materialFor: null, paletteIds: paletteIds);

        ShapeEmission.Emit(builder: builder, frame: in frame, inScope: false, material: material, shape: document.Shapes![shapeIndex], shapes: document.Shapes);
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
                blend: (run.Engraves ? SdfBlendOp.Subtraction : SdfBlendOp.Union),
                extrudeHalfDepth: (run.Relief * transform.Scale),
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
    /// <see langword="null"/> lays every run out fresh.</param>
    /// <param name="includes">Whether the run at an index of <see cref="CreationDocument.TextRuns"/> is emitted;
    /// <see langword="null"/> emits every run.</param>
    public static void EmitTextDynamic(SdfProgramBuilder builder, CreationDocument document, int dynamicSlot, float scale, Func<string?, FontAtlas> fontFor, Func<TextRunDocument, int> materialFor, IReadOnlyList<TextLayoutResult>? textLayouts = null, Func<int, bool>? includes = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fontFor);
        ArgumentNullException.ThrowIfNull(materialFor);

        var runs = (document.TextRuns ?? []);

        for (var index = 0; (index < runs.Count); index++) {
            if ((includes is not null) && !includes(arg: index)) { continue; }
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
                blend: (run.Engraves ? SdfBlendOp.Subtraction : SdfBlendOp.Union),
                extrudeHalfDepth: (run.Relief * scale),
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
    /// its domain fold's displacement and its <see cref="WarpedPrimitiveReach"/>, grown by its dilation, onion and sine
    /// relief. The one per-shape reach every creation bound is built from (<see cref="RenderReach"/>,
    /// <see cref="CreationGeometry.Reach"/>, a pooled domain-bearing shape's instance bound).</summary>
    /// <param name="shape">The authored shape.</param>
    /// <returns>The reach in creation units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static float ShapeReach(ShapeDocument shape) {
        ArgumentNullException.ThrowIfNull(shape);

        // A domain fold carries the shape across its lattice, so the fold's displacement bound is charged beside the
        // shape's own position; Dilate/Onion and a sine relief move the outer surface outward by their own value.
        return (((((shape.Position.Length() + WarpedPrimitiveReach(shape: shape)) + ShapeDomainOps.Reach(domain: shape.Domain)) + (shape.Dilate ?? 0f)) + (shape.Onion ?? 0f)) + (shape.Displace?.Reach ?? 0f));
    }
    /// <summary>Measures how far one shape's primitive reaches from the shape's own origin, in creation units: its
    /// primitive's reach with a raised panel's rise (<see cref="ShapePanelDocument.Depth"/> negative) and its cells
    /// padding, carried through its warps (flare, shear, bumps, warp, elongation). A Sweep carries no
    /// <see cref="SdfSolidGeometry.Reach(SdfSolidPrimitive, Vector3, SdfLift, float)"/> unit-scale law (its own control
    /// points already carry creation-unit dimensions — see <see cref="SdfSolidPrimitive.Sweep"/>'s remarks), so its own
    /// uniform scale bakes on directly; a panel is refused on it.</summary>
    /// <param name="shape">The authored shape.</param>
    /// <returns>The reach in creation units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static float WarpedPrimitiveReach(ShapeDocument shape) {
        ArgumentNullException.ThrowIfNull(shape);

        var primitiveReach = ((shape.Type == SdfSolidPrimitive.Sweep)
            ? ((shape.Curve?.Reach() ?? 0f) * shape.Scale.X)
            : SdfSolidGeometry.Reach(
                type: shape.Type,
                scale: shape.Scale,
                lift: (shape.Lift ?? SdfLift.Extrude),
                panelRaise: ((shape.Panel is { Depth: < 0f } panel)
                ? -panel.Depth
                : 0f)
            )
        );

        // Inverse warp bounds compose in reverse point-evaluation order (bumps, shear, profile).
        return ShapeWarpReach.Expand(
            (primitiveReach + (shape.Cells?.PrimitiveReachPadding(
                shape.Scale,
                shape.Flare
            ) ?? 0f)),
            shape.Flare,
            shape.Shear,
            ShapeWarpReach.PointReach(shape: shape)
        );
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

        // Noise relief grows the composed surface outward by up to its scaled amplitude, so the
        // bound charges it once up front.
        var reach = ((document.Noise?.Amplitude ?? 0f) * scale);
        var any = false;
        var shapes = (document.Shapes ?? []);

        for (var shapeIndex = 0; (shapeIndex < shapes.Count); shapeIndex++) {
            var shape = shapes[shapeIndex];
            // The shape and its relief share creation units and scale together.
            var shapeReach = ((ShapeReach(shape: shape) + (document.Noise?.Amplitude ?? 0f)) * scale);

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
        var depth = (MathF.Abs(x: run.Relief) * scale);
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
            if (runs[index].Engraves) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Measures one shape's world-space cull bound under the stamp transform: its reflected, stamped
    /// position and its primitive reach — the tight per-shape sibling of <see cref="RenderReach"/>. Only sound for a
    /// DOMAIN-FREE shape (a fold's copies leave any shape-local sphere); callers keep domain-bearing shapes on the
    /// whole-creation bound. A raised panel (<see cref="ShapePanelDocument.Depth"/> negative) widens the bound by
    /// <c>|Depth|</c>; a recess needs no widening (subtraction only removes). <see cref="ShapeDocument.Dilate"/>,
    /// <see cref="ShapeDocument.Onion"/> and a sine relief (<see cref="ShapeDocument.Displace"/>) move the outer
    /// surface outward by their own creation-unit value, scaled the same way; the smooth-blend halo is the packer's own
    /// concern (<see cref="RequiresScope"/>).</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="shapeIndex">The shape's index in <see cref="CreationDocument.Shapes"/>.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <returns>The bound center (world space) and radius.</returns>
    public static (Vector3 Center, float Radius) ShapeStampBound(CreationDocument document, int shapeIndex, CreationStampTransform transform) {
        ArgumentNullException.ThrowIfNull(document);

        var shape = document.Shapes![shapeIndex];
        var frame = StampFrame.Of(transform: transform);
        // The position term is untouched by the warps, which all apply after the shape's own translate.
        var warpedReach = WarpedPrimitiveReach(shape: shape);

        return (
            StampedPosition(
                frame: frame,
                local: MirroredShapePosition(
                    frame: frame,
                    shape: shape
                )
            ).ToVector3(),
            ((((warpedReach + (shape.Dilate ?? 0f)) + (shape.Onion ?? 0f)) + (shape.Displace?.Reach ?? 0f)) * transform.Scale)
        );
    }
    /// <summary>Measures one shape's world-axis box under the stamp transform: its primitive's local box
    /// (<see cref="SdfSolidGeometry.LocalBox"/>) at its scale, turned and placed as the stamp places it, widened by
    /// a raised panel, <see cref="ShapeDocument.Dilate"/> and <see cref="ShapeDocument.Onion"/> as
    /// <see cref="ShapeStampBound"/> widens its sphere. For a long, thin shape it is far tighter than that sphere's
    /// cube.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="shapeIndex">The shape's index in <see cref="CreationDocument.Shapes"/>.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <returns>The box's world-space corners; <see langword="null"/> for a shape the box does not hold: one carrying a
    /// domain fold, a warp, a twist, a bend, an elongation, cellular relief or a sweep, or an unbounded primitive.</returns>
    public static (Vector3 Low, Vector3 High)? ShapeStampBox(CreationDocument document, int shapeIndex, CreationStampTransform transform) {
        ArgumentNullException.ThrowIfNull(document);

        var shape = document.Shapes![shapeIndex];

        if ((shape.Domain is { Count: > 0 }) || (shape.Type == SdfSolidPrimitive.Sweep) || ShapePointOps.NeedsScope(shape: shape) || (shape.Elongate is not null)) {
            return null;
        }

        var bounds = SdfSolidGeometry.LocalBox(lift: (shape.Lift ?? SdfLift.Extrude), scale: shape.Scale, type: shape.Type);

        if (bounds.IsUnbounded) {
            return null;
        }

        var frame = StampFrame.Of(transform: transform);

        var (position, rotation) = ReflectedShapeTransformFixed(normal: frame.ReflectionNormal, shape: shape);
        var turn = (frame.Rotation * rotation).Normalize().ToQuaternion();
        var axisX = Vector3.Transform(value: (Vector3.UnitX * transform.Scale), rotation: turn);
        var axisY = Vector3.Transform(value: (Vector3.UnitY * transform.Scale), rotation: turn);
        var axisZ = Vector3.Transform(value: (Vector3.UnitZ * transform.Scale), rotation: turn);
        var center = (StampedPosition(frame: frame, local: position).ToVector3() + (((axisX * bounds.Center.X) + (axisY * bounds.Center.Y)) + (axisZ * bounds.Center.Z)));
        var raise = ((shape.Panel is { Depth: < 0f } panel) ? -panel.Depth : 0f);
        var widen = (((raise + (shape.Dilate ?? 0f)) + (shape.Onion ?? 0f)) * transform.Scale);
        var half = (VectorFunctions.OrientedHalfExtent(axisX: axisX, axisY: axisY, axisZ: axisZ, halfExtent: bounds.HalfExtents) + new Vector3(value: widen));

        return ((center - half), (center + half));
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

            if (!EmitsContact(shape: shape)) {
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

            // The shape's own box at its scale (SdfSolidGeometry.LocalBox, symmetric across local X, so a mirrored copy
            // keeps it), in its scaled local units: a revolve's radial offset and a baked capsule's segment included.
            var bounds = SdfSolidGeometry.LocalBox(lift: (shape.Lift ?? SdfLift.Extrude), scale: shape.Scale, type: shape.Type);

            if (!bounds.IsUnbounded && ShapePointOps.DeformsContact(shape: shape)) {
                // Twist and bend preserve distance from the shape origin, including for an off-centre primitive.
                // Its straight box does not enclose the rotated surface; a sphere's enclosing cube does.
                var radius = MathF.BitIncrement(x: (Vector3.Abs(value: bounds.Center) + bounds.HalfExtents).Length());

                bounds = new SdfSolidBounds(Center: Vector3.Zero, HalfExtents: new Vector3(value: radius));
            }
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
                    ((shapeAxisX * boundsCenter.X)
                    + (shapeAxisY * boundsCenter.Y))
                    + (shapeAxisZ * boundsCenter.Z)
                ));
                var axisX = stampRotation.Rotate(vector: (shapeAxisX * stampScale));
                var axisY = stampRotation.Rotate(vector: (shapeAxisY * stampScale));
                var axisZ = stampRotation.Rotate(vector: (shapeAxisZ * stampScale));

                visitor(obj: new FixedCreationStampPrimitiveCopy(
                    Center: (transform.Origin + stampRotation.Rotate(vector: (localBoundsCenter * stampScale))),
                    HalfExtents: FixedAabb.OrientedHalfExtent(axisX: axisX, axisY: axisY, axisZ: axisZ, halfExtent: boundsHalfExtents),
                    LocalHalfExtents: (boundsHalfExtents * stampScale),
                    PlaneNormal: (bounds.IsUnbounded
                    ? axisY.Normalize()
                    : FixedVector3.Zero),
                    Rotation: (stampRotation * placed.Rotation).Normalize(),
                    Shape: shape,
                    UniformScale: uniformScale,
                    StampScale: stampScale
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
    /// <summary>Returns a pattern's copy count along each step: the declared count, at least one, and one for no
    /// pattern. The one definition of a grid's extent, which the instance walk, the counts and the placement deal
    /// all read.</summary>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    public static (int CountA, int CountB) GridCounts(CreationStampPattern? pattern) => (
        Math.Max(
            val1: (pattern?.CountA ?? 1),
            val2: 1
        ),
        Math.Max(
            val1: (pattern?.CountB ?? 1),
            val2: 1
        )
    );
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
            var (countA, countB) = GridCounts(pattern: pattern);
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
        var (countA, countB) = GridCounts(pattern: pattern);
        var copies = (sampledCount ?? checked((countA * countB)));

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
        var (countA, countB) = GridCounts(pattern: pattern);
        var copies = ((sampledCount is { } sampled)
            ? Math.Min(
                val1: sampled,
                val2: ceiling
            )
            : MultiplySaturated(
                ceiling: ceiling,
                left: countA,
                right: countB
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
