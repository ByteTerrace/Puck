using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.Text;

namespace Puck.Forge.Authoring;

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
    public static void Emit(SdfProgramBuilder builder, CreationDocument document, CreationStampTransform transform, Func<ShapeDocument, int> materialFor, float? contactMargin = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(materialFor);

        foreach (var shape in (document.Shapes ?? [])) {
            var (shapePosition, shapeRotation) = ReflectedShapeTransform(
                shape: shape,
                normal: transform.ReflectionNormal
            );
            var shapeScale = EffectiveScale(value: shape.Scale);
            var chain = ShapeDomainOps.Apply(
                chain: builder
                    .ResetPoint()
                    .Translate(offset: transform.Origin)
                    .Rotate(rotation: transform.Rotation)
                    .Scale(scale: new Vector3(value: transform.Scale)),
                domain: shape.Domain
            )
                .Translate(offset: shapePosition)
                .Rotate(rotation: shapeRotation);

            var blend = (shape.Blend ?? SdfBlendOp.Union);
            var smooth = (shape.Smooth ?? 0f);

            if (
                (contactMargin is not { } margin) ||
                (margin == 0f)
            ) {
                _ = SdfSolidGeometry.AppendScaledPrimitive(
                    chain: chain,
                    type: shape.Type,
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
                type: shape.Type,
                scale: shapeScale,
                material: materialFor(arg: shape)
            ).Dilate(radius: margin);
            _ = chain.PopField();
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
                        type: shape.Type,
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
                    type: shape.Type,
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
    /// layout options. Shape geometry uses <see cref="SdfSolidGeometry.Reach(SdfSolidPrimitive, Vector3)"/>; text geometry
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

        var reach = 0f;
        var any = false;

        foreach (var shape in (document.Shapes ?? [])) {
            reach = MathF.Max(
                x: reach,
                y: ((shape.Position.Length() + SdfSolidGeometry.Reach(
                    type: shape.Type,
                    scale: shape.Scale
                )) * scale)
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
        var copies = (sampledCount is { } sampled
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
