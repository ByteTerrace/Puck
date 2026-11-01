using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>Resolves a definition's <c>render.farDistance</c> to the value a frame uploads: the authored depth, or the
/// engine's pinned <see cref="SdfFrame.DefaultFarDistance"/> when the world authors none — the exact value every world
/// marched to before the field existed, so an unauthored world renders unchanged. Read from the LIVE definition every
/// frame by <see cref="WorldFramePresenter"/> (and by a mirrored session's panel frame), so a <c>world.row.set
/// render</c> lands on the next frame with no rebuild, the same way <c>render.lighting</c>/<c>render.sky</c> do.</summary>
public static class WorldRenderFarDistance {
    /// <summary>Resolves the far distance a frame uploads for <paramref name="defaults"/>.</summary>
    /// <param name="defaults">The definition's render section.</param>
    /// <returns>The authored far distance, or <see cref="SdfFrame.DefaultFarDistance"/> when none is authored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    public static float Resolve(WorldRenderDefaults defaults) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        return (defaults.FarDistance ?? SdfFrame.DefaultFarDistance);
    }
    /// <summary>Resolves the shadow cascades' reach a frame uploads for <paramref name="defaults"/>: each authored
    /// member of <c>render.shadowCoverage</c>, and <see cref="SdfShadowCoverage.Default"/>'s for each absent one.</summary>
    /// <param name="defaults">The definition's render section.</param>
    /// <returns>The coverage.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    public static SdfShadowCoverage ResolveShadowCoverage(WorldRenderDefaults defaults) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        var authored = defaults.ShadowCoverage;
        var fallback = SdfShadowCoverage.Default;

        return new SdfShadowCoverage(
            Casters: (authored?.Casters ?? fallback.Casters),
            Far: (authored?.Far ?? fallback.Far),
            Near: (authored?.Near ?? fallback.Near)
        );
    }
}
