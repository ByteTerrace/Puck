using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>Adds an ellipse (the exact ellipse 2D SDF) lifted to a 3D solid — <see cref="SdfLift.Revolve"/> at offset 0 gives
    /// an exact spheroid (which, unlike the approximate <see cref="Ellipsoid(Vector3, int, SdfBlendOp, float, bool)"/> #6,
    /// earns a real cull bound), <see cref="SdfLift.Extrude"/> an elliptic-cylinder prism. Exact and 1-Lipschitz.
    /// KEEP IN SYNC with sdfEllipseSolid in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="semiX">The semi-axis along local X.</param>
    /// <param name="semiY">The semi-axis along local Y.</param>
    /// <param name="lift">Whether to revolve the profile around Y (offset 0 ⇒ a spheroid) or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <param name="rounding">The edge-rounding radius filleting the extrusion's rims, clamped by
    /// <see cref="ClampRounding"/>; zero emits the sharp shape. The inset shrinks both semi-axes, so the emitted
    /// solid is the inset ellipse offset back out — exact at the four vertices and a smooth near-ellipse
    /// between them (an ellipse's inner parallel curve is not itself an ellipse).</param>
    /// <param name="capChamfer">A 45-degree bevel radius on the extrusion's two cap rims (<see cref="SdfLift.Extrude"/>
    /// only — a revolve has no cap seam and ignores it), clamped like <paramref name="rounding"/>; zero (the default)
    /// takes the plain extrude join exactly, so an unchamfered ellipse is unchanged.</param>
    /// <param name="detail">Whether the shape is SHADING-ONLY (<see cref="SdfInstruction.Detail"/>) — skipped by
    /// every march/step-bound consumer and included only at an already-found hit.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="semiX"/>, <paramref name="semiY"/> or
    /// <paramref name="liftAmount"/> is not finite, the derived lifted bound radius (see remarks) is not finite,
    /// <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or
    /// <paramref name="smooth"/>/<paramref name="rounding"/>/<paramref name="capChamfer"/> is not finite.</exception>
    public SdfProgramBuilder Ellipse(float semiX, float semiY, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float rounding = 0f, float capChamfer = 0f, bool detail = false) {
        // Signs are absorbed (MathF.Abs then a 1e-4 floor on both semi-axes, MathF.Max(0) on the lift); a NaN would
        // survive both MathF.Max calls and then poison the circle-degeneracy nudge below.
        RequireFinite(
            value: semiX,
            paramName: nameof(semiX),
            subject: "An ellipse semi-axis"
        );
        RequireFinite(
            value: semiY,
            paramName: nameof(semiY),
            subject: "An ellipse semi-axis"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireFinite(
            value: rounding,
            paramName: nameof(rounding),
            subject: "An edge-rounding radius"
        );
        RequireFinite(
            value: capChamfer,
            paramName: nameof(capChamfer),
            subject: "A cap-chamfer radius"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var ea = MathF.Max(
            x: MathF.Abs(x: semiX),
            y: 1e-4f
        );
        var eb = MathF.Max(
            x: MathF.Abs(x: semiY),
            y: 1e-4f
        );

        // The exact ellipse divides by (eb²−ea²); nudge a perfect circle apart so it never divides by zero (a circle is
        // better served by Sphere/Cylinder anyway). Sub-pixel at any sane authoring scale.
        if (MathF.Abs(x: (ea - eb)) < 1e-4f) {
            eb = (ea + 1e-4f);
        }

        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );

        RequireFiniteLiftedReach(
            radius2D: MathF.Max(
                x: ea,
                y: eb
            ),
            liftAmount: clampedLift,
            lift: lift,
            shapeName: "ellipse"
        );

        // Both semi-axes shrink by the same amount, so the 1e-4 circle-degeneracy gap above survives the inset.
        var profileInradius = MathF.Max(
            x: 0f,
            y: (MathF.Min(
                x: ea,
                y: eb
            ) - 1e-4f)
        );
        var round = ClampRounding(
            rounding: rounding,
            profileInradius: profileInradius,
            lift: lift,
            liftAmount: clampedLift
        );
        // Shares round's ceiling rather than eroding the profile — a cap chamfer bevels the extrude's Z-seam only,
        // so it needs no offset-back-out trick and cannot itself change ea/eb/clampedLift.
        var clampedCapChamfer = ClampRounding(
            rounding: capChamfer,
            profileInradius: profileInradius,
            lift: lift,
            liftAmount: clampedLift
        );

        if (round > 0f) {
            ea -= round;
            eb -= round;
            clampedLift = InsetLift(
                lift: lift,
                liftAmount: clampedLift,
                rounding: round
            );
        }

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            derived2: clampedCapChamfer,
            derived3: round,
            detail: detail,
            dimensions: new Vector4(
                w: clampedLift,
                x: ea,
                y: eb,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.Ellipse,
            smooth: smooth
        );
    }
    /// <summary>Adds a regular convex <paramref name="sides"/>-gon (the exact star-polygon SDF with the m = 2 regular-polygon case) lifted to
    /// a 3D solid — <see cref="SdfLift.Extrude"/> gives a prism (a nut, a column, a gem), <see cref="SdfLift.Revolve"/>
    /// a lathe of the polygon's profile. The half-sector π/n is host-baked. Exact and 1-Lipschitz. KEEP IN SYNC with
    /// sdfPolyStar/sdfStar2D in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="sides">The side count n (clamped to ≥ 3).</param>
    /// <param name="radius">The circumradius (centre to a vertex).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <param name="rounding">The edge-rounding radius filleting the lift's rims (extrude) or the profile's corners
    /// (revolve), clamped by <see cref="ClampRounding"/>; zero emits the sharp shape.</param>
    /// <param name="detail">Whether the shape is SHADING-ONLY (<see cref="SdfInstruction.Detail"/>) — skipped by
    /// every march/step-bound consumer and included only at an already-found hit.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> or <paramref name="liftAmount"/> is not
    /// finite, the derived lifted bound radius (see remarks) is not finite, <paramref name="material"/> is negative,
    /// <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or
    /// <paramref name="smooth"/>/<paramref name="rounding"/> is not finite.</exception>
    public SdfProgramBuilder RegularPolygon(int sides, float radius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float rounding = 0f, bool detail = false) {
        // Signs are absorbed (MathF.Abs on the radius, MathF.Max(0) on the lift); sides is an int clamped to >= 3.
        RequireFinite(
            value: radius,
            paramName: nameof(radius),
            subject: "A polygon circumradius"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireFinite(
            value: rounding,
            paramName: nameof(rounding),
            subject: "An edge-rounding radius"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var n = Math.Max(
            val1: 3,
            val2: sides
        );
        var absRadius = MathF.Abs(x: radius);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );

        RequireFiniteLiftedReach(
            lift: lift,
            liftAmount: clampedLift,
            radius2D: absRadius,
            shapeName: "regular polygon"
        );

        // A convex regular n-gon is closed under erosion: its inradius is R·cos(π/n) and inset-by-r is the n-gon of
        // circumradius R − r/cos(π/n).
        var apothemRatio = MathF.Cos(x: (MathF.PI / n));
        var round = ClampRounding(
            rounding: rounding,
            profileInradius: (absRadius * apothemRatio),
            lift: lift,
            liftAmount: clampedLift
        );

        if (round > 0f) {
            absRadius = MathF.Max(
                x: 0f,
                y: (absRadius - (round / apothemRatio))
            );
            clampedLift = InsetLift(
                lift: lift,
                liftAmount: clampedLift,
                rounding: round
            );
        }

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),      // Data1.y = lift mode
            derived2: 1f,                     // Data1.z = ecs.y = 1 (m = 2: the regular-polygon case)
            derived3: round,                  // Data1.w = the edge-rounding radius
            detail: detail,
            dimensions: new Vector4(
                w: clampedLift,
                x: absRadius,
                y: (MathF.PI / n),            // an = π/n, host-baked
                z: 0f                         // ecs.x = 0
            ),
            material: material,
            shape: SdfShapeType.RegularPolygon,
            smooth: smooth
        );
    }
    /// <summary>Adds a rounded rectangle (exact rounded-box 2D SDF) lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a
    /// rounded slab/plaque, <see cref="SdfLift.Revolve"/> a rounded disc/puck. Exact and 1-Lipschitz. KEEP IN SYNC
    /// with sdfRoundedRect in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="halfWidth">Half-width of the rectangle (its local X half-extent).</param>
    /// <param name="halfHeight">Half-height of the rectangle (its local Y half-extent).</param>
    /// <param name="cornerRadius">Corner-rounding radius; clamped to the smaller half-extent (corners round inward).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset (for <see cref="SdfLift.Revolve"/>) or the extrude half-height (for
    /// <see cref="SdfLift.Extrude"/>); clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <param name="rounding">The edge-rounding radius filleting the lift's rims (extrude) or the profile's corners
    /// (revolve), clamped by <see cref="ClampRounding"/>; zero emits the sharp shape.</param>
    /// <param name="capChamfer">A 45-degree bevel radius on the extrusion's two cap rims (<see cref="SdfLift.Extrude"/>
    /// only — a revolve has no cap seam and ignores it), clamped like <paramref name="rounding"/>; zero (the default)
    /// takes the plain extrude join exactly, so an unchamfered rounded rectangle is unchanged.</param>
    /// <param name="detail">Whether the shape is SHADING-ONLY (<see cref="SdfInstruction.Detail"/>) — skipped by
    /// every march/step-bound consumer and included only at an already-found hit.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not finite, the derived lifted bound radius (see
    /// remarks) is not finite, <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined
    /// <see cref="SdfLift"/>, or <paramref name="smooth"/>/<paramref name="rounding"/>/<paramref name="capChamfer"/>
    /// is not finite.</exception>
    public SdfProgramBuilder RoundedRectangle(float halfWidth, float halfHeight, float cornerRadius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float rounding = 0f, float capChamfer = 0f, bool detail = false) {
        // Every sign is absorbed below (MathF.Abs on the half-extents, Math.Clamp to [0, min] on the corner radius,
        // MathF.Max(0) on the lift), and none of those absorb NaN.
        RequireFinite(
            value: halfWidth,
            paramName: nameof(halfWidth),
            subject: "A rounded-rectangle half-width"
        );
        RequireFinite(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A rounded-rectangle half-height"
        );
        RequireFinite(
            value: cornerRadius,
            paramName: nameof(cornerRadius),
            subject: "A rounded-rectangle corner radius"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireFinite(
            value: rounding,
            paramName: nameof(rounding),
            subject: "An edge-rounding radius"
        );
        RequireFinite(
            value: capChamfer,
            paramName: nameof(capChamfer),
            subject: "A cap-chamfer radius"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var hw = MathF.Abs(x: halfWidth);
        var hh = MathF.Abs(x: halfHeight);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );
        var corner = Math.Clamp(
            cornerRadius,
            0f,
            MathF.Min(
                x: hw,
                y: hh
            )
        );

        RequireFiniteLiftedReach(
            radius2D: new Vector2(
                x: hw,
                y: hh
            ).Length(),
            liftAmount: clampedLift,
            lift: lift,
            shapeName: "rounded-rectangle"
        );

        // A rounded rectangle is closed under erosion: shrinking both half-extents and the corner radius by r gives
        // the exact inset profile (a corner radius already at or under r erodes to a sharp corner, floored at zero).
        var profileInradius = MathF.Min(
            x: hw,
            y: hh
        );
        var round = ClampRounding(
            rounding: rounding,
            profileInradius: profileInradius,
            lift: lift,
            liftAmount: clampedLift
        );
        // Shares round's ceiling rather than eroding the profile — a cap chamfer bevels the extrude's Z-seam only.
        var clampedCapChamfer = ClampRounding(
            rounding: capChamfer,
            profileInradius: profileInradius,
            lift: lift,
            liftAmount: clampedLift
        );

        if (round > 0f) {
            hw -= round;
            hh -= round;
            corner = MathF.Max(
                x: 0f,
                y: (corner - round)
            );
            clampedLift = InsetLift(
                lift: lift,
                liftAmount: clampedLift,
                rounding: round
            );
        }

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            derived2: clampedCapChamfer,
            derived3: round,
            detail: detail,
            dimensions: new Vector4(
                w: clampedLift,
                x: hw,
                y: hh,
                z: corner
            ),
            material: material,
            shape: SdfShapeType.RoundedRectangle,
            smooth: smooth
        );
    }
    /// <summary>Adds an <paramref name="points"/>-pointed star (the exact star-polygon SDF) lifted to a 3D solid — <see cref="SdfLift.Extrude"/>
    /// gives a star prism (a badge, a gem), <see cref="SdfLift.Revolve"/> a spiked lathe. The baked constants
    /// (π/n and ecs = (cos(π/m), sin(π/m))) are host-baked. Exact and 1-Lipschitz. KEEP IN SYNC with
    /// sdfPolyStar/sdfStar2D in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="points">The point count n (clamped to ≥ 2).</param>
    /// <param name="radius">The outer radius (centre to a point tip).</param>
    /// <param name="sharpness">The inner-radius control m, clamped to [2, n]: 2 is a convex n-gon, larger is sharper
    /// (deeper notches between points).</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <param name="detail">Whether the shape is SHADING-ONLY (<see cref="SdfInstruction.Detail"/>) — skipped by
    /// every march/step-bound consumer and included only at an already-found hit.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/>, <paramref name="sharpness"/> or
    /// <paramref name="liftAmount"/> is not finite, the derived lifted bound radius (see remarks) is not finite,
    /// <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or
    /// <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Star(int points, float radius, float sharpness, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, bool detail = false) {
        // Signs are absorbed (MathF.Abs on the radius, Math.Clamp to [2, n] on the sharpness, MathF.Max(0) on the
        // lift), and a NaN sharpness would otherwise reach both baked trig constants.
        RequireFinite(
            value: radius,
            paramName: nameof(radius),
            subject: "A star outer radius"
        );
        RequireFinite(
            value: sharpness,
            paramName: nameof(sharpness),
            subject: "A star sharpness"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var n = Math.Max(
            val1: 2,
            val2: points
        );
        var m = Math.Clamp(
            max: n,
            min: 2f,
            value: sharpness
        );
        var en = (MathF.PI / m);
        var absRadius = MathF.Abs(x: radius);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );

        RequireFiniteLiftedReach(
            lift: lift,
            liftAmount: clampedLift,
            radius2D: absRadius,
            shapeName: "star"
        );

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),      // Data1.y = lift mode
            derived2: MathF.Sin(x: en),          // Data1.z = ecs.y = sin(π/m)
            detail: detail,
            dimensions: new Vector4(
                w: clampedLift,
                x: absRadius,
                y: (MathF.PI / n),            // an = π/n, HOST-BAKED
                z: MathF.Cos(x: en)             // ecs.x = cos(π/m), HOST-BAKED
            ),
            material: material,
            shape: SdfShapeType.Star,
            smooth: smooth
        );
    }
    /// <summary>Adds an isosceles trapezoid (exact isosceles-trapezoid 2D SDF) lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a
    /// keystone/wedge prism, <see cref="SdfLift.Revolve"/> a frustum/lampshade/cup. Exact and 1-Lipschitz. KEEP IN
    /// SYNC with sdfTrapezoidSolid in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="bottomHalfWidth">Half-width of the bottom edge (at local −Y).</param>
    /// <param name="topHalfWidth">Half-width of the top edge (at local +Y).</param>
    /// <param name="halfHeight">Half-height of the trapezoid.</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <param name="rounding">The edge-rounding radius filleting the lift's rims (extrude) or the profile's corners
    /// (revolve), clamped by <see cref="ClampRounding"/>; zero emits the sharp shape.</param>
    /// <param name="capChamfer">A 45-degree bevel radius on the extrusion's two cap rims (<see cref="SdfLift.Extrude"/>
    /// only — a revolve has no cap seam and ignores it), clamped like <paramref name="rounding"/>; zero (the default)
    /// takes the plain extrude join exactly, so an unchamfered trapezoid is unchanged.</param>
    /// <param name="detail">Whether the shape is SHADING-ONLY (<see cref="SdfInstruction.Detail"/>) — skipped by
    /// every march/step-bound consumer and included only at an already-found hit.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not finite, the derived lifted bound radius (see
    /// remarks) is not finite, the profile's slant vector — authored or inset by <paramref name="rounding"/> — is
    /// shorter than <see cref="MinTrapezoidProfileSlant"/>, <paramref name="material"/> is negative,
    /// <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or
    /// <paramref name="smooth"/>/<paramref name="rounding"/>/<paramref name="capChamfer"/> is not finite.</exception>
    public SdfProgramBuilder Trapezoid(float bottomHalfWidth, float topHalfWidth, float halfHeight, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float rounding = 0f, float capChamfer = 0f, bool detail = false) {
        // Signs are absorbed (MathF.Abs on all three half-extents, MathF.Max(0) on the lift).
        RequireFinite(
            value: bottomHalfWidth,
            paramName: nameof(bottomHalfWidth),
            subject: "A trapezoid bottom half-width"
        );
        RequireFinite(
            value: topHalfWidth,
            paramName: nameof(topHalfWidth),
            subject: "A trapezoid top half-width"
        );
        RequireFinite(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A trapezoid half-height"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireFinite(
            value: rounding,
            paramName: nameof(rounding),
            subject: "An edge-rounding radius"
        );
        RequireFinite(
            value: capChamfer,
            paramName: nameof(capChamfer),
            subject: "A cap-chamfer radius"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var bottomAbs = MathF.Abs(x: bottomHalfWidth);
        var topAbs = MathF.Abs(x: topHalfWidth);
        var heightAbs = MathF.Abs(x: halfHeight);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );
        var radius2D = MathF.Max(
            x: new Vector2(
                x: bottomAbs,
                y: heightAbs
            ).Length(),
            y: new Vector2(
                x: topAbs,
                y: heightAbs
            ).Length()
        );
        // The exact 2D core projects onto the slanted side by dividing by that side's squared length, so a profile
        // whose slant vanishes has no shape to be the distance to: the fixed-point evaluator divides by zero and the
        // shader propagates NaN through every blend downstream. The bound is the representation's, not taste's — see
        // MinTrapezoidProfileSlant. Refused here rather than nudged (as Ellipse nudges a perfect circle) because
        // there is no nearby non-degenerate trapezoid to nudge toward: both the width difference and the height are
        // vanishing at once, so the authored shape has no extent in either profile direction.
        var slant = new Vector2(
            x: (topAbs - bottomAbs),
            y: (heightAbs + heightAbs)
        );

        if (slant.LengthSquared() < (MinTrapezoidProfileSlant * MinTrapezoidProfileSlant)) {
            throw new ArgumentOutOfRangeException(
                message: $"A trapezoid profile's slant vector (topHalfWidth − bottomHalfWidth, 2·halfHeight) must be at least {MinTrapezoidProfileSlant} long; this one is {slant.Length()}, which the deterministic fixed-point field evaluator cannot distinguish from a point.",
                paramName: nameof(halfHeight)
            );
        }

        RequireFiniteLiftedReach(
            lift: lift,
            liftAmount: clampedLift,
            radius2D: radius2D,
            shapeName: "trapezoid"
        );

        // The inset trapezoid: both horizontal edges move inward by r and each slant side moves inward along its own
        // normal (2·he, −Δ)/L, so the half-width at the new top/bottom is the offset side's x there. Exact only while
        // both inset half-widths stay non-negative — past that the true erosion is a shorter, off-centre triangle the
        // centred shape lane cannot express, and flooring a width at zero instead would offset back out PAST the
        // authored width — so TrapezoidRoundingCeiling caps the radius where the narrower end's inset width reaches
        // zero (KEEP IN SYNC with SdfSolidGeometry.MaxRounding, the authoring door's ceiling).
        var trapezoidProfileInradius = TrapezoidRoundingCeiling(
            bottomHalfWidth: bottomAbs,
            topHalfWidth: topAbs,
            halfHeight: heightAbs
        );
        var round = ClampRounding(
            rounding: rounding,
            profileInradius: trapezoidProfileInradius,
            lift: lift,
            liftAmount: clampedLift
        );
        // A cap chamfer never erodes the profile (it bevels the extrude's Z-seam only), so it is bounded by the
        // profile's TRUE inradius — the cap face survives while the bevel stays under it — not by the rounding lane's
        // narrower representability ceiling. KEEP IN SYNC with SdfSolidGeometry.MaxChamfer's trapezoid arm.
        var clampedCapChamfer = ClampRounding(
            rounding: capChamfer,
            profileInradius: TrapezoidInradius(
                bottomHalfWidth: bottomAbs,
                topHalfWidth: topAbs,
                halfHeight: heightAbs
            ),
            lift: lift,
            liftAmount: clampedLift
        );

        if (round > 0f) {
            var delta = (topAbs - bottomAbs);
            var slantLength = slant.Length();
            var normalShift = (((heightAbs + heightAbs) * round) / slantLength);
            var axialShift = ((delta * round) / slantLength);
            var perHeight = (1f / (heightAbs + heightAbs));

            bottomAbs = MathF.Max(
                x: 0f,
                y: ((bottomAbs - normalShift) + ((delta * (round - axialShift)) * perHeight))
            );
            topAbs = MathF.Max(
                x: 0f,
                y: ((topAbs - normalShift) - ((delta * (round + axialShift)) * perHeight))
            );
            heightAbs -= round;
            clampedLift = InsetLift(
                lift: lift,
                liftAmount: clampedLift,
                rounding: round
            );

            var insetSlant = new Vector2(
                x: (topAbs - bottomAbs),
                y: (heightAbs + heightAbs)
            );

            if (insetSlant.LengthSquared() < (MinTrapezoidProfileSlant * MinTrapezoidProfileSlant)) {
                throw new ArgumentOutOfRangeException(
                    message: $"A trapezoid rounded by {round} leaves a slant vector {insetSlant.Length()} long, under the {MinTrapezoidProfileSlant} the deterministic fixed-point field evaluator can distinguish from a point. Narrow the rounding.",
                    paramName: nameof(rounding)
                );
            }
        }

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            derived2: clampedCapChamfer,
            derived3: round,
            detail: detail,
            dimensions: new Vector4(
                w: clampedLift,
                x: bottomAbs,
                y: topAbs,
                z: heightAbs
            ),
            material: material,
            shape: SdfShapeType.Trapezoid,
            smooth: smooth
        );
    }
    /// <summary>Adds a 45-degree-chamfered rectangle (the box field intersected with a diagonal bevel half-plane per
    /// corner) lifted to a 3D solid — <see cref="SdfLift.Extrude"/> gives a beveled slab/plaque (armor plating),
    /// <see cref="SdfLift.Revolve"/> a beveled disc/puck. An extrude additionally bevels the two cap rims at the same
    /// <paramref name="chamfer"/> (see <c>sdfExtrudeChamfer2D</c> in Assets/Shaders/Sdf/sdf-vm.hlsli), so the solid
    /// reads chamfered on every edge, not only the four the 2D profile cuts. The field is the exact signed distance
    /// inside and on the surface and a conservative lower bound outside, in the wedge past each bevel vertex where the
    /// nearest point is the vertex rather than either plane (the same class of bound the chamfer blend carries);
    /// 1-Lipschitz throughout — like the rest of the 2D-lift family, no <c>AnalyzeLipschitz</c> step clamp is needed.
    /// KEEP IN SYNC with <c>sdfChamferedRect</c>/<c>sdfChamferBox2D</c> in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="halfWidth">Half-width of the rectangle (its local X half-extent).</param>
    /// <param name="halfHeight">Half-height of the rectangle (its local Y half-extent).</param>
    /// <param name="chamfer">The 45-degree bevel radius, clamped by <see cref="ClampChamfer"/> to
    /// <c>min(halfWidth, halfHeight)</c> — the clamp's own limit degenerates the profile to a diamond (square) or
    /// octagon (rectangle), which is allowed — and, for an extrude, to <paramref name="liftAmount"/>, since the cap
    /// bevel rides the same radius.</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset (for <see cref="SdfLift.Revolve"/>) or the extrude half-height (for
    /// <see cref="SdfLift.Extrude"/>); clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <param name="rounding">A family-wide fillet radius applied ON TOP of the chamfer — the profile's largest
    /// inscribed circle after the chamfer cut bounds it (see <see cref="ClampChamfer"/>'s companion,
    /// <see cref="ChamferedRectangleInradius"/>): the plain rectangle's own inradius <c>min(halfWidth, halfHeight)</c>,
    /// narrowed by the chamfer plane <c>x + y = halfWidth + halfHeight − chamfer</c>, which sits
    /// <c>(halfWidth + halfHeight − chamfer)/√2</c> from the origin along its unit normal and binds first once the
    /// chamfer is large relative to the rectangle's aspect. Clamped by <see cref="ClampRounding"/> against that
    /// inradius (and, for an extrude, the lift half-height); zero emits the sharp-chamfered shape.</param>
    /// <param name="detail">Whether the shape is SHADING-ONLY (<see cref="SdfInstruction.Detail"/>) — skipped by
    /// every march/step-bound consumer and included only at an already-found hit.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not finite, the derived lifted bound radius (see
    /// remarks) is not finite, <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined
    /// <see cref="SdfLift"/>, or <paramref name="smooth"/>/<paramref name="chamfer"/>/<paramref name="rounding"/> is
    /// not finite.</exception>
    public SdfProgramBuilder ChamferedRectangle(float halfWidth, float halfHeight, float chamfer, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float rounding = 0f, bool detail = false) {
        // Every sign is absorbed below (MathF.Abs on the half-extents, ClampChamfer's own MathF.Abs on the chamfer,
        // MathF.Max(0) on the lift), and none of those absorb NaN.
        RequireFinite(
            value: halfWidth,
            paramName: nameof(halfWidth),
            subject: "A chamfered-rectangle half-width"
        );
        RequireFinite(
            value: halfHeight,
            paramName: nameof(halfHeight),
            subject: "A chamfered-rectangle half-height"
        );
        RequireFinite(
            value: chamfer,
            paramName: nameof(chamfer),
            subject: "A chamfer radius"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireFinite(
            value: rounding,
            paramName: nameof(rounding),
            subject: "An edge-rounding radius"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var hw = MathF.Abs(x: halfWidth);
        var hh = MathF.Abs(x: halfHeight);
        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );
        var c = ClampChamfer(
            chamfer: chamfer,
            halfWidth: hw,
            halfHeight: hh,
            lift: lift,
            liftAmount: clampedLift
        );

        RequireFiniteLiftedReach(
            radius2D: new Vector2(
                x: hw,
                y: hh
            ).Length(),
            liftAmount: clampedLift,
            lift: lift,
            shapeName: "chamfered-rectangle"
        );

        var round = ClampRounding(
            rounding: rounding,
            profileInradius: ChamferedRectangleInradius(
                halfWidth: hw,
                halfHeight: hh,
                chamfer: c
            ),
            lift: lift,
            liftAmount: clampedLift
        );

        if (round > 0f) {
            hw -= round;
            hh -= round;
            // Isotropic erosion of an intersection of half-planes shifts each constraint's offset by the SAME radius
            // along its own outward normal. The rectangle's two edge constraints shift by round directly (absorbed
            // into hw/hh above); the chamfer plane's offset (halfWidth + halfHeight − c)/√2 shifts by round too, which
            // back-solves to c' = c − round·(2 − √2) — erosion eats into the chamfer from both adjacent edges at once.
            c = MathF.Max(
                x: 0f,
                y: (c - (round * (2f - MathF.Sqrt(x: 2f))))
            );
            clampedLift = InsetLift(
                lift: lift,
                liftAmount: clampedLift,
                rounding: round
            );
        }

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            derived3: round,
            detail: detail,
            dimensions: new Vector4(
                w: clampedLift,
                x: hw,
                y: hh,
                z: c
            ),
            material: material,
            shape: SdfShapeType.ChamferedRectangle,
            smooth: smooth
        );
    }
    /// <summary>Adds a validated convex polygon (the exact iq polygon SDF, correct for any simple polygon —
    /// convexity is validated at authoring time, not required by the field itself) lifted to a 3D solid — a member
    /// of the 2D-primitive family (<see cref="SdfLift.Extrude"/> a prism, <see cref="SdfLift.Revolve"/> a lathe of
    /// the profile), but with a profile too large to pack inline: the vertices live in a side table this program's
    /// own word stream carries (see <see cref="SdfShapeType.ConvexPolygon"/>), and the instruction's Data0.x carries
    /// only a packed (table offset, vertex count) reference <see cref="SdfProgram"/> patches in at
    /// <see cref="Build"/>. Exact and 1-Lipschitz (the polygon SDF is a true Euclidean distance to the boundary), so
    /// — like the rest of the family — it earns a real cull bound with no <c>AnalyzeLipschitz</c> step clamp.</summary>
    /// <param name="vertices">3 to <see cref="SdfPrismProfile.MaxConvexVertices"/> local XY points, clockwise,
    /// convex, no coincident or collinear vertices — see <see cref="SdfPrismProfile.IsValidConvexHull"/>, which this
    /// refuses against.</param>
    /// <param name="cornerRadius">The uniform corner-rounding radius, clamped by <see cref="ClampRounding"/> against
    /// <see cref="ConvexPolygonInradius"/> (and, for an extrude, the lift half-height); zero emits the sharp
    /// polygon.</param>
    /// <param name="lift">Whether to revolve the profile around Y or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <param name="capChamfer">A 45-degree bevel radius on the extrusion's two cap rims (<see cref="SdfLift.Extrude"/>
    /// only), clamped like <paramref name="cornerRadius"/>; zero (the default) takes the plain extrude join
    /// exactly.</param>
    /// <param name="detail">Whether the emitted shape instruction is shading-only (<see cref="SdfInstruction.Detail"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="vertices"/> is not a valid convex hull (see
    /// <see cref="SdfPrismProfile.IsValidConvexHull"/>), <paramref name="liftAmount"/> is not finite, the derived
    /// lifted bound radius is not finite, <paramref name="material"/> is negative, <paramref name="lift"/> is not a
    /// defined <see cref="SdfLift"/>, or <paramref name="smooth"/>/<paramref name="cornerRadius"/>/
    /// <paramref name="capChamfer"/> is not finite.</exception>
    public SdfProgramBuilder ConvexPolygon(IReadOnlyList<Vector2> vertices, float cornerRadius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, float capChamfer = 0f, bool detail = false) {
        if (!SdfPrismProfile.IsValidConvexHull(vertices: vertices)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(vertices),
                message: $"A convex-polygon profile must carry {SdfPrismProfile.MinConvexVertices}..{SdfPrismProfile.MaxConvexVertices} finite, clockwise, convex, non-degenerate vertices."
            );
        }

        RequireFinite(
            value: cornerRadius,
            paramName: nameof(cornerRadius),
            subject: "A convex-polygon corner-rounding radius"
        );
        RequireFinite(
            value: liftAmount,
            paramName: nameof(liftAmount),
            subject: "A lift amount"
        );
        RequireFinite(
            value: capChamfer,
            paramName: nameof(capChamfer),
            subject: "A cap-chamfer radius"
        );
        RequireDefined(
            value: lift,
            paramName: nameof(lift)
        );

        var count = vertices.Count;
        var radius2D = 0f;

        for (var i = 0; (i < count); i++) {
            radius2D = MathF.Max(
                x: radius2D,
                y: vertices[i].Length()
            );
        }

        var clampedLift = MathF.Max(
            x: 0f,
            y: liftAmount
        );

        RequireFiniteLiftedReach(
            radius2D: radius2D,
            liftAmount: clampedLift,
            lift: lift,
            shapeName: "convex polygon"
        );

        var profileInradius = ConvexPolygonInradius(vertices: vertices);
        var round = ClampRounding(
            rounding: cornerRadius,
            profileInradius: profileInradius,
            lift: lift,
            liftAmount: clampedLift
        );
        var clampedCapChamfer = ClampRounding(
            rounding: capChamfer,
            profileInradius: profileInradius,
            lift: lift,
            liftAmount: clampedLift
        );
        var emittedVertices = InsetConvexPolygon(
            vertices: vertices,
            round: round
        );

        if (round > 0f) {
            clampedLift = InsetLift(
                lift: lift,
                liftAmount: clampedLift,
                rounding: round
            );
        }

        var instructionIndex = m_instructions.Count;

        m_convexPolygonProfiles.Add(item: (instructionIndex, emittedVertices));

        // Data0.x carries a placeholder (SdfProgram.Build patches in the real table offset once every profile's
        // side-table layout is known); the vertex count alone is meaningless without it, so this instruction is not
        // a legal ConvexPolygon until that patch runs.
        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            derived2: clampedCapChamfer,
            derived3: round,
            detail: detail,
            dimensions: new Vector4(
                w: clampedLift,
                x: 0f,
                y: 0f,
                z: 0f
            ),
            material: material,
            shape: SdfShapeType.ConvexPolygon,
            smooth: smooth
        );
    }
}
