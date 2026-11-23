namespace Puck.SdfVm;

/// <summary>How far the key light's shadow entries reach (<c>render.shadowCoverage</c>): the near entry covers the first
/// <see cref="Near"/> metres from the eye in every direction, the far entry out to <see cref="Far"/> metres or the frame's
/// far distance, whichever is nearer, and casters up to <see cref="Casters"/> metres toward the light past each entry's
/// square draw into it. Each entry spreads the same texels (<see cref="SdfViewQuality.ShadowResolution"/>) over its square
/// (<see cref="SdfMeshShadowCascades.HalfExtentOf"/>), so a longer reach coarsens that entry's texel in proportion
/// (<see cref="TexelOf"/>). Presentation only.</summary>
/// <param name="Near">The distance, in metres from the eye, the near entry covers; positive and less than
/// <paramref name="Far"/>.</param>
/// <param name="Far">The farthest distance, in metres from the eye, the far entry covers.</param>
/// <param name="Casters">The distance, in metres, past an entry's square toward the light at which casters may lie;
/// non-negative.</param>
public readonly record struct SdfShadowCoverage(float Near, float Far, float Casters) {
    /// <summary>The coverage a world that authors none renders with: 16 m near, 120 m far and 200 m of casters.</summary>
    public static SdfShadowCoverage Default { get; } = new(Near: 16f, Far: 120f, Casters: 200f);

    /// <summary>Returns the width, in metres, of one texel of an entry reaching <paramref name="reach"/> metres.</summary>
    /// <param name="reach">The entry's reach, in metres.</param>
    /// <param name="texels">The texels along each side of the entry.</param>
    /// <returns>The texel width.</returns>
    public static float TexelOf(float reach, int texels) =>
        ((2f * SdfMeshShadowCascades.HalfExtentOf(reach: reach)) / Math.Max(texels, 1));
    /// <summary>Describes the coverage and each key-light entry's texel, as <c>world.budget</c> prints it.</summary>
    /// <param name="farDistance">The frame's far distance, in metres, which bounds the far entry.</param>
    /// <param name="texels">The texels along each side of an entry.</param>
    /// <returns>The description.</returns>
    public string Describe(float farDistance, int texels) {
        var far = MathF.Min(Far, farDistance);
        var near = MathF.Min(Near, far);

        return string.Create(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"shadows near {near:0.#} m at {(100f * TexelOf(reach: near, texels: texels)):0.#} cm a texel, far {far:0.#} m at {(100f * TexelOf(reach: far, texels: texels)):0.#} cm, casters {Casters:0.#} m toward the light ({texels} texels a side)"
        );
    }
}
