using Puck.SdfVm;

namespace Puck.World.Client;

// The quality every view the presentation dresses renders at, from its render settings: each seat's view carries it, so
// a seat presented elsewhere renders its routed world at the same quality it renders the boot world at.
public sealed partial class WorldFramePresenter {
    private SdfViewQuality ViewQuality() => ViewQualityOf(settings: m_settings);

    /// <summary>Returns the quality a presentation's own views render at under its render settings; a nested view renders
    /// at the same quality with what the world's nested-view table gives up added
    /// (<see cref="WorldRenderSettings.NestedRestriction(string?)"/>).</summary>
    /// <param name="settings">The live render settings.</param>
    /// <returns>The quality.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public static SdfViewQuality ViewQualityOf(WorldRenderSettings settings) {
        ArgumentNullException.ThrowIfNull(argument: settings);

        return new() {
            SkyFieldScale = settings.SkyFieldScale,
            DisableAmbientOcclusion = !settings.AmbientOcclusion,
            DisableSoftShadows = (settings.Shadows == ShadowTier.Off),
            ShadowResolution = ShadowResolutionOf(tier: settings.Shadows),
            Temporal = settings.Temporal,
        };
    }

    /// <summary>Returns the resolution a shadow tier renders the cascades at: <see cref="ShadowTier.High"/> at full
    /// resolution, <see cref="ShadowTier.Medium"/> at half and <see cref="ShadowTier.Low"/> at a quarter;
    /// <see cref="ShadowTier.Off"/>, which renders no cascade, reads as full.</summary>
    /// <param name="tier">The shadow tier.</param>
    /// <returns>The cascades' resolution.</returns>
    public static SdfShadowResolution ShadowResolutionOf(ShadowTier tier) => tier switch {
        ShadowTier.Low => SdfShadowResolution.Quarter,
        ShadowTier.Medium => SdfShadowResolution.Half,
        _ => SdfShadowResolution.Full,
    };
}
