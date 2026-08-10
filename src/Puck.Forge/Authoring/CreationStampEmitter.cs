using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;

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

/// <summary>One primitive copy after a stamp transform has been applied.</summary>
/// <param name="Shape">The authored shape.</param>
/// <param name="Center">The primitive's world-axis bound center.</param>
/// <param name="HalfExtents">The primitive's world-axis bound half-extents.</param>
/// <param name="UniformScale">The primitive's world scale when it is isotropic; zero otherwise.</param>
/// <param name="PlaneNormal">The unit world normal for an unbounded plane; zero for a finite primitive.</param>
public readonly record struct CreationStampPrimitiveCopy(ShapeDocument Shape, Vector3 Center, Vector3 HalfExtents, float UniformScale, Vector3 PlaneNormal);

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

    /// <summary>Whether a shape's effective per-axis scale is isotropic after the builder's magnitude and nonzero
    /// normalization.</summary>
    /// <param name="shape">The authored shape.</param>
    public static bool IsIsotropicallyScaled(ShapeDocument shape) {
        ArgumentNullException.ThrowIfNull(shape);

        var scale = EffectiveScale(value: shape.Scale);
        return ((scale.X == scale.Y) && (scale.Y == scale.Z));
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
            var (shapePosition, shapeRotation) = ReflectedShapeTransform(shape: shape, normal: transform.ReflectionNormal);
            var chain = builder
                .ResetPoint()
                .Translate(offset: transform.Origin)
                .Rotate(rotation: transform.Rotation)
                .Scale(scale: new Vector3(value: transform.Scale))
                .Translate(offset: shapePosition)
                .Rotate(rotation: shapeRotation)
                .Scale(scale: shape.Scale);

            var blend = (shape.Blend ?? SdfBlendOp.Union);
            var smooth = (shape.Smooth ?? 0f);

            if (contactMargin is not { } margin || (margin == 0f)) {
                _ = CreationGeometry.AppendPrimitive(chain: chain, type: shape.Type, material: materialFor(arg: shape), blend: blend, smooth: smooth);
                continue;
            }

            chain = CreationGeometry.AppendPrimitive(
                chain: chain.PushField(compose: blend, smooth: smooth),
                type: shape.Type,
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
        var stampScale = FixedQ4816.Max(x: FixedQ4816.Abs(value: transform.Scale), y: s_minimumTransformExtent);
        var reflectionNormal = transform.ReflectionNormal?.Normalize();

        foreach (var shape in (document.Shapes ?? [])) {
            var (shapePosition, shapeRotation) = ReflectedShapeTransformFixed(shape: shape, normal: reflectionNormal);
            var chain = builder
                .ResetPoint()
                .Translate(offset: transform.Origin.ToVector3())
                .Rotate(rotation: stampRotation)
                .Scale(scale: new Vector3(value: ((float)((double)stampScale))))
                .Translate(offset: shapePosition.ToVector3())
                .Rotate(rotation: shapeRotation)
                .Scale(scale: EffectiveFixedScale(value: shape.Scale).ToVector3());

            var blend = (shape.Blend ?? SdfBlendOp.Union);
            var smooth = (shape.Smooth ?? 0f);

            if (contactMargin is not { } margin || (margin == 0f)) {
                _ = CreationGeometry.AppendPrimitive(chain: chain, type: shape.Type, material: materialFor(arg: shape), blend: blend, smooth: smooth);
                continue;
            }

            chain = CreationGeometry.AppendPrimitive(
                chain: chain.PushField(compose: blend, smooth: smooth),
                type: shape.Type,
                material: materialFor(arg: shape)
            ).Dilate(radius: margin);
            _ = chain.PopField();
        }
    }

    /// <summary>Visits every primitive represented by one materialized stamp transform.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <param name="visitor">Receives world-axis bounds for each primitive.</param>
    public static void VisitPrimitiveCopies(CreationDocument document, CreationStampTransform transform, Action<CreationStampPrimitiveCopy> visitor) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(visitor);

        var stampRotation = Quaternion.Normalize(value: transform.Rotation);
        var stampScale = MathF.Max(x: MathF.Abs(transform.Scale), y: MinimumTransformExtent);

        foreach (var shape in (document.Shapes ?? [])) {
            var bounds = CreationGeometry.GetLocalBounds(type: shape.Type);
            var (shapePosition, reflectedRotation) = ReflectedShapeTransform(shape: shape, normal: transform.ReflectionNormal);
            var shapeRotation = Quaternion.Normalize(value: reflectedRotation);
            var shapeScale = EffectiveScale(value: shape.Scale);
            var localBoundsCenter = (shapePosition + Vector3.Transform(value: (bounds.Center * shapeScale), rotation: shapeRotation));
            var localAxisX = (Vector3.Transform(value: (Vector3.UnitX * shapeScale.X), rotation: shapeRotation) * stampScale);
            var localAxisY = (Vector3.Transform(value: (Vector3.UnitY * shapeScale.Y), rotation: shapeRotation) * stampScale);
            var localAxisZ = (Vector3.Transform(value: (Vector3.UnitZ * shapeScale.Z), rotation: shapeRotation) * stampScale);
            var axisX = Vector3.Transform(value: localAxisX, rotation: stampRotation);
            var axisY = Vector3.Transform(value: localAxisY, rotation: stampRotation);
            var axisZ = Vector3.Transform(value: localAxisZ, rotation: stampRotation);
            var worldCenter = (transform.Origin + Vector3.Transform(value: (localBoundsCenter * stampScale), rotation: stampRotation));
            var worldHalfExtents = new Vector3(
                x: ((MathF.Abs(axisX.X) * bounds.HalfExtents.X) + (MathF.Abs(axisY.X) * bounds.HalfExtents.Y) + (MathF.Abs(axisZ.X) * bounds.HalfExtents.Z)),
                y: ((MathF.Abs(axisX.Y) * bounds.HalfExtents.X) + (MathF.Abs(axisY.Y) * bounds.HalfExtents.Y) + (MathF.Abs(axisZ.Y) * bounds.HalfExtents.Z)),
                z: ((MathF.Abs(axisX.Z) * bounds.HalfExtents.X) + (MathF.Abs(axisY.Z) * bounds.HalfExtents.Y) + (MathF.Abs(axisZ.Z) * bounds.HalfExtents.Z))
            );
            var uniformScale = (IsIsotropicallyScaled(shape: shape) ? (stampScale * shapeScale.X) : 0f);
            var planeNormal = (bounds.IsUnbounded ? Vector3.Normalize(value: axisY) : Vector3.Zero);

            visitor(obj: new CreationStampPrimitiveCopy(Shape: shape, Center: worldCenter, HalfExtents: worldHalfExtents, UniformScale: uniformScale, PlaneNormal: planeNormal));
        }
    }

    /// <summary>Visits every primitive represented by one materialized stamp transform, computed entirely in fixed
    /// point.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="transform">The stamp transform.</param>
    /// <param name="visitor">Receives world-axis bounds for each primitive.</param>
    /// <remarks>
    /// <para>The deterministic counterpart to <see cref="VisitPrimitiveCopies"/>, for the callers whose output reaches
    /// SIMULATION STATE — the contact colliders. Both compute the same geometry; only the arithmetic domain differs,
    /// and that difference is the whole point. The single-precision body reaches <see cref="MathF.Sin"/> and
    /// <see cref="MathF.Cos"/> through its caller's <see cref="Quaternion.CreateFromAxisAngle"/>, and those route to
    /// the platform's own libm, which is not required to return the same bits on another operating system, another
    /// architecture, or another runtime version. A collider position is not presentation: it decides where a body
    /// stops. This body reaches only integer arithmetic.</para>
    /// <para>Two representation differences, both forced by the domain. Authored floats enter through
    /// <see cref="FixedVector3.FromVector3"/> and <see cref="FixedQuaternion.FromQuaternion"/> — the one door each, so
    /// the rounding is not a per-caller decision. And a rotation is carried as its three transformed unit axes rather
    /// than as a quaternion, which is what lets the reflected case skip the float path's
    /// matrix-to-quaternion-and-back round trip: that round trip exists only because its callers wanted a quaternion,
    /// and the frame it recovers is the one computed here directly.</para>
    /// <para>Rendering must keep calling <see cref="VisitPrimitiveCopies"/>: presentation floats sit outside the
    /// determinism contract by design, and per-frame fixed-point cost buys nothing there. The two bodies sit adjacent
    /// so that a change to what a creation's geometry MEANS is visibly owed to both — a divergence between what is
    /// drawn and what is collided is the failure this adjacency exists to prevent.</para>
    /// </remarks>
    public static void VisitFixedPrimitiveCopies(CreationDocument document, FixedCreationStampTransform transform, Action<FixedCreationStampPrimitiveCopy> visitor) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(visitor);

        var stampRotation = transform.Rotation.Normalize();
        var stampScale = FixedQ4816.Max(x: FixedQ4816.Abs(value: transform.Scale), y: s_minimumTransformExtent);
        var reflectionNormal = transform.ReflectionNormal?.Normalize();

        foreach (var shape in (document.Shapes ?? [])) {
            var bounds = CreationGeometry.GetLocalBounds(type: shape.Type);
            var boundsCenter = FixedVector3.FromVector3(value: bounds.Center);
            var boundsHalfExtents = FixedVector3.FromVector3(value: bounds.HalfExtents);
            var shapeScale = EffectiveFixedScale(value: shape.Scale);
            var (shapePosition, shapeAxisX, shapeAxisY, shapeAxisZ) = ReflectedShapeBasis(shape: shape, normal: reflectionNormal);
            // Vector3.Transform(bounds.Center * shapeScale, shapeRotation), written against the basis: a rotation
            // applied to a vector IS the scaled sum of its transformed unit axes.
            var localBoundsCenter = (shapePosition + (
                (shapeAxisX * (boundsCenter.X * shapeScale.X))
                + (shapeAxisY * (boundsCenter.Y * shapeScale.Y))
                + (shapeAxisZ * (boundsCenter.Z * shapeScale.Z))
            ));
            var localAxisX = ((shapeAxisX * shapeScale.X) * stampScale);
            var localAxisY = ((shapeAxisY * shapeScale.Y) * stampScale);
            var localAxisZ = ((shapeAxisZ * shapeScale.Z) * stampScale);
            var axisX = stampRotation.Rotate(vector: localAxisX);
            var axisY = stampRotation.Rotate(vector: localAxisY);
            var axisZ = stampRotation.Rotate(vector: localAxisZ);
            var worldCenter = (transform.Origin + stampRotation.Rotate(vector: (localBoundsCenter * stampScale)));
            var worldHalfExtents = new FixedVector3(
                X: (((FixedQ4816.Abs(value: axisX.X) * boundsHalfExtents.X) + (FixedQ4816.Abs(value: axisY.X) * boundsHalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.X) * boundsHalfExtents.Z)),
                Y: (((FixedQ4816.Abs(value: axisX.Y) * boundsHalfExtents.X) + (FixedQ4816.Abs(value: axisY.Y) * boundsHalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Y) * boundsHalfExtents.Z)),
                Z: (((FixedQ4816.Abs(value: axisX.Z) * boundsHalfExtents.X) + (FixedQ4816.Abs(value: axisY.Z) * boundsHalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Z) * boundsHalfExtents.Z))
            );
            var uniformScale = (IsIsotropicallyScaled(shape: shape) ? (stampScale * shapeScale.X) : FixedQ4816.Zero);
            var planeNormal = (bounds.IsUnbounded ? axisY.Normalize() : FixedVector3.Zero);

            visitor(obj: new FixedCreationStampPrimitiveCopy(Shape: shape, Center: worldCenter, HalfExtents: worldHalfExtents, UniformScale: uniformScale, PlaneNormal: planeNormal));
        }
    }

    private static Vector3 EffectiveScale(Vector3 value) => Vector3.Max(value1: Vector3.Abs(value: value), value2: new Vector3(value: MinimumTransformExtent));

    // The degeneracy floor in the fixed domain. Q48.16 resolves 1/65536, so 0.0001 lands on the nearest representable
    // value above it rather than exactly — this is a guard against a zero-scale axis collapsing the frame, never a
    // value a result is read off, so the quantization is immaterial. A product of two floors still underflows to zero,
    // which yields a zero-extent (inert) collider rather than the float path's vanishingly thin one.
    private static readonly FixedQ4816 s_minimumTransformExtent = FixedQ4816.FromDouble(value: MinimumTransformExtent);
    private static readonly FixedVector3 s_unitX = new(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_unitY = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_unitZ = new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);

    private static FixedVector3 EffectiveFixedScale(Vector3 value) =>
        new(
        X: FixedQ4816.Max(x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.X)), y: s_minimumTransformExtent),
        Y: FixedQ4816.Max(x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.Y)), y: s_minimumTransformExtent),
        Z: FixedQ4816.Max(x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value.Z)), y: s_minimumTransformExtent)
    );

    // The fixed-point ReflectedShapeTransform, carrying the frame itself instead of a quaternion. The unreflected
    // result is the shape rotation's three transformed unit axes; the reflected result mirrors all three and negates X
    // to restore a proper (determinant +1) frame — the same negation the float sibling applies, and for the same
    // reason: a mirrored basis is improper and no quaternion represents it.
    private static (FixedVector3 Position, FixedVector3 AxisX, FixedVector3 AxisY, FixedVector3 AxisZ) ReflectedShapeBasis(ShapeDocument shape, FixedVector3? normal) {
        var rotation = FixedQuaternion.FromQuaternion(value: shape.Rotation).Normalize();
        var axisX = rotation.Rotate(vector: s_unitX);
        var axisY = rotation.Rotate(vector: s_unitY);
        var axisZ = rotation.Rotate(vector: s_unitZ);
        var position = FixedVector3.FromVector3(value: shape.Position);

        if (normal is not { } unitNormal) {
            return (Position: position, AxisX: axisX, AxisY: axisY, AxisZ: axisZ);
        }

        return (
            Position: ReflectFixed(value: position, normal: unitNormal),
            AxisX: -ReflectFixed(value: axisX, normal: unitNormal),
            AxisY: ReflectFixed(value: axisY, normal: unitNormal),
            AxisZ: ReflectFixed(value: axisZ, normal: unitNormal)
        );
    }

    private static FixedVector3 ReflectFixed(FixedVector3 value, FixedVector3 normal) {
        var projection = FixedVector3.Dot(left: value, right: normal);

        return (value - (normal * (projection + projection)));
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
        var reflectedXAxis = rotation.Rotate(vector: s_unitX).Normalize();
        var reflectionPairVector = FixedVector3.Cross(left: reflectedXAxis, right: unitNormal);
        var reflectionPair = new FixedQuaternion(
            X: reflectionPairVector.X,
            Y: reflectionPairVector.Y,
            Z: reflectionPairVector.Z,
            W: FixedVector3.Dot(left: reflectedXAxis, right: unitNormal)
        ).Normalize();

        return (
            Position: ReflectFixed(value: position, normal: unitNormal),
            Rotation: (reflectionPair * rotation).Normalize()
        );
    }

    private static (Vector3 Position, Quaternion Rotation) ReflectedShapeTransform(ShapeDocument shape, Vector3? normal) {
        if (normal is not { } authoredNormal) {
            return (Position: shape.Position, Rotation: shape.Rotation);
        }

        var unitNormal = Vector3.Normalize(value: authoredNormal);
        var rotation = Quaternion.Normalize(value: shape.Rotation);
        var axisX = -ReflectVector(value: Vector3.Transform(value: Vector3.UnitX, rotation: rotation), normal: unitNormal);
        var axisY = ReflectVector(value: Vector3.Transform(value: Vector3.UnitY, rotation: rotation), normal: unitNormal);
        var axisZ = ReflectVector(value: Vector3.Transform(value: Vector3.UnitZ, rotation: rotation), normal: unitNormal);
        var reflectedRotation = Quaternion.Normalize(value: Quaternion.CreateFromRotationMatrix(matrix: new Matrix4x4(
            m11: axisX.X, m12: axisX.Y, m13: axisX.Z, m14: 0f,
            m21: axisY.X, m22: axisY.Y, m23: axisY.Z, m24: 0f,
            m31: axisZ.X, m32: axisZ.Y, m33: axisZ.Z, m34: 0f,
            m41: 0f, m42: 0f, m43: 0f, m44: 1f
        )));

        return (Position: ReflectVector(value: shape.Position, normal: unitNormal), Rotation: reflectedRotation);
    }

    private static Vector3 ReflectVector(Vector3 value, Vector3 normal) => (value - ((2f * Vector3.Dot(vector1: value, vector2: normal)) * normal));
}

