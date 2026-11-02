namespace Puck.SdfVm;

/// <summary>How far the key light's shadow cascades reach (<c>render.shadowCoverage</c>): the near cascade covers the
/// view's first <see cref="Near"/> metres, the far cascade the view out to <see cref="Far"/> metres or the frame's far
/// distance, whichever is nearer, and casters up to <see cref="Casters"/> metres toward the light past each cascade's
/// sphere draw into it. Each cascade spreads the same texels (<see cref="SdfViewQuality.ShadowResolution"/>) over its
/// slice's bounding sphere, so a longer reach coarsens that cascade's texel in proportion (<see cref="TexelOf"/>).
/// Presentation only.</summary>
/// <param name="Near">The depth, in metres from the eye, the near cascade covers; positive and less than
/// <paramref name="Far"/>.</param>
/// <param name="Far">The farthest depth, in metres from the eye, the far cascade covers.</param>
/// <param name="Casters">The distance, in metres, past a cascade's sphere toward the light at which casters may lie;
/// non-negative.</param>
public readonly record struct SdfShadowCoverage(float Near, float Far, float Casters) {
    /// <summary>The coverage a world that authors none renders with: 16 m near, 120 m far and 200 m of casters.</summary>
    public static SdfShadowCoverage Default { get; } = new(Near: 16f, Far: 120f, Casters: 200f);

    /// <summary>Gets whether the coverage is admissible: finite, a positive near reach below the far one, and
    /// non-negative casters.</summary>
    public bool IsValid => (float.IsFinite(f: Near) && float.IsFinite(f: Far) && float.IsFinite(f: Casters) && (Near > 0f) && (Near < Far) && (Casters >= 0f));

    /// <summary>Returns the radius, in metres, of the sphere enclosing a view's frustum slice from its eye to
    /// <paramref name="depth"/>, rounded up to a whole metre: the half extent of the cascade that covers it.</summary>
    /// <param name="depth">The slice's depth, in metres.</param>
    /// <param name="tanHalfFieldOfView">The tangent of half the view's vertical field of view.</param>
    /// <param name="aspectRatio">The view's width over its height.</param>
    /// <returns>The radius.</returns>
    public static float RadiusOf(float depth, float tanHalfFieldOfView, float aspectRatio) {
        var lateral = (tanHalfFieldOfView * tanHalfFieldOfView) * (1f + (aspectRatio * aspectRatio));
        var centerDepth = MathF.Min(depth, ((depth * (1f + lateral)) * 0.5f));

        return MathF.Ceiling(MathF.Sqrt(((depth - centerDepth) * (depth - centerDepth)) + ((depth * depth) * lateral)));
    }
    /// <summary>Returns the width, in metres, of one texel of a cascade covering a slice to <paramref name="depth"/>.</summary>
    /// <param name="depth">The slice's depth, in metres.</param>
    /// <param name="texels">The texels along each side of the cascade.</param>
    /// <param name="tanHalfFieldOfView">The tangent of half the view's vertical field of view.</param>
    /// <param name="aspectRatio">The view's width over its height.</param>
    /// <returns>The texel width.</returns>
    public static float TexelOf(float depth, int texels, float tanHalfFieldOfView, float aspectRatio) =>
        ((2f * RadiusOf(aspectRatio: aspectRatio, depth: depth, tanHalfFieldOfView: tanHalfFieldOfView)) / Math.Max(texels, 1));
    /// <summary>Describes the coverage and each key-light cascade's texel for a view of the given shape, as
    /// <c>world.budget</c> prints it.</summary>
    /// <param name="farDistance">The frame's far distance, in metres, which bounds the far cascade.</param>
    /// <param name="texels">The texels along each side of a cascade.</param>
    /// <param name="tanHalfFieldOfView">The tangent of half the view's vertical field of view.</param>
    /// <param name="aspectRatio">The view's width over its height.</param>
    /// <returns>The description.</returns>
    public string Describe(float farDistance, int texels, float tanHalfFieldOfView, float aspectRatio) {
        var far = MathF.Min(Far, farDistance);
        var near = MathF.Min(Near, far);

        return string.Create(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"shadows near {near:0.#} m at {(100f * TexelOf(aspectRatio: aspectRatio, depth: near, tanHalfFieldOfView: tanHalfFieldOfView, texels: texels)):0.#} cm a texel, far {far:0.#} m at {(100f * TexelOf(aspectRatio: aspectRatio, depth: far, tanHalfFieldOfView: tanHalfFieldOfView, texels: texels)):0.#} cm, casters {Casters:0.#} m toward the light ({texels} texels a side)"
        );
    }
}
