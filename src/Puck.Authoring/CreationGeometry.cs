using System.Numerics;
using Puck.SdfVm;

namespace Puck.Authoring;

/// <summary>
/// The canonical geometry of the <c>puck.creation.v1</c> primitive vocabulary — the dimension table every consumer of
/// a creation's <see cref="ShapeDocument"/> emits through, so a shape renders byte-for-byte the same geometry in every
/// stamp, workbench, and bake regardless of which project draws it. This is the render-side sibling of the
/// <see cref="CreationCanonicalizer"/> hash contract: the hash pins a creation's DATA identity; this table pins what
/// that data MEANS as geometry. Values are copied value-for-value from the Demo authoring reference (the behavioral
/// oracle) — changing one changes the meaning of every persisted creation and is a schema-scale act.
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
            _ => chain.Sphere(radius: SphereRadius, material: material, blend: blend, smooth: smooth),
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
            _ => SphereRadius,
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