/// <summary>Materializes the same placement pattern and reflected copies consumed by creation stamp emission.</summary>
public static class CreationStampLattice {
    /// <summary>Visits pattern copies in A-major, then B-major order, followed immediately by each reflected copy.</summary>
    /// <param name="origin">The placement origin.</param>
    /// <param name="rotation">The placement rotation.</param>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="visitor">Receives each materialized instance.</param>
    public static void ForEachInstance(Vector3 origin, Quaternion rotation, CreationStampPattern? pattern, CreationStampPlane? mirror, Action<CreationStampInstance> visitor) {
        ArgumentNullException.ThrowIfNull(visitor);

        var countA = Math.Max(val1: (pattern?.CountA ?? 1), val2: 1);
        var countB = Math.Max(val1: (pattern?.CountB ?? 1), val2: 1);
        var stepA = (pattern?.StepA ?? Vector3.Zero);
        var stepB = (pattern?.StepB ?? Vector3.Zero);
        var plane = mirror is { } authoredPlane
            ? new CreationStampPlane(Normal: Vector3.Normalize(value: authoredPlane.Normal), Offset: authoredPlane.Offset)
            : (CreationStampPlane?)null;

        for (var indexA = 0; (indexA < countA); indexA++) {
            for (var indexB = 0; (indexB < countB); indexB++) {
                var localOrigin = ((stepA * indexA) + (stepB * indexB));

                Visit(local: localOrigin, reflectionNormal: null);

                if (plane is { } reflection) {
                    var reflectedOrigin = (localOrigin - ((2f * (Vector3.Dot(vector1: localOrigin, vector2: reflection.Normal) - reflection.Offset)) * reflection.Normal));
                    Visit(local: reflectedOrigin, reflectionNormal: reflection.Normal);
                }
            }
        }

        void Visit(Vector3 local, Vector3? reflectionNormal) {
            visitor(obj: new CreationStampInstance(
                Origin: (origin + Vector3.Transform(value: local, rotation: rotation)),
                ReflectionNormal: reflectionNormal
            ));
        }
    }

