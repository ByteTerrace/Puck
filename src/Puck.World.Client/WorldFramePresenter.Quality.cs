using Puck.SdfVm;

namespace Puck.World.Client;

// The quality every view the presentation dresses renders at, from its render settings: each seat's view carries it, so
// a seat presented elsewhere renders its routed world at the same quality it renders the boot world at.
public sealed partial class WorldFramePresenter {
    private SdfViewQuality ViewQuality() => new() {
        SkyFieldScale = m_settings.SkyFieldScale,
        DisableAmbientOcclusion = !m_settings.AmbientOcclusion,
        DisableSoftShadows = !m_settings.Shadows,
        Temporal = m_settings.Temporal,
    };
}
