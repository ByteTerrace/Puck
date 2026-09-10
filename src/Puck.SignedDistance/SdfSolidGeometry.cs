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
/// (1,1,1); RoundCone lower r=1, upper r=0.5, height 1; Torus major 1, minor 0.4; Prism bottom half-width,
/// half-height and extrusion half-depth 1, top half-width given by taper (default 0.5). Changing a value here changes the
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
    /// <param name="taper">Prism top width divided by bottom width, finite in [0, 1]; ignored by other primitives.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <param name="profile">Optional Prism cross-section; null is the trapezoid.</param>
    /// <param name="lift">Prism only: how the profile becomes a solid — extruded along Z, or revolved about Y at the
    /// unit radial offset. Ignored by every other primitive.</param>
    /// <param name="rounding">The edge-rounding radius, in the primitive's own unit space; admitted on Prism and
    /// Cylinder (a Cone's sharp apex has no room, so its ceiling is zero) and ignored elsewhere. Clamped by
    /// <see cref="SdfProgramBuilder.ClampRounding"/>.</param>
    /// <param name="chamfer">The 45-degree edge-chamfer radius, in the primitive's own unit space; admitted on Box,
    /// Cylinder, and Prism (an extrude only — a revolve has no cap seam to bevel) and ignored elsewhere. A Box or
    /// Cylinder with a positive chamfer emits <see cref="SdfProgramBuilder.ChamferedRectangle"/> in place of its
    /// native shape; a Prism's chamfer bevels its profile's cap rims instead (see
    /// <see cref="SdfProgramBuilder.Trapezoid"/>/<see cref="SdfProgramBuilder.RoundedRectangle"/>'s
    /// <c>capChamfer</c>). Clamped by <see cref="MaxChamfer"/>.</param>
    /// <param name="detail">Whether the emitted shape instruction is SHADING-ONLY (<see cref="SdfInstruction.Detail"/>)
    /// — skipped by every march/step-bound consumer and included only at an already-found hit.</param>
    /// <param name="exponent">Superellipsoid only: the generalizing exponent; ignored by every other
    /// primitive.</param>
    public static SdfProgramBuilder AppendPrimitive(SdfProgramBuilder chain, SdfSolidPrimitive type, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float taper = 0.5f, SdfPrismProfile? profile = null, SdfLift lift = SdfLift.Extrude, float rounding = 0f, float chamfer = 0f, bool detail = false, float exponent = SdfProgramBuilder.MinSuperellipsoidExponent) {
        ArgumentNullException.ThrowIfNull(chain);
        if (profile is not null && (type != SdfSolidPrimitive.Prism || !profile.IsValid())) { throw new ArgumentOutOfRangeException(nameof(profile)); }

        if (type == SdfSolidPrimitive.Prism && profile is { Kind: not SdfPrismProfileKind.Trapezoid }) {
            return AppendProfile(chain, Vector3.One, profile, material, blend, smooth, lift, rounding, detail);
        }
        if (type == SdfSolidPrimitive.Prism) {
            ArgumentOutOfRangeException.ThrowIfLessThan(taper, 0f);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(taper, 1f);
            if (!float.IsFinite(taper)) { throw new ArgumentOutOfRangeException(nameof(taper)); }
            return chain.Trapezoid(1f, taper, 1f, lift, 1f, material, blend, smooth, rounding, capChamfer: chamfer, detail: detail);
        }
        if (type == SdfSolidPrimitive.Box && chamfer > 0f) {
            // The plain Box arm's zero set is BoxHalfExtents with a BoxRound fillet; the chamfered Box keeps that outer
            // extent and that fillet (the family's rounding lane, applied on top of the chamfer) so a chamfer going to
            // zero lands on the plain Box and the two share one collider.
            return chain.ChamferedRectangle(
                blend: blend,
                chamfer: chamfer,
                detail: detail,
                halfHeight: BoxHalfExtents.Y,
                halfWidth: BoxHalfExtents.X,
                lift: SdfLift.Extrude,
                liftAmount: BoxHalfExtents.Z,
                material: material,
                rounding: BoxRound,
                smooth: smooth
            );
        }
        if (type == SdfSolidPrimitive.Cylinder && chamfer > 0f) {
            return chain.ChamferedRectangle(
                blend: blend,
                chamfer: chamfer,
                detail: detail,
                halfHeight: CylinderHalfHeight,
                halfWidth: CylinderRadius,
                lift: SdfLift.Revolve,
                liftAmount: 0f,
                material: material,
                smooth: smooth
            );
        }
        return type switch {
            SdfSolidPrimitive.Box => chain.Box(
            blend: blend,
            detail: detail,
            halfExtents: BoxHalfExtents,
            material: material,
            round: BoxRound,
            smooth: smooth
        ),
            SdfSolidPrimitive.Torus => chain.Torus(
            blend: blend,
            detail: detail,
            majorRadius: TorusMajor,
            material: material,
            minorRadius: TorusMinor,
            smooth: smooth
        ),
            SdfSolidPrimitive.Cylinder => chain.Cylinder(
            blend: blend,
            detail: detail,
            halfHeight: CylinderHalfHeight,
            material: material,
            radius: CylinderRadius,
            rounding: rounding,
            smooth: smooth
        ),
            SdfSolidPrimitive.Capsule => chain.Capsule(
            blend: blend,
            detail: detail,
            endpoint: CapsuleEndpoint,
            material: material,
            radius: CapsuleRadius,
            smooth: smooth
        ),
            SdfSolidPrimitive.Ellipsoid => chain.Ellipsoid(
            blend: blend,
            detail: detail,
            material: material,
            radii: EllipsoidRadii,
            smooth: smooth
        ),
            SdfSolidPrimitive.Superellipsoid => chain.Superellipsoid(
            blend: blend,
            detail: detail,
            exponent: exponent,
            material: material,
            radii: EllipsoidRadii,
            smooth: smooth
        ),
            SdfSolidPrimitive.RoundCone => chain.RoundCone(
            blend: blend,
            detail: detail,
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
            detail: detail,
            smooth: smooth
        ),
            SdfSolidPrimitive.Cone => chain.Trapezoid(
            blend: blend,
            bottomHalfWidth: ConeRadius,
            detail: detail,
            halfHeight: ConeHalfHeight,
            lift: SdfLift.Revolve,
            liftAmount: 0f,
            material: material,
            rounding: rounding,
            smooth: smooth,
            topHalfWidth: 0f
        ),
            SdfSolidPrimitive.Sphere => chain.Sphere(
            blend: blend,
            detail: detail,
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
    /// <param name="taper">Prism top width divided by bottom width, finite in [0, 1]; ignored by other primitives.</param>
    /// <param name="profile">Optional Prism cross-section; null uses the trapezoid.</param>
    /// <param name="lift">Prism only: extrude the profile along Z to <c>scale.z</c>, or revolve it about Y with
    /// <c>scale.z</c> as the radial offset (zero gives a solid of revolution). Ignored by every other
    /// primitive.</param>
    /// <param name="rounding">The edge-rounding radius in world units; admitted on Prism and Cylinder (a Cone's
    /// ceiling is zero) and ignored elsewhere. An emission riding a <c>Scale</c> transform divides it by the largest scale component, so
    /// the local radius is the conservative one. Clamped by <see cref="SdfProgramBuilder.ClampRounding"/>.</param>
    /// <param name="chamfer">The 45-degree edge-chamfer radius in world units; admitted on Box, Cylinder, and Prism
    /// (an extrude only) and ignored elsewhere. Clamped by <see cref="MaxChamfer"/>; scales the same way
    /// <paramref name="rounding"/> does.</param>
    /// <param name="detail">Whether the emitted shape instruction is SHADING-ONLY (<see cref="SdfInstruction.Detail"/>)
    /// — skipped by every march/step-bound consumer and included only at an already-found hit.</param>
    /// <param name="exponent">Superellipsoid only: the generalizing exponent; ignored by every other
    /// primitive.</param>
    public static SdfProgramBuilder AppendScaledPrimitive(SdfProgramBuilder chain, SdfSolidPrimitive type, Vector3 scale,
        int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float taper = 0.5f, SdfPrismProfile? profile = null,
        SdfLift lift = SdfLift.Extrude, float rounding = 0f, float chamfer = 0f, bool detail = false, float exponent = SdfProgramBuilder.MinSuperellipsoidExponent) {
        ArgumentNullException.ThrowIfNull(chain);
        if (profile is not null && (type != SdfSolidPrimitive.Prism || !profile.IsValid())) { throw new ArgumentOutOfRangeException(nameof(profile)); }

        var effectiveScale = EffectiveScale(scale: scale);

        if (type == SdfSolidPrimitive.Prism && profile is { Kind: not SdfPrismProfileKind.Trapezoid }) {
            return AppendProfile(chain, effectiveScale, profile, material, blend, smooth, lift, rounding, detail);
        }

        // A revolved prism reads scale.z as a radial offset rather than an extent, so it can never ride the uniform
        // Scale transform below (which would multiply that offset a second time); it emits its dimensions natively.
        // A revolve has no cap seam, so chamfer (a cap-only bevel here) never reaches it — MaxChamfer's ceiling is
        // zero for this combination and admission is refused upstream of emission.
        if ((type == SdfSolidPrimitive.Prism) && (lift == SdfLift.Revolve)) {
            RequireTaper(taper: taper);

            return chain.Trapezoid(effectiveScale.X, (effectiveScale.X * taper), effectiveScale.Y,
                SdfLift.Revolve, MathF.Abs(x: scale.Z), material, blend, smooth, rounding, detail: detail);
        }

        if (IsUniform(scale: effectiveScale)) {
            return AppendPrimitive(
                chain: chain.Scale(scale: effectiveScale),
                type: type,
                material: material,
                blend: blend,
                smooth: smooth,
                taper: taper,
                profile: profile,
                lift: lift,
                rounding: (rounding / effectiveScale.X),
                chamfer: (chamfer / effectiveScale.X),
                detail: detail,
                exponent: exponent
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

        if (type == SdfSolidPrimitive.Prism) {
            RequireTaper(taper: taper);

            return chain.Trapezoid(effectiveScale.X, effectiveScale.X * taper, effectiveScale.Y,
                SdfLift.Extrude, effectiveScale.Z, material, blend, smooth, rounding, capChamfer: chamfer, detail: detail);
        }
        // The plain Box arm preserves the transformed box's axial zero-set extents under one conventional world-space
        // corner radius, so the field stays a true rounded-box distance; the chamfered arm keeps the same zero set and
        // the same fillet (riding the family's rounding lane on top of the chamfer), so the two share one collider and
        // a chamfer going to zero lands on the plain Box.
        var boxExtents = (((BoxHalfExtents + new Vector3(value: BoxRound)) * effectiveScale) - new Vector3(value: boxRound));

        if (type == SdfSolidPrimitive.Box && chamfer > 0f) {
            return chain.ChamferedRectangle(
                blend: blend,
                chamfer: chamfer,
                detail: detail,
                halfHeight: boxExtents.Y,
                halfWidth: boxExtents.X,
                lift: SdfLift.Extrude,
                liftAmount: boxExtents.Z,
                material: material,
                rounding: boxRound,
                smooth: smooth
            );
        }
        return type switch {
            SdfSolidPrimitive.Box => chain.Box(
                halfExtents: boxExtents,
                round: boxRound,
                material: material,
                blend: blend,
                detail: detail,
                smooth: smooth
            ),
            SdfSolidPrimitive.Sphere => chain.Ellipsoid(
            radii: (new Vector3(value: SphereRadius) * effectiveScale),
            material: material,
            blend: blend,
            detail: detail,
            smooth: smooth
        ),
            SdfSolidPrimitive.Ellipsoid => chain.Ellipsoid(
            blend: blend,
            detail: detail,
            material: material,
            radii: (EllipsoidRadii * effectiveScale),
            smooth: smooth
        ),
            // Bakes anisotropy natively (like Ellipsoid immediately above), never riding the generic Scale-wrapped
            // fallback below: the exponent's 1-Lipschitz proof (SdfProgramBuilder.Superellipsoid) is stated against
            // the native per-axis-radii formula, not a uniformly scaled unit shape.
            SdfSolidPrimitive.Superellipsoid => chain.Superellipsoid(
            blend: blend,
            detail: detail,
            exponent: exponent,
            material: material,
            radii: (EllipsoidRadii * effectiveScale),
            smooth: smooth
        ),
            SdfSolidPrimitive.Capsule when (effectiveScale.X == effectiveScale.Z) => chain.Capsule(
            blend: blend,
            detail: detail,
            endpoint: new Vector3(
                x: 0f,
                y: (CapsuleEndpoint.Y * effectiveScale.Y),
                z: 0f
            ),
            material: material,
            radius: (CapsuleRadius * effectiveScale.X),
            smooth: smooth
        ),
            SdfSolidPrimitive.Cylinder when ((effectiveScale.X == effectiveScale.Z) && (chamfer > 0f)) => chain.ChamferedRectangle(
            blend: blend,
            chamfer: chamfer,
            detail: detail,
            halfHeight: (CylinderHalfHeight * effectiveScale.Y),
            halfWidth: (CylinderRadius * effectiveScale.X),
            lift: SdfLift.Revolve,
            liftAmount: 0f,
            material: material,
            smooth: smooth
        ),
            SdfSolidPrimitive.Cylinder when (effectiveScale.X == effectiveScale.Z) => chain.Cylinder(
            blend: blend,
            detail: detail,
            halfHeight: (CylinderHalfHeight * effectiveScale.Y),
            material: material,
            radius: (CylinderRadius * effectiveScale.X),
            rounding: rounding,
            smooth: smooth
        ),
            SdfSolidPrimitive.Cone when (effectiveScale.X == effectiveScale.Z) => chain.Trapezoid(
            blend: blend,
            bottomHalfWidth: (ConeRadius * effectiveScale.X),
            detail: detail,
            halfHeight: (ConeHalfHeight * effectiveScale.Y),
            lift: SdfLift.Revolve,
            liftAmount: 0f,
            material: material,
            rounding: rounding,
            smooth: smooth,
            topHalfWidth: 0f
        ),
            SdfSolidPrimitive.Plane => chain.Plane(
            normal: Vector3.UnitY,
            offset: 0f,
            material: material,
            blend: blend,
            detail: detail,
            smooth: smooth
        ),
            _ => AppendPrimitive(
            chain: chain.Scale(scale: effectiveScale),
            type: type,
            material: material,
            blend: blend,
            rounding: (rounding / MathF.Max(x: effectiveScale.X, y: MathF.Max(x: effectiveScale.Y, y: effectiveScale.Z))),
            chamfer: (chamfer / MathF.Max(x: effectiveScale.X, y: MathF.Max(x: effectiveScale.Y, y: effectiveScale.Z))),
            smooth: smooth,
            detail: detail
        ),
        };
    }
    private static void RequireTaper(float taper) {
        ArgumentOutOfRangeException.ThrowIfLessThan(taper, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(taper, 1f);

        if (!float.IsFinite(taper)) { throw new ArgumentOutOfRangeException(nameof(taper)); }
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
    /// <param name="taper">Prism top-to-bottom width ratio; ignored by other primitives.</param>
    /// <param name="profile">Optional Prism cross-section; null uses the trapezoid.</param>
    /// <param name="lift">Prism only: extrude or revolve. Ignored by other primitives.</param>
    /// <param name="rounding">The authored edge-rounding radius in world units, or zero for none.</param>
    /// <param name="chamfer">The authored 45-degree edge-chamfer radius in world units, or zero for none.</param>
    /// <param name="exponent">The Superellipsoid exponent; ignored by other primitives. Refused outside
    /// [<see cref="SdfProgramBuilder.MinSuperellipsoidExponent"/>, <see cref="SdfProgramBuilder.MaxSuperellipsoidExponent"/>].</param>
    public static bool TryValidateScaledPrimitive(SdfSolidPrimitive type, Vector3 scale, out string refusal, float taper = 0.5f, SdfPrismProfile? profile = null, SdfLift lift = SdfLift.Extrude, float rounding = 0f, float chamfer = 0f, float exponent = SdfProgramBuilder.MinSuperellipsoidExponent) {
        refusal = string.Empty;

        var effectiveScale = EffectiveScale(scale: scale);

        if (profile is not null && (type != SdfSolidPrimitive.Prism || !profile.IsValid())) {
            refusal = "an invalid prism profile";
            return false;
        }

        if (!Enum.IsDefined(value: lift)) {
            refusal = "an undefined prism lift";
            return false;
        }

        if ((type != SdfSolidPrimitive.Prism) && (lift != SdfLift.Extrude)) {
            refusal = "a lift on a primitive that has no profile to lift";
            return false;
        }

        if (type == SdfSolidPrimitive.Prism && (!float.IsFinite(taper) || taper < 0f || taper > 1f)) {
            refusal = "a prism taper outside the finite [0, 1] interval";
            return false;
        }

        if (
            (type == SdfSolidPrimitive.Superellipsoid) &&
            (!float.IsFinite(exponent) || (exponent < SdfProgramBuilder.MinSuperellipsoidExponent) || (exponent > SdfProgramBuilder.MaxSuperellipsoidExponent))
        ) {
            refusal = $"a superellipsoid exponent outside the finite [{SdfProgramBuilder.MinSuperellipsoidExponent}, {SdfProgramBuilder.MaxSuperellipsoidExponent}] interval";
            return false;
        }

        if (rounding != 0f) {
            if (!float.IsFinite(rounding) || (rounding < 0f)) {
                refusal = "a non-finite or negative edge-rounding radius";

                return false;
            }

            if (!RoundingAdmits(
                type: type,
                scale: effectiveScale,
                lift: lift,
                profile: profile,
                rounding: rounding,
                taper: taper,
                ceiling: out var roundingCeiling
            )) {
                refusal = ((roundingCeiling > 0f)
                    ? $"an edge-rounding radius {rounding} past the {roundingCeiling} this shape can carry (the profile inradius, and for an extrude its half-depth)"
                    : $"an edge-rounding radius {rounding} on a shape with no room to round");

                return false;
            }
        }

        if (chamfer != 0f) {
            if (!float.IsFinite(chamfer) || (chamfer < 0f)) {
                refusal = "a non-finite or negative edge-chamfer radius";

                return false;
            }

            var chamferCeiling = MaxChamfer(
                type: type,
                scale: effectiveScale,
                lift: lift,
                profile: profile,
                taper: taper
            );

            if (chamfer > chamferCeiling) {
                refusal = ((chamferCeiling > 0f)
                    ? $"an edge-chamfer radius {chamfer} past the {chamferCeiling} this shape can carry (the profile inradius, and for an extrude its half-depth)"
                    : $"an edge-chamfer radius {chamfer} on a shape with no room to chamfer");

                return false;
            }
        }

        if (profile is { Kind: SdfPrismProfileKind.ChamferedRectangle } && (lift == SdfLift.Extrude)) {
            // The profile's chamfer also bevels the cap rims, so an extrude thinner than the chamfer would have the
            // builder clamp the authored corner cut to its half-depth silently; refuse where the value is read.
            var profileChamfer = (profile.CornerRadius * MathF.Min(x: effectiveScale.X, y: effectiveScale.Y));

            if (profileChamfer > effectiveScale.Z) {
                refusal = $"a chamfered-rectangle profile whose {profileChamfer} chamfer exceeds the {effectiveScale.Z} extrude half-depth that also carries it on the cap rims";

                return false;
            }
        }

        if (profile is { Kind: not SdfPrismProfileKind.Trapezoid }) { return true; }

        // A revolved trapezoid profile keeps the same slant admission the extruded one has: scale.z leaves the
        // profile plane entirely, so the degeneracy check below reads the same two components either way.

        // A uniform scale rides one Scale transform over the unit primitive, so no authored dimension reaches a
        // shape's own admission rule; only the baked arms below can author a degenerate shape.
        if (IsUniform(scale: effectiveScale)) {
            return true;
        }

        if (
            (type != SdfSolidPrimitive.Prism) &&
            ((type != SdfSolidPrimitive.Cone) || (effectiveScale.X != effectiveScale.Z))
        ) {
            return true;
        }

        var slant = new Vector2(
            x: ((type == SdfSolidPrimitive.Prism ? 1f - taper : ConeRadius) * effectiveScale.X),
            y: ((2f * ConeHalfHeight) * effectiveScale.Y)
        );

        if (slant.LengthSquared() < (SdfProgramBuilder.MinTrapezoidProfileSlant * SdfProgramBuilder.MinTrapezoidProfileSlant)) {
            refusal = type == SdfSolidPrimitive.Cone
                ? $"a cone whose radial scale {effectiveScale.X} and axial scale {effectiveScale.Y} give it a {slant.Length()}-unit profile slant, under the {SdfProgramBuilder.MinTrapezoidProfileSlant} the deterministic fixed-point field evaluator can distinguish from a point"
                : $"a prism whose width scale {effectiveScale.X} and height scale {effectiveScale.Y} give it a {slant.Length()}-unit profile slant, under the {SdfProgramBuilder.MinTrapezoidProfileSlant} the deterministic fixed-point field evaluator can distinguish from a point";

            return false;
        }

        return true;
    }
    /// <summary>Reads a primitive's local extent from the canonical dimension table.</summary>
    /// <param name="type">The primitive.</param>
    /// <returns>The finite local bounds, or the unbounded marker for <see cref="SdfSolidPrimitive.Plane"/>.</returns>
    public static SdfSolidBounds GetLocalBounds(SdfSolidPrimitive type) {
        return type switch {
            // The exact ellipse builder nudges a unit circle's Y radius by 1e-4 to avoid its degeneracy.
            SdfSolidPrimitive.Prism => new(Center: Vector3.Zero, HalfExtents: new Vector3(1.0001f)),
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
            // |p_i| <= r_i on every axis for any exponent (the superellipsoid never leaves its own axis-aligned
            // box — see SdfProgramBuilder.Superellipsoid's remarks), so the unit box is exact here, tight at e = 2
            // (the ellipsoid limit) and at the corners as e grows.
            SdfSolidPrimitive.Superellipsoid => new(
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
    /// <param name="lift">Prism only: extrude or revolve. A revolve reads <c>scale.z</c> as a radial offset rather
    /// than an extent, so it reaches farther than the same scale extruded.</param>
    /// <returns>The reach in local units.</returns>
    /// <remarks>Reads the same effective scale <see cref="AppendScaledPrimitive"/> emits at — each component's
    /// magnitude, raised to <see cref="MinimumScale"/>. A reach taken from the authored value instead disagrees with
    /// the emitted program wherever a component is signed or vanishing: every consumer folds reach into a running
    /// <c>Max</c> seeded at zero, so the cull bound collapses to its margin around geometry that is still there.</remarks>
    public static float Reach(SdfSolidPrimitive type, Vector3 scale, SdfLift lift = SdfLift.Extrude) {
        var magnitude = EffectiveScale(scale: scale);

        // A revolved prism has no unit reach to scale: scale.z is the radial offset the profile sits at, so the
        // farthest surface point is (offset + radial extent) out and (axial extent) up. The 1.0001 factor is the
        // ellipse profile's own circle-degeneracy nudge, carried here so one bound covers every profile kind.
        if ((type == SdfSolidPrimitive.Prism) && (lift == SdfLift.Revolve)) {
            return new Vector2(
                x: (MathF.Abs(x: scale.Z) + (magnitude.X * 1.0001f)),
                y: (magnitude.Y * 1.0001f)
            ).Length();
        }

        // A superellipsoid's farthest point is genuinely axis-dependent past e = 2 (a rounded-corner "squircle"
        // reaches past max(radii) toward its corners — see SdfProgramBuilder.Superellipsoid's remarks), so the
        // plain reach*maxScale form below (sound only for a canonical reach that is itself axis-aligned) cannot
        // express it. |p_i| <= r_i on every axis for every admitted exponent, so the axis-aligned box's own
        // circumsphere radius is always sound, and tight in the e -> infinity (box) limit.
        if (type == SdfSolidPrimitive.Superellipsoid) {
            return magnitude.Length();
        }

        var maxScale = MathF.Max(
            x: magnitude.X,
            y: MathF.Max(
                x: magnitude.Y,
                y: magnitude.Z
            )
        );
        var reach = type switch {
            SdfSolidPrimitive.Prism => MathF.Sqrt(3f) * 1.0001f,
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
    /// <summary>A primitive's worst-case reach, widened by an outward panel raise — the panel trait's raised
    /// (<c>Depth &lt; 0</c>) case, whose face stands <c>|Depth|</c> proud of the plate's own, so the plate's own cull
    /// bound must grow by that much too. A recess needs no widening: subtraction only removes.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The shape's per-axis scale.</param>
    /// <param name="lift">Prism only: extrude or revolve.</param>
    /// <param name="panelRaise">The additional outward reach a raised panel adds; a non-positive value leaves the
    /// plain reach unchanged.</param>
    /// <returns>The widened reach in local units.</returns>
    public static float Reach(SdfSolidPrimitive type, Vector3 scale, SdfLift lift, float panelRaise) =>
        (Reach(type: type, scale: scale, lift: lift) + MathF.Max(x: 0f, y: panelRaise));
    // Every primitive a panel can be authored on — every SdfSolidPrimitive but Plane, which the panel facet refuses
    // (no meaningful local face). KEEP IN SYNC with the enum: a member added there and left out here silently
    // undercounts MaxPanelReach's worst case.
    private static readonly SdfSolidPrimitive[] PanelableTypes = [
        SdfSolidPrimitive.Sphere,
        SdfSolidPrimitive.Box,
        SdfSolidPrimitive.Torus,
        SdfSolidPrimitive.Cylinder,
        SdfSolidPrimitive.Capsule,
        SdfSolidPrimitive.Ellipsoid,
        SdfSolidPrimitive.RoundCone,
        SdfSolidPrimitive.Cone,
        SdfSolidPrimitive.Prism,
    ];
    private static float WidestHalfExtent(SdfSolidPrimitive type, Vector3 scale, SdfLift lift) => MathF.Max(
        x: HalfExtent(
            type: type,
            scale: scale,
            lift: lift,
            axis: Vector3.UnitX
        ),
        y: MathF.Max(
            x: HalfExtent(
                type: type,
                scale: scale,
                lift: lift,
                axis: Vector3.UnitY
            ),
            y: HalfExtent(
                type: type,
                scale: scale,
                lift: lift,
                axis: Vector3.UnitZ
            )
        )
    );
    /// <summary>The worst-case additional reach a raised panel (<c>ShapePanelDocument.Depth</c> negative) can
    /// contribute at a scale, before any specific primitive, face, or inset is known — a capacity probe's own
    /// ceiling. <c>ShapePanelDocument.Resolve</c>'s own validation ceiling on <c>|Depth|</c> is twice the eroded
    /// copy's own half-extent along its face, and a nonnegative <c>Inset</c> only shrinks that copy from the
    /// plate's own — so the worst case, at zero inset, is twice the plate's own widest half-extent, maximized across
    /// every <see cref="PanelableTypes">primitive a panel can be authored on</see> (and, for
    /// <see cref="SdfSolidPrimitive.Prism"/>, both lifts — a revolve's radial half-extent reads
    /// <c>scale.z</c> as an offset rather than an extent and so can exceed the same primitive's extrude form).</summary>
    /// <param name="scale">The per-axis scale to measure every candidate primitive at.</param>
    /// <returns>The worst-case additional reach a raised panel can add at this scale, at least zero.</returns>
    public static float MaxPanelReach(Vector3 scale) {
        var widest = 0f;

        foreach (var type in PanelableTypes) {
            widest = MathF.Max(
                x: widest,
                y: WidestHalfExtent(
                    type: type,
                    scale: scale,
                    lift: SdfLift.Extrude
                )
            );

            if (type == SdfSolidPrimitive.Prism) {
                widest = MathF.Max(
                    x: widest,
                    y: WidestHalfExtent(
                        type: type,
                        scale: scale,
                        lift: SdfLift.Revolve
                    )
                );
            }
        }

        return (2f * widest);
    }
    /// <summary>A primitive's local support half-extent along a unit local axis — how far its emitted zero set reaches
    /// from its center in that direction, the panel trait's erode/translate input (see <c>ShapePanelDocument</c>).
    /// Reads the same zero set <see cref="AppendScaledPrimitive"/> emits: a Box's face sits at its half-extents lane
    /// in both its spellings (the corner fillet is an inset), <c>(1 + BoxRound)·scaleᵢ − BoxRound·min(scale)</c> per
    /// axis and exactly <c>scale</c> when uniform. Exact along a principal axis for every primitive; off-axis, a Sphere or Ellipsoid
    /// reads its true support (<c>|radii ⊙ axis|</c>), and every other primitive reads the support of its canonical
    /// unit-scale half-extent box (<see cref="GetLocalBounds"/>'s dimension table, <c>Σ|axisᵢ|·extentᵢ</c>). Support
    /// (the farthest reach) rather than the ray-exit distance is what keeps a raised panel's bound widening sound:
    /// eroding every axis by <c>i</c> moves the support by at most <c>i</c>.</summary>
    /// <param name="type">The primitive. A <see cref="SdfSolidPrimitive.Plane"/> has no meaningful face; callers
    /// refuse it before reaching here.</param>
    /// <param name="scale">The authored per-axis scale.</param>
    /// <param name="lift">Prism only: extrude or revolve.</param>
    /// <param name="axis">The local direction (normalized on entry).</param>
    /// <returns>The half-extent, at least zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis"/> is non-finite or zero-length: a face
    /// that names no direction is refused rather than silently read as some default axis.</exception>
    public static float HalfExtent(SdfSolidPrimitive type, Vector3 scale, SdfLift lift, Vector3 axis) {
        var effectiveScale = EffectiveScale(scale: scale);

        if (
            !float.IsFinite(f: axis.X) ||
            !float.IsFinite(f: axis.Y) ||
            !float.IsFinite(f: axis.Z) ||
            (axis == Vector3.Zero)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(axis),
                actualValue: axis,
                message: "A half-extent axis must be a finite, non-zero direction."
            );
        }

        var direction = Vector3.Normalize(value: axis);

        if ((type == SdfSolidPrimitive.Prism) && (lift == SdfLift.Revolve)) {
            // The revolve offset leaves the profile plane, so only the axial extent is well-defined as a face; the
            // radial reading matches Reach's own revolve formula (offset + the profile's radial half-extent).
            var radial = (MathF.Abs(x: scale.Z) + effectiveScale.X);

            return MathF.Abs(x: Vector3.Dot(
                vector1: Vector3.Abs(value: direction),
                vector2: new Vector3(x: radial, y: effectiveScale.Y, z: radial)
            ));
        }

        // The canonical (unit-scale) half-extent per local axis, from the same dimension table GetLocalBounds reads
        // — a Prism's extrude spelling carries its own here (X/Y/Z all canonically 1: bottom half-width, half-height,
        // extrusion half-depth) rather than GetLocalBounds's conservative 1.0001 placeholder cube.
        // A Sphere/Ellipsoid's support along a unit direction is the L2 length of its radii weighted by that
        // direction — the L1 projection below would read a unit sphere's diagonal support as √2.
        if (type is SdfSolidPrimitive.Sphere or SdfSolidPrimitive.Ellipsoid) {
            var radii = (((type == SdfSolidPrimitive.Sphere)
                ? new Vector3(value: SphereRadius)
                : EllipsoidRadii) * effectiveScale);

            return (radii * direction).Length();
        }

        // KEEP IN SYNC with AppendScaledPrimitive's Box arms: the fillet is an inset in both spellings (sdfBox
        // erodes its half-extents by the corner radius before offsetting back out; ChamferedRectangle insets by its
        // rounding), so the zero set is the half-extents lane itself — (1 + BoxRound)·scale − BoxRound·min(scale)
        // per axis, exactly `scale` when uniform.
        if (type == SdfSolidPrimitive.Box) {
            var minimumScale = MathF.Min(
                x: effectiveScale.X,
                y: MathF.Min(
                    x: effectiveScale.Y,
                    y: effectiveScale.Z
                )
            );
            var face = (((BoxHalfExtents + new Vector3(value: BoxRound)) * effectiveScale) - new Vector3(value: (BoxRound * minimumScale)));

            return MathF.Abs(x: Vector3.Dot(
                vector1: Vector3.Abs(value: direction),
                vector2: face
            ));
        }

        var canonical = type switch {
            SdfSolidPrimitive.Cylinder => new Vector3(x: CylinderRadius, y: CylinderHalfHeight, z: CylinderRadius),
            SdfSolidPrimitive.Cone => new Vector3(x: ConeRadius, y: ConeHalfHeight, z: ConeRadius),
            SdfSolidPrimitive.Capsule => new Vector3(x: CapsuleRadius, y: (CapsuleEndpoint.Y + CapsuleRadius), z: CapsuleRadius),
            SdfSolidPrimitive.RoundCone => new Vector3(
                x: MathF.Max(x: RoundConeLowerRadius, y: RoundConeUpperRadius),
                y: (((RoundConeHeight + RoundConeUpperRadius) + RoundConeLowerRadius) * 0.5f),
                z: MathF.Max(x: RoundConeLowerRadius, y: RoundConeUpperRadius)
            ),
            SdfSolidPrimitive.Torus => new Vector3(x: (TorusMajor + TorusMinor), y: TorusMinor, z: (TorusMajor + TorusMinor)),
            SdfSolidPrimitive.Prism => Vector3.One,
            _ => Vector3.One,
        };

        return MathF.Abs(x: Vector3.Dot(
            vector1: Vector3.Abs(value: direction),
            vector2: (canonical * effectiveScale)
        ));
    }
    // RoundedRectangle and Ellipse take their radial/axial half-extents natively, so they emit exactly at any scale
    // and either lift. A regular polygon carries one circumradius, so it rides a Scale transform: the extrude spelling
    // scales all three axes, the revolve spelling forces X == Z (an anisotropic XZ would revolve into an elliptical
    // ring rather than a solid of revolution) and divides the offset back into the scaled frame.
    /// <summary>Returns the largest edge-rounding radius a primitive can carry at a scale, in world units.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The authored per-axis scale.</param>
    /// <param name="lift">Prism only: extrude or revolve.</param>
    /// <param name="profile">Optional Prism cross-section; null uses the trapezoid.</param>
    /// <param name="taper">The prism taper (the top half-width as a fraction of the bottom); a trapezoid profile's
    /// narrower end binds its ceiling.</param>
    /// <returns>The ceiling, zero when the primitive carries no rounding at all.</returns>
    /// <remarks>The ceiling is the profile inradius, and — for an extrude, whose kernel offset moves the caps — the
    /// lift half-depth as well. Past it the inset profile is empty and the offset back out would grow the solid past
    /// its authored extent. The authoring doors compare the authored value against this and refuse by name; the
    /// builders clamp to the same quantity so a value that slips past a door still cannot grow a cull bound.</remarks>
    public static float MaxRounding(SdfSolidPrimitive type, Vector3 scale, SdfLift lift = SdfLift.Extrude, SdfPrismProfile? profile = null, float taper = 0.5f) {
        var effectiveScale = EffectiveScale(scale: scale);
        var radial = effectiveScale.X;
        var axial = effectiveScale.Y;
        float inradius;

        switch (type) {
            case SdfSolidPrimitive.Prism: {
                    inradius = (profile?.Kind switch {
                        SdfPrismProfileKind.RoundedRectangle => MathF.Min(x: radial, y: axial),
                        // The circumradius rides a Scale transform, so its inradius is the smaller scaled apothem.
                        SdfPrismProfileKind.Polygon => (MathF.Min(x: radial, y: axial) * MathF.Cos(x: (MathF.PI / Math.Clamp(value: (profile?.Sides ?? 3), min: 3, max: 32)))),
                        SdfPrismProfileKind.Ellipse => MathF.Max(x: 0f, y: (MathF.Min(x: radial, y: axial) - 1e-4f)),
                        // The chamfer amount is CornerRadius's own fraction of the profile, exactly as AppendProfile
                        // maps it — the rounding fillet then narrows the ALREADY-chamfered profile's inradius.
                        SdfPrismProfileKind.ChamferedRectangle => SdfProgramBuilder.ChamferedRectangleInradius(
                            halfWidth: radial,
                            halfHeight: axial,
                            chamfer: SdfProgramBuilder.ClampChamfer(
                                chamfer: (profile!.CornerRadius * MathF.Min(x: radial, y: axial)),
                                halfWidth: radial,
                                halfHeight: axial,
                                lift: lift,
                                liftAmount: effectiveScale.Z
                            )
                        ),
                        // Convex's CornerRadius IS the corner rounding (SdfProgramBuilder.ConvexPolygon), so the
                        // separate rounding field has no independent room left — zero here, like Polygon's own baked
                        // constant leaves it none.
                        SdfPrismProfileKind.Convex => 0f,
                        // The trapezoid's ceiling is the builder's own: the narrower end's inset width reaching zero
                        // binds before the half-height does whenever the profile is thinner than it is tall.
                        _ => SdfProgramBuilder.TrapezoidRoundingCeiling(
                            bottomHalfWidth: radial,
                            topHalfWidth: (radial * Math.Clamp(value: taper, min: 0f, max: 1f)),
                            halfHeight: axial
                        ),
                    });

                    break;
                }
            case SdfSolidPrimitive.Cone: {
                    // The cone revolves a sharp-apex trapezoid profile (top half-width 0), whose builder ceiling is
                    // zero: the door refuses any positive rounding rather than admitting one the builder discards.
                    inradius = SdfProgramBuilder.TrapezoidRoundingCeiling(
                        bottomHalfWidth: (ConeRadius * radial),
                        topHalfWidth: 0f,
                        halfHeight: (ConeHalfHeight * axial)
                    );

                    break;
                }
            case SdfSolidPrimitive.Cylinder: {
                    inradius = MathF.Min(x: radial, y: axial);

                    break;
                }
            default: {
                    return 0f;
                }
        }

        // The Cone always revolves and the Cylinder has no lift lane; only an extruded prism spends its half-depth.
        if ((type == SdfSolidPrimitive.Prism) && (lift == SdfLift.Extrude)) {
            inradius = MathF.Min(x: inradius, y: effectiveScale.Z);
        }

        return MathF.Max(x: 0f, y: inradius);
    }
    private static bool RoundingAdmits(SdfSolidPrimitive type, Vector3 scale, SdfLift lift, SdfPrismProfile? profile, float rounding, float taper, out float ceiling) {
        ceiling = MaxRounding(
            type: type,
            scale: scale,
            lift: lift,
            profile: profile,
            taper: taper
        );

        return (rounding <= ceiling);
    }
    /// <summary>Returns the largest 45-degree edge-chamfer radius a primitive can carry at a scale, in world units.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The authored per-axis scale.</param>
    /// <param name="lift">Prism only: extrude or revolve. A revolve has no cap seam for a shape-independent chamfer
    /// to bevel, so its ceiling is zero.</param>
    /// <param name="profile">Optional Prism cross-section; null uses the trapezoid.</param>
    /// <param name="taper">The prism taper (the top half-width as a fraction of the bottom); a trapezoid profile's
    /// narrower end binds its ceiling.</param>
    /// <returns>The ceiling, zero when the primitive carries no chamfer at all.</returns>
    /// <remarks>Box: the smallest half-extent across all three axes (the profile's own inradius and the extrude
    /// half-depth are the same quantity there). Cylinder: <c>min(radius, halfHeight)</c> — a revolve has no
    /// half-depth term. Prism: the profile's true inradius and, for an extrude, the lift half-depth — a cap chamfer
    /// never erodes the profile, so its ceiling is where the cap face itself disappears, not
    /// <see cref="MaxRounding"/>'s narrower erosion-representability ceiling: a rounded rectangle or ellipse profile
    /// admits <c>min(halfWidth, halfHeight)</c>, and the trapezoid profile
    /// <see cref="SdfProgramBuilder.TrapezoidInradius"/> (so a triangle prism admits its inradius, where it admits no
    /// rounding at all). A <see cref="SdfPrismProfileKind.Polygon"/> profile has no free lane to carry a cap chamfer
    /// independent of its own baked constant and a <see cref="SdfPrismProfileKind.ChamferedRectangle"/> profile
    /// already carries its own chamfer, so both read zero here. Every other primitive reads zero: the field admits no
    /// chamfer.</remarks>
    public static float MaxChamfer(SdfSolidPrimitive type, Vector3 scale, SdfLift lift = SdfLift.Extrude, SdfPrismProfile? profile = null, float taper = 0.5f) {
        var effectiveScale = EffectiveScale(scale: scale);
        var radial = effectiveScale.X;
        var axial = effectiveScale.Y;

        switch (type) {
            case SdfSolidPrimitive.Box: {
                    return MathF.Max(x: 0f, y: MathF.Min(x: radial, y: MathF.Min(x: axial, y: effectiveScale.Z)));
                }
            case SdfSolidPrimitive.Cylinder: {
                    return MathF.Max(x: 0f, y: MathF.Min(x: radial, y: axial));
                }
            case SdfSolidPrimitive.Prism: {
                    // A revolve has no cap seam for this cap-only bevel to reach (the profile's own chamfer, where one
                    // exists, is authored through SdfPrismProfileKind.ChamferedRectangle instead).
                    if (lift != SdfLift.Extrude) {
                        return 0f;
                    }

                    var inradius = (profile?.Kind switch {
                        SdfPrismProfileKind.RoundedRectangle => MathF.Min(x: radial, y: axial),
                        SdfPrismProfileKind.Ellipse => MathF.Max(x: 0f, y: (MathF.Min(x: radial, y: axial) - 1e-4f)),
                        // The true inradius, not the rounding ceiling: a cap chamfer erodes nothing. KEEP IN SYNC with
                        // the Trapezoid builder's capChamfer clamp.
                        null => SdfProgramBuilder.TrapezoidInradius(
                            bottomHalfWidth: radial,
                            topHalfWidth: (radial * Math.Clamp(value: taper, min: 0f, max: 1f)),
                            halfHeight: axial
                        ),
                        // Polygon's Data1.z already carries its baked ecs.y (no free lane for an independent cap
                        // chamfer); ChamferedRectangle folds its own cap chamfer from its profile chamfer already.
                        _ => 0f,
                    });

                    return MathF.Max(x: 0f, y: MathF.Min(x: inradius, y: effectiveScale.Z));
                }
            default: {
                    return 0f;
                }
        }
    }
    private static SdfProgramBuilder AppendProfile(SdfProgramBuilder chain, Vector3 scale, SdfPrismProfile profile,
        int material, SdfBlendOp blend, float smooth, SdfLift lift = SdfLift.Extrude, float rounding = 0f, bool detail = false) {
        if (!profile.IsValid()) { throw new ArgumentOutOfRangeException(nameof(profile)); }

        var revolve = (lift == SdfLift.Revolve);
        var revolveOffset = MathF.Abs(x: scale.Z);
        var polygonScale = (revolve
            ? new Vector3(x: scale.X, y: scale.Y, z: scale.X)
            : scale);
        var scaledRounding = (rounding / MathF.Max(x: polygonScale.X, y: MathF.Max(x: polygonScale.Y, y: polygonScale.Z)));

        return profile.Kind switch {
            SdfPrismProfileKind.RoundedRectangle => chain.RoundedRectangle(scale.X, scale.Y,
                profile.CornerRadius * MathF.Min(scale.X, scale.Y), lift,
                (revolve ? revolveOffset : scale.Z), material, blend, smooth, rounding, detail: detail),
            SdfPrismProfileKind.Polygon => chain.Scale(polygonScale).RegularPolygon(profile.Sides, 1f, lift,
                (revolve ? (revolveOffset / polygonScale.X) : 1f), material, blend, smooth, scaledRounding, detail: detail),
            SdfPrismProfileKind.Ellipse => (revolve
                ? chain.Ellipse(scale.X, scale.Y, SdfLift.Revolve, revolveOffset, material, blend, smooth, rounding, detail: detail)
                : chain.Scale(scale).Ellipse(1f, 1f, SdfLift.Extrude, 1f, material, blend, smooth, scaledRounding, detail: detail)),
            SdfPrismProfileKind.ChamferedRectangle => chain.ChamferedRectangle(scale.X, scale.Y,
                (profile.CornerRadius * MathF.Min(scale.X, scale.Y)), lift,
                (revolve ? revolveOffset : scale.Z), material, blend, smooth, rounding, detail: detail),
            // Vertices are authored unscaled (like Polygon's unit circumradius), so they ride the SAME
            // Scale(polygonScale) wrap; CornerRadius reads as a fraction of the RAW vertices' own inradius, the
            // profile's sole rounding control — the separate rounding field has no independent ceiling here
            // (SdfSolidGeometry.MaxRounding's Convex arm is 0), so it is summed in defensively rather than dropped:
            // ClampRounding still bounds the total, exactly as every other profile's builder clamps rather than trusts.
            SdfPrismProfileKind.Convex => chain.Scale(polygonScale).ConvexPolygon(profile.Vertices!,
                ((profile.CornerRadius * SdfProgramBuilder.ConvexPolygonInradius(profile.Vertices!)) + scaledRounding), lift,
                (revolve ? (revolveOffset / polygonScale.X) : 1f), material, blend, smooth, detail: detail),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }
}