    /// <summary>Visits pattern copies in A-major, then B-major order, followed immediately by each reflected copy —
    /// the deterministic counterpart to <see cref="ForEachInstance"/>, in the same order.</summary>
    /// <param name="origin">The placement origin.</param>
    /// <param name="rotation">The placement rotation.</param>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/> for one copy.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="visitor">Receives each materialized instance.</param>
    /// <remarks>The pattern and mirror are AUTHORED single-precision records, so they enter the contract through
    /// <see cref="FixedVector3.FromVector3"/> here rather than at each caller. The step accumulation is a scaled
    /// index rather than a running sum, exactly as the single-precision body computes it, so copy <c>n</c> does not
    /// inherit <c>n−1</c> roundings.</remarks>
    public static void ForEachFixedInstance(FixedVector3 origin, FixedQuaternion rotation, CreationStampPattern? pattern, CreationStampPlane? mirror, Action<FixedCreationStampInstance> visitor) {
        ArgumentNullException.ThrowIfNull(visitor);

        var countA = Math.Max(val1: (pattern?.CountA ?? 1), val2: 1);
        var countB = Math.Max(val1: (pattern?.CountB ?? 1), val2: 1);
        var stepA = FixedVector3.FromVector3(value: (pattern?.StepA ?? Vector3.Zero));
        var stepB = FixedVector3.FromVector3(value: (pattern?.StepB ?? Vector3.Zero));
        var planeNormal = (mirror is { } authoredPlane) ? FixedVector3.FromVector3(value: authoredPlane.Normal).Normalize() : (FixedVector3?)null;
        var planeOffset = FixedQ4816.FromDouble(value: (mirror?.Offset ?? 0f));

        for (var indexA = 0; (indexA < countA); indexA++) {
            for (var indexB = 0; (indexB < countB); indexB++) {
                var localOrigin = ((stepA * FixedQ4816.FromInteger(value: indexA)) + (stepB * FixedQ4816.FromInteger(value: indexB)));

                Visit(local: localOrigin, reflectionNormal: null);

                if (planeNormal is { } reflectionNormal) {
                    var signedDistance = (FixedVector3.Dot(left: localOrigin, right: reflectionNormal) - planeOffset);

                    Visit(local: (localOrigin - (reflectionNormal * (signedDistance + signedDistance))), reflectionNormal: reflectionNormal);
                }
            }
        }

        void Visit(FixedVector3 local, FixedVector3? reflectionNormal) {
            visitor(obj: new FixedCreationStampInstance(
                Origin: (origin + rotation.Rotate(vector: local)),
                ReflectionNormal: reflectionNormal
            ));
        }
    }

