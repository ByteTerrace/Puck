using System.Numerics;
using Puck.SdfVm;

namespace Puck.Forge.Authoring;

/// <summary>
/// The canonical geometry of the <c>puck.creation.v1</c> primitive vocabulary — the dimension table every consumer of
/// a creation's <see cref="ShapeDocument"/> emits through, so a shape renders byte-for-byte the same geometry in every
/// stamp, workbench, and bake regardless of which project draws it. This is the render-side sibling of the
/// <see cref="CreationCanonicalizer"/> hash contract: the hash pins a creation's DATA identity; this table pins what
/// that data MEANS as geometry. The canonical primitive dimension table. Changing any value changes the meaning of
/// every persisted creation — a schema-scale act. THIS table is the authority.
/// </summary>
public static class CreationGeometry {
    // The canonical unit-scale dimensions (a contract, not a preference: every persisted creation was authored and
    // previewed against exactly these numbers — see the type summary).
    private const float SphereRadius = 0.38f;
    private static readonly Vector3 BoxHalfExtents = new(x: 0.34f, y: 0.34f, z: 0.34f);
    private const float BoxRound = 0.04f;
    private const float CylinderHalfHeight = 0.36f;
    private const float CylinderRadius = 0.30f;
    private const float TorusMajor = 0.30f;
    private const float TorusMinor = 0.12f;
    private static readonly Vector3 CapsuleEndpoint = new(x: 0f, y: 0.55f, z: 0f);
    private const float CapsuleRadius = 0.20f;
    private static readonly Vector3 EllipsoidRadii = new(x: 0.42f, y: 0.28f, z: 0.34f);
    private const float RoundConeLowerRadius = 0.22f;
    private const float RoundConeHeight = 0.52f;
    private const float RoundConeUpperRadius = 0.05f;
    private const float ConeRadius = 0.30f;
    private const float ConeHalfHeight = 0.36f;

