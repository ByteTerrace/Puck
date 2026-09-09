using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// The canonical geometry of the <see cref="SdfSolidPrimitive"/> vocabulary: the dimension table every consumer emits
/// through, so one primitive means one volume in every program, bound, and contact compile.
/// </summary>
/// <remarks>The unit-size law: an authored scale of <c>(1,1,1)</c> is the primitive's unit size and every other scale
/// reads as a direct multiple of it. Sphere r=1; Box half-extents (1,1,1); Capsule r=1, endpoint (0, 0.5, 0) —
/// <c>scale.y</c> is the cylindrical section's length and <c>scale.x</c>/<c>z</c> the radius, so total height is
/// 2·radius + length; Cylinder r=1, half-height 1; Cone base r=1, half-height 1 (apex radius 0); Ellipsoid radii
/// (1,1,1); RoundCone lower r=1, upper r=0.5, height 1; Torus major 1, minor 0.4. Changing a value here changes the
/// meaning of every persisted document that names the vocabulary.</remarks>
public static class SdfSolidGeometry {
    /// <summary>The smallest magnitude any per-axis scale component emits at: a component nearer zero than this is
    /// raised to it, so a shape authored flat still has a field.</summary>
    public const float MinimumScale = 0.0001f;

    private const float BoxRound = 0.04f;
    private const float CapsuleRadius = 1f;
    private const float ConeHalfHeight = 1f;
    private const float ConeRadius = 1f;
    private const float CylinderHalfHeight = 1f;
    private const float CylinderRadius = 1f;
    private const float RoundConeHeight = 1f;
    private const float RoundConeLowerRadius = 1f;
    private const float RoundConeUpperRadius = 0.5f;
    private const float SphereRadius = 1f;
    private const float TorusMajor = 1f;
    private const float TorusMinor = 0.4f;

    private static readonly Vector3 BoxHalfExtents = new(
        x: 1f,
        y: 1f,
        z: 1f
    );
    private static readonly Vector3 CapsuleEndpoint = new(
        x: 0f,
        y: 0.5f,
        z: 0f
    );
    private static readonly Vector3 EllipsoidRadii = new(
        x: 1f,
        y: 1f,
        z: 1f
    );

    private static Vector3 EffectiveScale(Vector3 scale) =>
        Vector3.Max(
            value1: Vector3.Abs(value: scale),
            value2: new Vector3(value: MinimumScale)
        );
    private static bool IsUniform(Vector3 scale) =>
        ((scale.X == scale.Y) && (scale.Y == scale.Z));