    /// <summary>Returns the number of materialized render instances.</summary>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/>.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    public static int InstanceCount(CreationStampPattern? pattern, CreationStampPlane? mirror) {
        var countA = Math.Max(val1: (pattern?.CountA ?? 1), val2: 1);
        var countB = Math.Max(val1: (pattern?.CountB ?? 1), val2: 1);
        var copies = checked(countA * countB);

        return (mirror is null ? copies : checked(copies * 2));
    }

    /// <summary>Returns the materialized pattern-and-mirror copy count, saturated at <paramref name="ceiling"/>.</summary>
    /// <param name="pattern">The pattern declaration, or <see langword="null"/>.</param>
    /// <param name="mirror">The authored local reflection plane, or <see langword="null"/>.</param>
    /// <param name="ceiling">The largest returned value.</param>
    public static long MaterializedCopyCount(CreationStampPattern? pattern, CreationStampPlane? mirror, long ceiling = long.MaxValue) {
        var countA = Math.Max(val1: (pattern?.CountA ?? 1), val2: 1);
        var countB = Math.Max(val1: (pattern?.CountB ?? 1), val2: 1);
        var copies = MultiplySaturated(left: countA, right: countB, ceiling: ceiling);

        return (mirror is null ? copies : MultiplySaturated(left: copies, right: 2L, ceiling: ceiling));
    }

    /// <summary>Multiplies non-negative counts and saturates at <paramref name="ceiling"/>.</summary>
    public static long MultiplySaturated(long left, long right, long ceiling) {
        if ((left <= 0L) || (right <= 0L)) {
            return 0L;
        }

        return ((left > (ceiling / right)) ? ceiling : Math.Min(val1: (left * right), val2: ceiling));
    }
}
