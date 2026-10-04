namespace Puck.SignedDistance.Illumination;

/// <summary>A finite orthographic light view. The receiver box chooses its transverse extent; the caster box chooses
/// its depth interval. The texel half-diagonal covers each complete directional pixel column without a distant eye.</summary>
/// <param name="Origin">The camera position, in world units.</param>
/// <param name="Right">The camera's unit right axis.</param>
/// <param name="Up">The camera's unit up axis.</param>
/// <param name="TowardLight">The unit direction from the region toward the light.</param>
/// <param name="HalfWidth">The square camera's half-width in world units.</param>
/// <param name="Near">The near depth along the camera axis.</param>
/// <param name="Far">The furthest ray distance covering the caster box.</param>
/// <param name="Resolution">The square map's edge in texels.</param>
public readonly record struct IrradianceLightProjection(Double3 Origin, Double3 Right, Double3 Up,
    Double3 TowardLight, double HalfWidth, double Near, double Far, int Resolution) {
    /// <summary>The radius covering a complete texel column at every depth.</summary>
    public double SweepRadius => ((HalfWidth * Math.Sqrt(d: 2.0)) / Resolution);

    /// <summary>Constructs a bounded view whose parallel rays have zero divergence from the directional light.</summary>
    /// <param name="receiverMin">The finite receiver box's least corner.</param>
    /// <param name="receiverMax">The finite receiver box's greatest corner.</param>
    /// <param name="casterMin">The finite box containing every potential caster's least corner.</param>
    /// <param name="casterMax">The finite box containing every potential caster's greatest corner.</param>
    /// <param name="towardLight">The nonzero direction toward the light.</param>
    /// <param name="penumbraSlope">The positive tangent of the light's angular radius.</param>
    /// <param name="resolution">The map's positive edge in texels.</param>
    /// <returns>The bounded camera. Unbounded geometry needs a per-hit shadow ray instead of a finite map.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A box is unbounded or inverted, the direction is zero, or a
    /// scalar is nonpositive or nonfinite.</exception>
    public static IrradianceLightProjection Create(Double3 receiverMin, Double3 receiverMax, Double3 casterMin,
        Double3 casterMax, Double3 towardLight, double penumbraSlope, int resolution) {
        RequireBox(min: receiverMin, max: receiverMax);
        RequireBox(min: casterMin, max: casterMax);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: resolution);
        if (!double.IsFinite(d: penumbraSlope) || (penumbraSlope <= 0.0) ||
            !double.IsFinite(d: towardLight.Length) || (towardLight.Length <= 0.0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(penumbraSlope));
        }
        var toward = towardLight.Normalize();
        var helper = ((Math.Abs(value: toward.Y) < 0.9) ? new Double3(X: 0, Y: 1, Z: 0) : new Double3(X: 1, Y: 0, Z: 0));
        var right = Double3.Cross(a: helper, b: toward).Normalize();
        var up = Double3.Cross(a: toward, b: right);
        var center = ((receiverMin + receiverMax) * 0.5);
        var receiverHalf = ((receiverMax - receiverMin) * 0.5);
        var casterCenter = ((casterMin + casterMax) * 0.5);
        var casterHalf = ((casterMax - casterMin) * 0.5);
        var transverse = Math.Max(val1: Radius(axis: right, half: receiverHalf), val2: Radius(axis: up, half: receiverHalf));
        var receiverDepth = Radius(axis: toward, half: receiverHalf);
        var casterDepth = Radius(axis: toward, half: casterHalf);
        var casterOffset = Double3.Dot(a: (casterCenter - center), b: toward);
        var closest = Math.Max(val1: receiverDepth, val2: (casterOffset + casterDepth));
        var furthest = Math.Min(val1: -receiverDepth, val2: (casterOffset - casterDepth));
        var halfWidth = (Math.Max(val1: transverse, val2: 0.001) * (1.0 + (2.0 / resolution)));
        var radius = ((halfWidth * Math.Sqrt(d: 2.0)) / resolution);
        // Start close to the caster volume, with enough clearance for the complete swept column. No value grows
        // as the penumbra shrinks: it remains a required positive authoring parameter, not a virtual-eye distance.
        const double Near = 0.001;
        var distance = (closest + radius + (2 * Near));
        var far = (distance - furthest + radius + Near);
        var origin = (center + (toward * distance));
        if (!double.IsFinite(d: far) || !double.IsFinite(d: origin.Length) || !double.IsFinite(d: halfWidth)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(receiverMin), message: "The light view exceeds finite projection coordinates.");
        }
        return new IrradianceLightProjection(Origin: origin, Right: right, Up: up,
            TowardLight: toward, HalfWidth: halfWidth, Near: Near, Far: far, Resolution: resolution);
    }

    /// <summary>Returns the near-plane origin of a map texel's parallel ray.</summary>
    /// <param name="column">The zero-based column.</param>
    /// <param name="row">The zero-based row, increasing down the image.</param>
    /// <returns>The ray's world-space origin.</returns>
    public Double3 OriginAt(int column, int row) => (Origin +
        (Right * ((((column + 0.5) * 2.0 / Resolution) - 1.0) * HalfWidth)) +
        (Up * ((1.0 - ((row + 0.5) * 2.0 / Resolution)) * HalfWidth)));

    /// <summary>Projects a receiver onto the map, refusing points outside its frustum or swept depth.</summary>
    /// <param name="point">The receiver position.</param>
    /// <param name="column">Receives the texel column.</param>
    /// <param name="row">Receives the texel row.</param>
    /// <param name="travel">Receives the receiver's projected distance along that texel's ray.</param>
    /// <returns>Whether the receiver is inside the swept volume.</returns>
    public bool Project(Double3 point, out int column, out int row, out double travel) {
        var offset = (point - Origin);
        var depth = -Double3.Dot(a: offset, b: TowardLight);
        column = -1;
        row = -1;
        travel = 0.0;
        if ((depth < Near) || !double.IsFinite(d: depth)) { return false; }
        var horizontal = (Double3.Dot(a: offset, b: Right) / HalfWidth);
        var vertical = (Double3.Dot(a: offset, b: Up) / HalfWidth);
        if ((Math.Abs(value: horizontal) >= 1.0) || (Math.Abs(value: vertical) >= 1.0)) { return false; }
        column = ((int)((horizontal + 1.0) * 0.5 * Resolution));
        row = ((int)((1.0 - vertical) * 0.5 * Resolution));
        travel = depth;
        return (travel <= Far);
    }

    private static double Radius(Double3 axis, Double3 half) =>
        ((Math.Abs(value: axis.X) * half.X) + (Math.Abs(value: axis.Y) * half.Y) + (Math.Abs(value: axis.Z) * half.Z));

    private static void RequireBox(Double3 min, Double3 max) {
        if (!double.IsFinite(d: min.Length) || !double.IsFinite(d: max.Length) ||
            (min.X > max.X) || (min.Y > max.Y) || (min.Z > max.Z)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(min), message: "A light view requires finite ordered receiver and caster bounds.");
        }
    }
}