    /// <summary>Reads a primitive's local extent from the canonical dimension table.</summary>
    /// <param name="type">The primitive.</param>
    /// <returns>The finite local bounds, or the unbounded marker for <see cref="AvatarPrimitive.Plane"/>.</returns>
    public static CreationPrimitiveBounds GetLocalBounds(AvatarPrimitive type) {
        return type switch {
            AvatarPrimitive.Box => new(Center: Vector3.Zero, HalfExtents: (BoxHalfExtents + new Vector3(value: BoxRound))),
            AvatarPrimitive.Torus => new(Center: Vector3.Zero, HalfExtents: new Vector3(x: (TorusMajor + TorusMinor), y: TorusMinor, z: (TorusMajor + TorusMinor))),
            AvatarPrimitive.Cylinder => new(Center: Vector3.Zero, HalfExtents: new Vector3(x: CylinderRadius, y: CylinderHalfHeight, z: CylinderRadius)),
            AvatarPrimitive.Capsule => new(Center: Vector3.Zero, HalfExtents: new Vector3(x: CapsuleRadius, y: (CapsuleEndpoint.Y + CapsuleRadius), z: CapsuleRadius)),
            AvatarPrimitive.Ellipsoid => new(Center: Vector3.Zero, HalfExtents: EllipsoidRadii),
            AvatarPrimitive.RoundCone => new(
                Center: new Vector3(x: 0f, y: (((RoundConeHeight + RoundConeUpperRadius) - RoundConeLowerRadius) * 0.5f), z: 0f),
                HalfExtents: new Vector3(
                    x: MathF.Max(x: RoundConeLowerRadius, y: RoundConeUpperRadius),
                    y: ((RoundConeHeight + RoundConeUpperRadius + RoundConeLowerRadius) * 0.5f),
                    z: MathF.Max(x: RoundConeLowerRadius, y: RoundConeUpperRadius)
                )
            ),
            AvatarPrimitive.Cone => new(Center: Vector3.Zero, HalfExtents: new Vector3(x: ConeRadius, y: ConeHalfHeight, z: ConeRadius)),
            AvatarPrimitive.Sphere => new(Center: Vector3.Zero, HalfExtents: new Vector3(value: SphereRadius)),
            AvatarPrimitive.Plane => CreationPrimitiveBounds.Unbounded,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(type), actualValue: type, message: "The creation primitive is not defined."),
        };
    }

    /// <summary>Emits ONE primitive's shape instruction onto an already-transformed builder chain, using the canonical
    /// dimensions. The blend op and smooth radius ride the shape instruction itself (zero extra words).</summary>
    /// <param name="chain">The builder with the point transform (translate/rotate/scale or dynamic) already applied.</param>
    /// <param name="type">The primitive to emit.</param>
    /// <param name="material">The material id for the shape.</param>
    /// <param name="blend">How the shape combines with the field before it (default plain union).</param>
    /// <param name="smooth">The blend radius for the smooth variants (0 for the hard ops).</param>
    /// <returns>The builder, for chaining.</returns>
    public static SdfProgramBuilder AppendPrimitive(SdfProgramBuilder chain, AvatarPrimitive type, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        ArgumentNullException.ThrowIfNull(chain);

        return type switch {
            AvatarPrimitive.Box => chain.Box(halfExtents: BoxHalfExtents, round: BoxRound, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Torus => chain.Torus(majorRadius: TorusMajor, minorRadius: TorusMinor, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Cylinder => chain.Cylinder(radius: CylinderRadius, halfHeight: CylinderHalfHeight, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Capsule => chain.Capsule(endpoint: CapsuleEndpoint, radius: CapsuleRadius, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Ellipsoid => chain.Ellipsoid(radii: EllipsoidRadii, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.RoundCone => chain.RoundCone(lowerRadius: RoundConeLowerRadius, upperRadius: RoundConeUpperRadius, height: RoundConeHeight, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Plane => chain.Plane(normal: Vector3.UnitY, offset: 0f, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Cone => chain.Trapezoid(bottomHalfWidth: ConeRadius, topHalfWidth: 0f, halfHeight: ConeHalfHeight, lift: SdfLift.Revolve, liftAmount: 0f, material: material, blend: blend, smooth: smooth),
            AvatarPrimitive.Sphere => chain.Sphere(radius: SphereRadius, material: material, blend: blend, smooth: smooth),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(type), actualValue: type, message: "The creation primitive is not defined."),
        };
    }

    /// <summary>Emits a primitive at an authored per-axis scale, preferring a native distance spelling over the
    /// renderer-only non-uniform scale transform. Boxes bake their extents, spheres and ellipsoids bake their radii,
    /// and axially symmetric cylinders and cones bake their radial and vertical dimensions. A plane's zero set is
    /// scale-invariant. Other anisotropic shapes retain the generic transform for rendering, but physical field
    /// evaluators deliberately refuse that conservative march bound until the VM gains a native spelling.</summary>
    /// <param name="chain">The builder chain after translation and rotation.</param>
    /// <param name="type">The primitive to emit.</param>
    /// <param name="scale">The authored per-axis scale. Components use the builder's magnitude/nonzero convention.</param>
    /// <param name="material">The material id for the shape.</param>
    /// <param name="blend">How the shape combines with the field before it.</param>
    /// <param name="smooth">The blend radius for smooth composition.</param>
    /// <returns>The builder, for chaining.</returns>
    public static SdfProgramBuilder AppendScaledPrimitive(SdfProgramBuilder chain, AvatarPrimitive type, Vector3 scale,
        int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        ArgumentNullException.ThrowIfNull(chain);

        var effectiveScale = Vector3.Max(
            value1: Vector3.Abs(value: scale),
            value2: new Vector3(value: 0.0001f)
        );

        if ((effectiveScale.X == effectiveScale.Y) && (effectiveScale.Y == effectiveScale.Z)) {
            return AppendPrimitive(
                chain: chain.Scale(scale: effectiveScale),
                type: type,
                material: material,
                blend: blend,
                smooth: smooth
            );
        }

        var minimumScale = MathF.Min(x: effectiveScale.X, y: MathF.Min(x: effectiveScale.Y, y: effectiveScale.Z));
        var boxRound = (BoxRound * minimumScale);

        return type switch {
            AvatarPrimitive.Box => chain.Box(
                // Preserve the transformed box's axial zero-set extents, but use one conventional world-space
                // corner radius so the resulting field remains a true rounded-box distance.
                halfExtents: (((BoxHalfExtents + new Vector3(value: BoxRound)) * effectiveScale) - new Vector3(value: boxRound)),
                round: boxRound,
                material: material,
                blend: blend,
                smooth: smooth
            ),
            AvatarPrimitive.Sphere => chain.Ellipsoid(
                radii: (new Vector3(value: SphereRadius) * effectiveScale),
                material: material,
                blend: blend,
                smooth: smooth
            ),
            AvatarPrimitive.Ellipsoid => chain.Ellipsoid(
                radii: (EllipsoidRadii * effectiveScale),
                material: material,
                blend: blend,
                smooth: smooth
            ),
            AvatarPrimitive.Cylinder when effectiveScale.X == effectiveScale.Z => chain.Cylinder(
                radius: (CylinderRadius * effectiveScale.X),
                halfHeight: (CylinderHalfHeight * effectiveScale.Y),
                material: material,
                blend: blend,
                smooth: smooth
            ),
            AvatarPrimitive.Cone when effectiveScale.X == effectiveScale.Z => chain.Trapezoid(
                bottomHalfWidth: (ConeRadius * effectiveScale.X),
                topHalfWidth: 0f,
                halfHeight: (ConeHalfHeight * effectiveScale.Y),
                lift: SdfLift.Revolve,
                liftAmount: 0f,
                material: material,
                blend: blend,
                smooth: smooth
            ),
            AvatarPrimitive.Plane => chain.Plane(
                normal: Vector3.UnitY,
                offset: 0f,
                material: material,
                blend: blend,
                smooth: smooth
            ),
            _ => AppendPrimitive(
                chain: chain.Scale(scale: effectiveScale),
                type: type,
                material: material,
                blend: blend,
                smooth: smooth
            ),
        };
    }

    /// <summary>A primitive's worst-case reach from its local origin at a given scale — the largest scale component
    /// times the primitive's farthest surface point.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The shape's per-axis scale.</param>
    /// <returns>The reach in local units.</returns>
    public static float Reach(AvatarPrimitive type, Vector3 scale) {
        var maxScale = MathF.Max(x: scale.X, y: MathF.Max(x: scale.Y, y: scale.Z));
        var reach = type switch {
            AvatarPrimitive.Box => (BoxHalfExtents.Length() + BoxRound),
            AvatarPrimitive.Torus => (TorusMajor + TorusMinor),
            AvatarPrimitive.Cylinder => MathF.Sqrt(x: ((CylinderRadius * CylinderRadius) + (CylinderHalfHeight * CylinderHalfHeight))),
            AvatarPrimitive.Capsule => (CapsuleEndpoint.Length() + CapsuleRadius),
            AvatarPrimitive.Ellipsoid => MathF.Max(x: EllipsoidRadii.X, y: MathF.Max(x: EllipsoidRadii.Y, y: EllipsoidRadii.Z)),
            // Base at the local origin, tip up +Y: the farthest surface point is the rounded tip (height + tip radius).
            AvatarPrimitive.RoundCone => (RoundConeHeight + RoundConeUpperRadius),
            // SdfProgram classifies the containing instance as unmaskable and replaces this placeholder bound with its
            // always-tested sentinel after reading the emitted Plane instruction.
            AvatarPrimitive.Plane => 0f,
            AvatarPrimitive.Cone => MathF.Sqrt(x: ((ConeRadius * ConeRadius) + (ConeHalfHeight * ConeHalfHeight))),
            AvatarPrimitive.Sphere => SphereRadius,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(type), actualValue: type, message: "The creation primitive is not defined."),
        };

        return (reach * maxScale);
    }

    /// <summary>A whole creation's worst-case reach from its own local origin — the largest per-shape reach across
    /// every authored shape and text run, the instance bound a stamp of it needs (a masked-out tile must never clip a
    /// glyph that reaches past the boxes).</summary>
    /// <param name="document">The creation (normalized or not; absent lists read as empty).</param>
    /// <returns>The reach in creation-local units (a small floor for an empty document).</returns>
    public static float Reach(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var reach = 0f;
        var any = false;

        foreach (var shape in (document.Shapes ?? [])) {
            reach = MathF.Max(x: reach, y: (shape.Position.Length() + Reach(type: shape.Type, scale: shape.Scale)));
            any = true;
        }

        foreach (var run in (document.TextRuns ?? [])) {
            // A generous run reach: its anchor offset + half the run's world extent (~0.6 em per glyph advance) + the
            // relief depth. A fat bound only costs a rare extra evaluation; a too-tight one would cull real glyphs.
            var runReach = ((run.Position.Length() + ((0.6f * MathF.Max(x: run.EmHeight, y: 0.001f)) * MathF.Max(x: run.GlyphCount, y: 1))) + (run.Depth ?? 0.02f));

            reach = MathF.Max(x: reach, y: runReach);
            any = true;
        }

        return (any ? reach : 0.6f);
    }
}

/// <summary>A canonical primitive's local axis-aligned extent.</summary>
/// <param name="Center">The bound center in primitive-local coordinates.</param>
/// <param name="HalfExtents">The distances from <paramref name="Center"/> to the bound faces.</param>
/// <param name="IsUnbounded">Whether the primitive has no finite bound.</param>
public readonly record struct CreationPrimitiveBounds(Vector3 Center, Vector3 HalfExtents, bool IsUnbounded = false) {
    /// <summary>The extent marker for an unbounded primitive.</summary>
    public static CreationPrimitiveBounds Unbounded { get; } = new(Center: Vector3.Zero, HalfExtents: Vector3.Zero, IsUnbounded: true);
}
