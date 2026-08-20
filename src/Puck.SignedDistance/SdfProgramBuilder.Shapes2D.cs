using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>Adds an ellipse (the exact ellipse 2D SDF) lifted to a 3D solid — <see cref="SdfLift.Revolve"/> at offset 0 gives
    /// an exact spheroid (which, unlike the approximate <see cref="Ellipsoid(Vector3, int, SdfBlendOp, float)"/> #6,
    /// earns a real cull bound), <see cref="SdfLift.Extrude"/> an elliptic-cylinder prism. Exact and 1-Lipschitz.
    /// KEEP IN SYNC with sdfEllipseSolid in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="semiX">The semi-axis along local X.</param>
    /// <param name="semiY">The semi-axis along local Y.</param>
    /// <param name="lift">Whether to revolve the profile around Y (offset 0 ⇒ a spheroid) or extrude it along Z.</param>
    /// <param name="liftAmount">The revolve offset or the extrude half-height; clamped to ≥ 0.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="semiX"/>, <paramref name="semiY"/> or
    /// <paramref name="liftAmount"/> is not finite, the derived lifted bound radius (see remarks) is not finite,
    /// <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or
    /// <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Ellipse(float semiX, float semiY, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
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

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> or <paramref name="liftAmount"/> is not
    /// finite, the derived lifted bound radius (see remarks) is not finite, <paramref name="material"/> is negative,
    /// <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or <paramref name="smooth"/> is not
    /// finite.</exception>
    public SdfProgramBuilder RegularPolygon(int sides, float radius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
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

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),      // Data1.y = lift mode
            derived2: 1f,                     // Data1.z = ecs.y = 1 (m = 2: the regular-polygon case)
            dimensions: new Vector4(
                w: clampedLift,
                x: absRadius,
                y: (MathF.PI / n),            // an = π/n, HOST-BAKED
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
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not finite, the derived lifted bound radius (see
    /// remarks) is not finite, <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined
    /// <see cref="SdfLift"/>, or <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder RoundedRectangle(float halfWidth, float halfHeight, float cornerRadius, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
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

        RequireFiniteLiftedReach(
            radius2D: new Vector2(
                x: hw,
                y: hh
            ).Length(),
            liftAmount: clampedLift,
            lift: lift,
            shapeName: "rounded-rectangle"
        );

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
            dimensions: new Vector4(
                w: clampedLift,
                x: hw,
                y: hh,
                z: Math.Clamp(
                    cornerRadius,
                    0f,
                    MathF.Min(
                        x: hw,
                        y: hh
                    )
                )
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/>, <paramref name="sharpness"/> or
    /// <paramref name="liftAmount"/> is not finite, the derived lifted bound radius (see remarks) is not finite,
    /// <paramref name="material"/> is negative, <paramref name="lift"/> is not a defined <see cref="SdfLift"/>, or
    /// <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Star(int points, float radius, float sharpness, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
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
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not finite, the derived lifted bound radius (see
    /// remarks) is not finite, the profile's slant vector is shorter than
    /// <see cref="MinTrapezoidProfileSlant"/>, <paramref name="material"/> is negative, <paramref name="lift"/> is
    /// not a defined <see cref="SdfLift"/>, or <paramref name="smooth"/> is not finite.</exception>
    public SdfProgramBuilder Trapezoid(float bottomHalfWidth, float topHalfWidth, float halfHeight, SdfLift lift, float liftAmount, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f) {
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

        return Shape(
            blend: blend,
            derived1: ((float)((uint)lift)),
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
}