    /// <summary>Emits ONE primitive's shape instruction onto an already-transformed builder chain, using the canonical
    /// dimensions. The blend op and smooth radius ride the shape instruction itself (zero extra words).</summary>
    /// <param name="chain">The builder with the point transform (translate/rotate/scale or dynamic) already applied.</param>
    /// <param name="type">The primitive to emit.</param>
    /// <param name="material">The material id for the shape.</param>
    /// <param name="blend">How the shape combines with the field before it (default plain union).</param>
    /// <param name="smooth">The blend radius for the smooth variants (0 for the hard ops).</param>
    /// <returns>The builder, for chaining.</returns>
    public static SdfProgramBuilder AppendPrimitive(SdfProgramBuilder chain, SdfSolidPrimitive type, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        ArgumentNullException.ThrowIfNull(chain);

        return type switch {
            SdfSolidPrimitive.Box => chain.Box(
            blend: blend,
            halfExtents: BoxHalfExtents,
            material: material,
            round: BoxRound,
            smooth: smooth
        ),
            SdfSolidPrimitive.Torus => chain.Torus(
            blend: blend,
            majorRadius: TorusMajor,
            material: material,
            minorRadius: TorusMinor,
            smooth: smooth
        ),
            SdfSolidPrimitive.Cylinder => chain.Cylinder(
            blend: blend,
            halfHeight: CylinderHalfHeight,
            material: material,
            radius: CylinderRadius,
            smooth: smooth
        ),
            SdfSolidPrimitive.Capsule => chain.Capsule(
            blend: blend,
            endpoint: CapsuleEndpoint,
            material: material,
            radius: CapsuleRadius,
            smooth: smooth
        ),
            SdfSolidPrimitive.Ellipsoid => chain.Ellipsoid(
            blend: blend,
            material: material,
            radii: EllipsoidRadii,
            smooth: smooth
        ),
            SdfSolidPrimitive.RoundCone => chain.RoundCone(
            blend: blend,
            height: RoundConeHeight,
            lowerRadius: RoundConeLowerRadius,
            material: material,
            smooth: smooth,
            upperRadius: RoundConeUpperRadius
        ),
            SdfSolidPrimitive.Plane => chain.Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfSolidPrimitive.Cone => chain.Trapezoid(
            blend: blend,
            bottomHalfWidth: ConeRadius,
            halfHeight: ConeHalfHeight,
            lift: SdfLift.Revolve,
            liftAmount: 0f,
            material: material,
            smooth: smooth,
            topHalfWidth: 0f
        ),
            SdfSolidPrimitive.Sphere => chain.Sphere(
            blend: blend,
            material: material,
            radius: SphereRadius,
            smooth: smooth
        ),
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(type),
            actualValue: type,
            message: "The creation primitive is not defined."
        ),
        };
    }
    /// <summary>Emits a primitive at an authored per-axis scale, preferring a native distance spelling over the
    /// renderer-only non-uniform scale transform. Boxes bake their extents, spheres and ellipsoids bake their radii,
    /// and axially symmetric capsules, cylinders, and cones bake their radial and vertical dimensions. A plane's zero
    /// set is scale-invariant. Other anisotropic shapes retain the generic transform for rendering, but physical
    /// field evaluators deliberately refuse that conservative march bound until the VM gains a native spelling.</summary>
    /// <param name="chain">The builder chain after translation and rotation.</param>
    /// <param name="type">The primitive to emit.</param>
    /// <param name="scale">The authored per-axis scale. Components use the builder's magnitude/nonzero convention.</param>
    /// <param name="material">The material id for the shape.</param>
    /// <param name="blend">How the shape combines with the field before it.</param>
    /// <param name="smooth">The blend radius for smooth composition.</param>
    /// <returns>The builder, for chaining.</returns>
    public static SdfProgramBuilder AppendScaledPrimitive(SdfProgramBuilder chain, SdfSolidPrimitive type, Vector3 scale,
        int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
        ArgumentNullException.ThrowIfNull(chain);

        var effectiveScale = EffectiveScale(scale: scale);

        if (IsUniform(scale: effectiveScale)) {
            return AppendPrimitive(
                chain: chain.Scale(scale: effectiveScale),
                type: type,
                material: material,
                blend: blend,
                smooth: smooth
            );
        }

        var minimumScale = MathF.Min(
            x: effectiveScale.X,
            y: MathF.Min(
                x: effectiveScale.Y,
                y: effectiveScale.Z
            )
        );
        var boxRound = (BoxRound * minimumScale);

        return type switch {
            SdfSolidPrimitive.Box => chain.Box(
                // Preserve the transformed box's axial zero-set extents, but use one conventional world-space
                // corner radius so the resulting field remains a true rounded-box distance.
                halfExtents: (((BoxHalfExtents + new Vector3(value: BoxRound)) * effectiveScale) - new Vector3(value: boxRound)),
                round: boxRound,
                material: material,
                blend: blend,
                smooth: smooth
            ),
            SdfSolidPrimitive.Sphere => chain.Ellipsoid(
            radii: (new Vector3(value: SphereRadius) * effectiveScale),
            material: material,
            blend: blend,
            smooth: smooth
        ),
            SdfSolidPrimitive.Ellipsoid => chain.Ellipsoid(
            blend: blend,
            material: material,
            radii: (EllipsoidRadii * effectiveScale),
            smooth: smooth
        ),
            SdfSolidPrimitive.Capsule when (effectiveScale.X == effectiveScale.Z) => chain.Capsule(
            blend: blend,
            endpoint: new Vector3(
                x: 0f,
                y: (CapsuleEndpoint.Y * effectiveScale.Y),
                z: 0f
            ),
            material: material,
            radius: (CapsuleRadius * effectiveScale.X),
            smooth: smooth
        ),
            SdfSolidPrimitive.Cylinder when (effectiveScale.X == effectiveScale.Z) => chain.Cylinder(
            blend: blend,
            halfHeight: (CylinderHalfHeight * effectiveScale.Y),
            material: material,
            radius: (CylinderRadius * effectiveScale.X),
            smooth: smooth
        ),
            SdfSolidPrimitive.Cone when (effectiveScale.X == effectiveScale.Z) => chain.Trapezoid(
            blend: blend,
            bottomHalfWidth: (ConeRadius * effectiveScale.X),
            halfHeight: (ConeHalfHeight * effectiveScale.Y),
            lift: SdfLift.Revolve,
            liftAmount: 0f,
            material: material,
            smooth: smooth,
            topHalfWidth: 0f
        ),
            SdfSolidPrimitive.Plane => chain.Plane(
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
    /// <summary>Returns whether <see cref="AppendScaledPrimitive"/> can emit <paramref name="type"/> at
    /// <paramref name="scale"/>, and names what it cannot when it cannot.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The authored per-axis scale.</param>
    /// <param name="refusal">Empty when the scale emits; otherwise a noun phrase naming what the scale asks for.</param>
    /// <returns><see langword="true"/> when the scale emits.</returns>
    /// <remarks>The document-side door: an authoring path validates here so an authored scale is refused where it is
    /// read rather than throwing out of an emission the caller cannot recover from. KEEP IN SYNC with
    /// <see cref="AppendScaledPrimitive"/>'s branch structure — every arm that bakes an authored dimension into a
    /// shape rather than riding a <c>Scale</c> transform needs its shape's own admission rule answered here.</remarks>
    public static bool TryValidateScaledPrimitive(SdfSolidPrimitive type, Vector3 scale, out string refusal) {
        refusal = string.Empty;

        var effectiveScale = EffectiveScale(scale: scale);

        // A uniform scale rides one Scale transform over the unit primitive, so no authored dimension reaches a
        // shape's own admission rule; only the baked arms below can author a degenerate shape.
        if (IsUniform(scale: effectiveScale)) {
            return true;
        }

        if (
            (type != SdfSolidPrimitive.Cone) ||
            (effectiveScale.X != effectiveScale.Z)
        ) {
            return true;
        }

        var slant = new Vector2(
            x: (ConeRadius * effectiveScale.X),
            y: ((2f * ConeHalfHeight) * effectiveScale.Y)
        );

        if (slant.LengthSquared() < (SdfProgramBuilder.MinTrapezoidProfileSlant * SdfProgramBuilder.MinTrapezoidProfileSlant)) {
            refusal = $"a cone whose radial scale {effectiveScale.X} and axial scale {effectiveScale.Y} give it a {slant.Length()}-unit profile slant, under the {SdfProgramBuilder.MinTrapezoidProfileSlant} the deterministic fixed-point field evaluator can distinguish from a point";

            return false;
        }

        return true;
    }
    /// <summary>Reads a primitive's local extent from the canonical dimension table.</summary>
    /// <param name="type">The primitive.</param>
    /// <returns>The finite local bounds, or the unbounded marker for <see cref="SdfSolidPrimitive.Plane"/>.</returns>
    public static SdfSolidBounds GetLocalBounds(SdfSolidPrimitive type) {
        return type switch {
            SdfSolidPrimitive.Box => new(
            Center: Vector3.Zero,
            HalfExtents: (BoxHalfExtents + new Vector3(value: BoxRound))
        ),
            SdfSolidPrimitive.Torus => new(
            Center: Vector3.Zero,
            HalfExtents: new Vector3(
                x: (TorusMajor + TorusMinor),
                y: TorusMinor,
                z: (TorusMajor + TorusMinor)
            )
        ),
            SdfSolidPrimitive.Cylinder => new(
            Center: Vector3.Zero,
            HalfExtents: new Vector3(
                x: CylinderRadius,
                y: CylinderHalfHeight,
                z: CylinderRadius
            )
        ),
            SdfSolidPrimitive.Capsule => new(
            Center: Vector3.Zero,
            HalfExtents: new Vector3(
                x: CapsuleRadius,
                y: (CapsuleEndpoint.Y + CapsuleRadius),
                z: CapsuleRadius
            )
        ),
            SdfSolidPrimitive.Ellipsoid => new(
            Center: Vector3.Zero,
            HalfExtents: EllipsoidRadii
        ),
            SdfSolidPrimitive.RoundCone => new(
            Center: new Vector3(
                x: 0f,
                y: (((RoundConeHeight + RoundConeUpperRadius) - RoundConeLowerRadius) * 0.5f),
                z: 0f
            ),
            HalfExtents: new Vector3(
                x: MathF.Max(
                    x: RoundConeLowerRadius,
                    y: RoundConeUpperRadius
                ),
                y: (((RoundConeHeight + RoundConeUpperRadius) + RoundConeLowerRadius) * 0.5f),
                z: MathF.Max(
                    x: RoundConeLowerRadius,
                    y: RoundConeUpperRadius
                )
            )
        ),
            SdfSolidPrimitive.Cone => new(
            Center: Vector3.Zero,
            HalfExtents: new Vector3(
                x: ConeRadius,
                y: ConeHalfHeight,
                z: ConeRadius
            )
        ),
            SdfSolidPrimitive.Sphere => new(
            Center: Vector3.Zero,
            HalfExtents: new Vector3(value: SphereRadius)
        ),
            SdfSolidPrimitive.Plane => SdfSolidBounds.Unbounded,
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(type),
            actualValue: type,
            message: "The creation primitive is not defined."
        ),
        };
    }
    /// <summary>The march step factor <see cref="AppendScaledPrimitive"/>'s emission carries for a primitive at a scale:
    /// the eccentricity (largest radius over smallest) of the ellipsoid a non-uniformly scaled
    /// <see cref="SdfSolidPrimitive.Sphere"/> or an <see cref="SdfSolidPrimitive.Ellipsoid"/> bakes, and exactly 1 for
    /// every other arm. <c>SdfProgram.AnalyzeLipschitz</c> folds this same ratio into the program's step scale, so a
    /// stamper reads it here to decide whether a shape's chain needs its own field scope: a scoped eccentricity taxes
    /// only its own candidate's march, an unscoped one taxes every march in the frame.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The authored per-axis scale.</param>
    /// <returns>The step factor, at least 1.</returns>
    /// <remarks>KEEP IN SYNC with <see cref="AppendScaledPrimitive"/>'s Sphere and Ellipsoid arms and with
    /// <c>SdfProgram.EllipsoidEccentricity</c>: the radii read here are the ones those arms bake.</remarks>
    public static float StepFactor(SdfSolidPrimitive type, Vector3 scale) {
        var effectiveScale = EffectiveScale(scale: scale);
        Vector3 radii;

        switch (type) {
            case SdfSolidPrimitive.Sphere when !IsUniform(scale: effectiveScale): {
                    radii = (new Vector3(value: SphereRadius) * effectiveScale);
                    break;
                }
            case SdfSolidPrimitive.Ellipsoid: {
                    radii = (EllipsoidRadii * effectiveScale);
                    break;
                }
            default: {
                    return 1f;
                }
        }

        var largest = MathF.Max(
            x: radii.X,
            y: MathF.Max(
                x: radii.Y,
                y: radii.Z
            )
        );
        var smallest = MathF.Min(
            x: radii.X,
            y: MathF.Min(
                x: radii.Y,
                y: radii.Z
            )
        );

        return ((smallest > 0f)
            ? (largest / smallest)
            : 1f
        );
    }
    /// <summary>A primitive's worst-case reach from its local origin at a given scale — the largest scale component's
    /// magnitude times the primitive's farthest surface point.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The shape's per-axis scale.</param>
    /// <returns>The reach in local units.</returns>
    /// <remarks>Reads the same effective scale <see cref="AppendScaledPrimitive"/> emits at — each component's
    /// magnitude, raised to <see cref="MinimumScale"/>. A reach taken from the authored value instead disagrees with
    /// the emitted program wherever a component is signed or vanishing: every consumer folds reach into a running
    /// <c>Max</c> seeded at zero, so the cull bound collapses to its margin around geometry that is still there.</remarks>
    public static float Reach(SdfSolidPrimitive type, Vector3 scale) {
        var magnitude = EffectiveScale(scale: scale);
        var maxScale = MathF.Max(
            x: magnitude.X,
            y: MathF.Max(
                x: magnitude.Y,
                y: magnitude.Z
            )
        );
        var reach = type switch {
            SdfSolidPrimitive.Box => (BoxHalfExtents.Length() + BoxRound),
            SdfSolidPrimitive.Torus => (TorusMajor + TorusMinor),
            SdfSolidPrimitive.Cylinder => MathF.Sqrt(x: ((CylinderRadius * CylinderRadius) + (CylinderHalfHeight * CylinderHalfHeight))),
            SdfSolidPrimitive.Capsule => (CapsuleEndpoint.Length() + CapsuleRadius),
            SdfSolidPrimitive.Ellipsoid => MathF.Max(
            x: EllipsoidRadii.X,
            y: MathF.Max(
                x: EllipsoidRadii.Y,
                y: EllipsoidRadii.Z
            )
        ),
            // Base at the local origin, tip up +Y: the farthest surface point is the rounded tip (height + tip radius).
            SdfSolidPrimitive.RoundCone => (RoundConeHeight + RoundConeUpperRadius),
            // SdfProgram classifies the containing instance as unmaskable and replaces this placeholder bound with its
            // always-tested sentinel after reading the emitted Plane instruction.
            SdfSolidPrimitive.Plane => 0f,
            SdfSolidPrimitive.Cone => MathF.Sqrt(x: ((ConeRadius * ConeRadius) + (ConeHalfHeight * ConeHalfHeight))),
            SdfSolidPrimitive.Sphere => SphereRadius,
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(type),
            actualValue: type,
            message: "The creation primitive is not defined."
        ),
        };

        return (reach * maxScale);
    }
}
