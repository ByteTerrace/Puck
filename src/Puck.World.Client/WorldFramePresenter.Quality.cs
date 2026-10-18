using Puck.SdfVm;

namespace Puck.World.Client;

// The quality every view the presentation dresses renders at, from its render settings: each seat's view carries it, so
// a seat presented elsewhere renders its routed world at the same quality it renders the boot world at.
public sealed partial class WorldFramePresenter {
    private SdfViewQuality ViewQuality() => new() {
        SkyFieldScale = m_settings.SkyFieldScale,
        DisableAmbientOcclusion = !m_settings.AmbientOcclusion,
        DisableSoftShadows = (m_settings.Shadows == ShadowTier.Off),
        ShadowResolution = ShadowResolutionOf(tier: m_settings.Shadows),
        Temporal = m_settings.Temporal,
    };

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
