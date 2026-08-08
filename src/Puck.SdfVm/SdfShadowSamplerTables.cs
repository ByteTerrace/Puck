using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>
/// The host-baked sampler table the world kernels' area-light shadow estimator reads: the two-dimensional digital
/// net's direction numbers followed by the sun disc's quantized polar map.
/// </summary>
/// <remarks>
/// <para>
/// The table exists so the shader contains no <c>sqrt</c>, <c>rsqrt</c>, <c>normalize</c>, or trigonometry on the
/// sampling path. Vulkan permits 3 ULP on <c>Sqrt</c> and 2.5 ULP on <c>FDiv</c>, so neither is a portable constant;
/// every transcendental is evaluated here in <see cref="double"/>, rounded to <see cref="float"/> exactly once, and
/// shipped as raw bits. What reaches the GPU is therefore identical on both backends by construction rather than by
/// measurement, which is the same discipline <c>SdfProgramBuilder</c> applies to per-shape constants: a program is
/// built once and evaluated millions of times, so derived values belong on the host.
/// </para>
/// <para>
/// The layout is <see cref="ConeDirectionTable"/>'s verbatim — this type only renames the cap half-angle to the
/// sun's angular radius and pins that layout. The build-once/upload-once caching policy lives in
/// <see cref="SdfWorldEngine"/>, which builds and uploads the table once and rebuilds it only when the sun's angular
/// radius changes.
/// </para>
/// </remarks>
internal static class SdfShadowSamplerTables {
    /// <summary>The table's length in 32-bit words.</summary>
    public const int WordCount = ConeDirectionTable.WordCount;

    /// <summary>Builds the shadow sampler table for a sun of a given angular radius.</summary>
    /// <param name="sunAngularRadius">The sun disc's angular radius in radians, in <c>[0, π/2)</c>.</param>
    /// <param name="destination">Receives exactly <see cref="WordCount"/> words.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not <see cref="WordCount"/> long.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sunAngularRadius"/> is negative, not a number, or at or above <c>π/2</c>.</exception>
    public static void Build(double sunAngularRadius, Span<uint> destination) =>
        ConeDirectionTable.Build(capHalfAngleRadians: sunAngularRadius, destination: destination);
}
