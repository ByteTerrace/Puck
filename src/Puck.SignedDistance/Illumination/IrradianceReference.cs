namespace Puck.SignedDistance.Illumination;

/// <summary>A reference estimate of the irradiance at a point.</summary>
/// <param name="Irradiance">The normalized irradiance: the cosine-weighted mean of the radiance arriving over the
/// hemisphere about the normal, so a uniform environment of colour c gives c.</param>
/// <param name="Paths">The count of paths the estimate averaged.</param>
/// <param name="Unresolved">The count of paths that met a ray the field could not resolve and so carried no light; an
/// estimate with any is not a reference answer.</param>
public readonly record struct IrradianceEstimate(Double3 Irradiance, int Paths, int Unresolved);
/// <summary>
/// The irradiance reference every law holds the radiance cache to: a quasi-Monte Carlo path estimate over the field,
/// with rays cast through <see cref="IrradianceField"/> (the one CPU interpreter), normals from its gradient, and the
/// surfaces, direct light and sky its caller supplies. It is independent of the cache's lattice, cells and solve. Every
/// path's samples come from a Halton sequence, a fixed pair of prime bases a bounce, so the estimate is the same on every
/// run and machine; it accumulates in scalar double arithmetic in a written order. Exactly zero diffuse reflectance
/// skips direct and screen queries and later reflections that cannot contribute; the hit's emission remains independent.
/// </summary>
public sealed class IrradianceReference {
    private static readonly int[] Primes = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71];

    // The height a path's rays launch from, with its interval certified (IrradianceCells.Launch): clear of the march's
    // accept threshold, so the first sample of a ray is not read as its own surface.
    private const double LaunchHeight = 0.004;

    private readonly IrradianceField m_field;
    private readonly IrradianceSurfaces m_surfaces;
    private readonly double m_exitDistance;
    private readonly double m_feedbackGain;

    /// <summary>Initializes a new instance of the <see cref="IrradianceReference"/> class.</summary>
    /// <param name="field">The field.</param>
    /// <param name="surfaces">The surfaces, direct light and sky.</param>
    /// <param name="exitDistance">The distance, in world units, past which a ray that met nothing has left the world and
    /// reads the sky: the far distance a camera treats as sky.</param>
    /// <param name="feedbackGain">The finite gain in [0, 1] applied once at each reflected feedback hop.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="surfaces"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The feedback gain is not finite or lies outside [0, 1].</exception>
    public IrradianceReference(IrradianceField field, IrradianceSurfaces surfaces, double exitDistance, double feedbackGain = 1d) {
        ArgumentNullException.ThrowIfNull(argument: field);
        ArgumentNullException.ThrowIfNull(argument: surfaces);
        if (!double.IsFinite(feedbackGain) || feedbackGain < 0d || feedbackGain > 1d) { throw new ArgumentOutOfRangeException(nameof(feedbackGain)); }

        m_field = field;
        m_surfaces = surfaces;
        m_exitDistance = exitDistance;
        m_feedbackGain = feedbackGain;
    }

    /// <summary>Estimates the normalized irradiance at a surface point.</summary>
    /// <param name="point">The point, in world units.</param>
    /// <param name="normal">The unit normal.</param>
    /// <param name="bounces">The count of reflections a path follows after its first hit: zero gives the light that
    /// arrives straight from emitters, lit surfaces and the sky.</param>
    /// <param name="paths">The count of paths, at most the sequence's useful length.</param>
    /// <returns>The estimate.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bounces"/> is negative or needs more prime bases
    /// than the sequence holds, or <paramref name="paths"/> is not positive.</exception>
    public IrradianceEstimate Estimate(Double3 point, Double3 normal, int bounces, int paths) => EstimateCore(point, normal, bounces, paths).Total;

    /// <summary>Estimates independent first-hit source categories and later feedback on the same paths, without
    /// deriving proportions from their final sum.</summary>
    /// <param name="point">The receiver surface point.</param>
    /// <param name="normal">The unit receiver normal.</param>
    /// <param name="bounces">The number of reflections after the first hit, from zero through nine.</param>
    /// <param name="paths">The positive number of Halton paths.</param>
    /// <returns>The source means and actual unresolved count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The bounce depth or path count is outside its supported range.</exception>
    public IrradianceSourceEstimate EstimateSources(Double3 point, Double3 normal, int bounces, int paths) => EstimateCore(point, normal, bounces, paths).Sources;

    /// <summary>Estimates physical incoming radiance along a captured first ray, then follows the same attributed
    /// finite-bounce paths as <see cref="EstimateSources"/>. This independent reference has the field's query budget,
    /// not the renderer's bounded Near allowance, and consumes no cached radiance.</summary>
    /// <param name="origin">The finite, already-launched first-ray origin. No second surface launch is applied.</param>
    /// <param name="direction">The finite nonzero captured direction, normalized before averaging paths.</param>
    /// <param name="bounces">The reflections after the fixed first hit, from zero through nine.</param>
    /// <param name="paths">The number of paths, from one through 256. Their first direction is identical; later
    /// reflections use the ordinary independent Halton samples for each path and bounce.</param>
    /// <returns>Independent source means and the actual unresolved-path count. Any unresolved path prevents this
    /// from being a reference answer. The field's counters retain every ray and point query performed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The origin or direction is not finite, the direction is zero,
    /// or the bounce depth or path count is outside its supported range.</exception>
    public IrradianceSourceEstimate EstimateIncidentSources(Double3 origin, Double3 direction, int bounces, int paths = 64) {
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(origin.Z)) {
            throw new ArgumentOutOfRangeException(nameof(origin), "The captured ray origin must be finite.");
        }
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y) || !double.IsFinite(direction.Z) || direction == Double3.Zero) {
            throw new ArgumentOutOfRangeException(nameof(direction), "The captured ray direction must be finite and nonzero.");
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paths, 256);
        // Scaling first preserves a finite nonzero direction even when its raw squared length overflows or underflows.
        var scale = Math.Max(Math.Abs(direction.X), Math.Max(Math.Abs(direction.Y), Math.Abs(direction.Z)));
        var unit = new Double3(direction.X / scale, direction.Y / scale, direction.Z / scale).Normalize();
        return EstimateCore(origin, default, bounces, paths, firstDirection: unit).Sources;
    }

    private (IrradianceEstimate Total, IrradianceSourceEstimate Sources) EstimateCore(Double3 point, Double3 normal, int bounces, int paths, Double3? firstDirection = null) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: bounces);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: bounces, other: ((Primes.Length / 2) - 1));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: paths);

        var sum = Double3.Zero;
        var contributions = default(IrradianceContributions);
        var unresolved = 0;

        for (var path = 0; (path < paths); path++) {
            Double3 radiance;
            bool resolved;
            IrradianceContributions attributed;
            if (firstDirection is { } direction) {
                radiance = IncidentRay(point, direction, path + 1, 0, bounces, out resolved, out attributed);
            }
            else {
                radiance = Incident(point, normal, path + 1, 0, bounces, out resolved, out attributed);
            }

            if (!resolved) {
                unresolved++;

                continue;
            }

            sum += radiance;
            contributions += attributed;
        }

        return (new IrradianceEstimate(Irradiance: (sum * (1.0 / paths)), Paths: paths, Unresolved: unresolved), new IrradianceSourceEstimate(contributions * (1.0 / paths), paths, unresolved));
    }
    /// <summary>Returns a cosine-weighted direction on the hemisphere about a normal from two numbers in [0, 1).</summary>
    /// <param name="normal">The unit normal.</param>
    /// <param name="u">The first number.</param>
    /// <param name="v">The second number.</param>
    /// <returns>The unit direction.</returns>
    public static Double3 CosineDirection(Double3 normal, double u, double v) {
        var radius = Math.Sqrt(d: u);
        var angle = ((2.0 * Math.PI) * v);
        var helper = ((Math.Abs(value: normal.X) < 0.9) ? new Double3(X: 1.0, Y: 0.0, Z: 0.0) : new Double3(X: 0.0, Y: 1.0, Z: 0.0));
        var tangent = Double3.Cross(a: helper, b: normal).Normalize();
        var bitangent = Double3.Cross(a: normal, b: tangent);
        var height = Math.Sqrt(d: Math.Max(val1: 0.0, val2: (1.0 - u)));

        return (((tangent * (radius * Math.Cos(d: angle))) + (bitangent * (radius * Math.Sin(a: angle)))) + (normal * height)).Normalize();
    }
    /// <summary>Returns element <paramref name="index"/> of the radical-inverse sequence in a prime base.</summary>
    /// <param name="index">The element, positive.</param>
    /// <param name="primeBase">The base.</param>
    /// <returns>A number in [0, 1).</returns>
    public static double RadicalInverse(int index, int primeBase) {
        var inverse = (1.0 / primeBase);
        var fraction = inverse;
        var result = 0.0;

        while (index > 0) {
            result += ((index % primeBase) * fraction);
            index /= primeBase;
            fraction *= inverse;
        }

        return result;
    }

    // The radiance arriving at a point from one sampled direction, following the path through its remaining bounces.
    private Double3 Incident(Double3 point, Double3 normal, int path, int bounce, int bounces, out bool resolved, out IrradianceContributions contributions) {
        var direction = CosineDirection(
            normal: normal,
            u: RadicalInverse(index: path, primeBase: Primes[(2 * bounce)]),
            v: RadicalInverse(index: path, primeBase: Primes[((2 * bounce) + 1)])
        );

        if (IrradianceCells.Launch(field: m_field, height: LaunchHeight, normal: normal, surface: point) is not { } launch) {
            resolved = false;
            contributions = default;
            return Double3.Zero;
        }
        return IncidentRay(launch.Point, direction, path, bounce, bounces, out resolved, out contributions);
    }

    private Double3 IncidentRay(Double3 origin, Double3 direction, int path, int bounce, int bounces,
        out bool resolved, out IrradianceContributions contributions) {
        resolved = true;
        contributions = default;
        var ray = m_field.Cast(direction: direction, maxDistance: m_exitDistance, origin: origin);

        if (ray.Kind == IrradianceRayKind.Miss) {
            var sky = m_surfaces.Sky(arg: direction);
            contributions = new(default, default, default, sky, default);
            return sky;
        }

        if ((ray.Kind == IrradianceRayKind.Unresolved) || !m_field.TryGradient(gradient: out var hitNormal, point: ray.Point)) {
            resolved = false;

            return Double3.Zero;
        }

        // The signed gradient points into free space. A finite-stencil normal at an accepted grazing edge can
        // follow the incoming ray; flipping it would launch the reflection into the solid instead.

        var reflected = m_surfaces.Reflection(ray.Point, hitNormal, ray.Material);
        var direct = reflected == Double3.Zero ? Double3.Zero
            : m_surfaces.Direct(arg1: ray.Point, arg2: hitNormal, arg3: ray.Material);
        var screens = reflected == Double3.Zero ? Double3.Zero
            : m_surfaces.Screens(arg1: ray.Point, arg2: hitNormal, arg3: ray.Material);
        var arriving = direct + screens;
        var feedback = Double3.Zero;
        if (bounce < bounces && reflected != Double3.Zero && m_feedbackGain > 0d) {
            feedback = Incident(bounce: bounce + 1, bounces: bounces, normal: hitNormal, path: path,
                point: ray.Point, resolved: out resolved, contributions: out _) * m_feedbackGain;
            arriving += feedback;
        }
        var emission = m_surfaces.Emission(arg: ray.Material);
        contributions = new(Double3.Multiply(reflected, direct), Double3.Multiply(reflected, feedback), emission,
            default, Double3.Multiply(reflected, screens));
        return Double3.Multiply(reflected, arriving) + emission;
    }
}
