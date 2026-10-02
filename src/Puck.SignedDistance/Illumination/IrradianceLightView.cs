namespace Puck.SignedDistance.Illumination;

/// <summary>
/// A light view on the CPU, the reference for the GPU's depth-only view of a shadowed directional light: a square grid
/// of texels across a region, seen from the light's side, each holding how far a sphere of the texel's half-diagonal
/// swept toward the region travelled before it touched a surface. Because the sphere covers the texel's whole column,
/// every occluder that crosses the column is recorded however thin, so a receiver an occluder shadows from farther than
/// the comparison's bias is never read lit; the price is that shadows widen by up to a texel. The GPU's view is a
/// perspective camera placed far along the light whose march accepts within the same radius of each pixel's ray; its
/// rays diverge by less than half the light's penumbra, which this orthographic grid does not model.
/// </summary>
public sealed class IrradianceLightView {
    private readonly Double3 m_origin;
    private readonly Double3 m_right;
    private readonly Double3 m_up;
    private readonly Double3 m_toward;
    private readonly int m_resolution;
    private readonly double m_distance;
    private readonly double[] m_depth;

    /// <summary>Initializes a new instance of the <see cref="IrradianceLightView"/> class and renders it.</summary>
    /// <param name="field">The field.</param>
    /// <param name="towardLight">The unit direction from a surface toward the light.</param>
    /// <param name="center">The centre of the region the view covers, in world units.</param>
    /// <param name="halfWidth">The region's half-width across the light's direction, in world units.</param>
    /// <param name="resolution">The texels along each side of the grid.</param>
    /// <param name="distance">How far along the light, in world units, the grid's plane sits from the centre; it must
    /// clear every caster between the light and the region.</param>
    /// <param name="sweepRadius">The swept sphere's radius in world units, or a negative value for the texel's
    /// half-diagonal, the radius that covers its column.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public IrradianceLightView(IrradianceField field, Double3 towardLight, Double3 center, double halfWidth, int resolution, double distance, double sweepRadius = -1.0) {
        ArgumentNullException.ThrowIfNull(argument: field);

        m_toward = towardLight.Normalize();
        m_resolution = resolution;
        m_distance = distance;
        TexelSize = ((2.0 * halfWidth) / resolution);
        SweepRadius = ((sweepRadius >= 0.0) ? sweepRadius : (TexelSize * Math.Sqrt(d: 0.5)));

        var helper = ((Math.Abs(value: m_toward.Y) < 0.9) ? new Double3(X: 0.0, Y: 1.0, Z: 0.0) : new Double3(X: 1.0, Y: 0.0, Z: 0.0));

        m_right = Double3.Cross(a: helper, b: m_toward).Normalize();
        m_up = Double3.Cross(a: m_toward, b: m_right);
        m_origin = (center + (m_toward * distance));
        m_depth = new double[(resolution * resolution)];

        for (var row = 0; (row < resolution); row++) {
            for (var column = 0; (column < resolution); column++) {
                var start = ((m_origin + (m_right * (((column + 0.5) * TexelSize) - halfWidth))) + (m_up * (((row + 0.5) * TexelSize) - halfWidth)));
                var sweep = field.Sweep(direction: -m_toward, maxDistance: (2.0 * distance), origin: start, radius: SweepRadius);

                m_depth[((row * resolution) + column)] = sweep.Kind switch {
                    IrradianceRayKind.Miss => double.PositiveInfinity,
                    IrradianceRayKind.Hit => sweep.Distance,
                    _ => CountUnresolved(),
                };
            }
        }

        double CountUnresolved() {
            Unresolved++;

            return double.NaN;
        }
    }

    /// <summary>Gets a texel's side, in world units.</summary>
    public double TexelSize { get; }
    /// <summary>Gets the swept sphere's radius, in world units.</summary>
    public double SweepRadius { get; }
    /// <summary>Gets the count of texels whose sweep could not finish; their receivers require a shadow ray.</summary>
    public int Unresolved { get; private set; }

    /// <summary>Returns whether a receiver sees the light, by its texel's depth and a bias of
    /// <c>r (1 + sin θ) / cos θ</c> plus a small constant, with r the swept sphere's radius and θ the angle between the
    /// receiver's normal and the light: <c>r / cos θ</c> is how far short a sphere resting on the receiver's own surface
    /// stops, and <c>r tan θ</c> how much deeper that surface can lie anywhere within the texel's column. A caster nearer
    /// the receiver along the light than this bias is not seen.</summary>
    /// <param name="point">The receiver's surface point, in world units.</param>
    /// <param name="normal">The receiver's unit normal.</param>
    /// <returns>Whether it is lit; <see langword="false"/> when it faces away; <see langword="null"/> when it lies
    /// outside the swept volume or its texel is unresolved, where the receiver marches its own shadow ray.</returns>
    public bool? Lit(Double3 point, Double3 normal) {
        var cosine = Double3.Dot(a: normal, b: m_toward);

        if (cosine <= 0.0) {
            return false;
        }

        var offset = (point - m_origin);
        var half = (0.5 * m_resolution);
        var column = ((int)Math.Floor(d: ((Double3.Dot(a: offset, b: m_right) / TexelSize) + half)));
        var row = ((int)Math.Floor(d: ((Double3.Dot(a: offset, b: m_up) / TexelSize) + half)));

        if ((column < 0) || (row < 0) || (column >= m_resolution) || (row >= m_resolution)) {
            return null;
        }

        var travel = -Double3.Dot(a: offset, b: m_toward);
        var depth = m_depth[((row * m_resolution) + column)];

        if ((travel < 0.0) || (travel > (2.0 * m_distance)) || double.IsNaN(d: depth)) {
            return null;
        }

        var sine = Math.Sqrt(d: Math.Max(val1: 0.0, val2: (1.0 - (cosine * cosine))));
        var bias = (((SweepRadius * (1.0 + sine)) / cosine) + Bias);

        return (travel <= (depth + bias));
    }

    private const double Bias = 0.002;
}
