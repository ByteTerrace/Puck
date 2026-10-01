using Puck.SdfVm;

namespace Puck.World.Client;

// The quality every view the presentation dresses renders at, from its render settings: each seat's view carries it, so
// a seat presented elsewhere renders its routed world at the same quality it renders the boot world at.
public sealed partial class WorldFramePresenter {
    private SdfViewQuality ViewQuality() => new() {
        // Shadow reach is continuous: zero skips the march; (0,1) scales gather + march reach; one uses the engine's 0
        // sentinel for full reach.
        DisableAmbientOcclusion = !m_settings.AmbientOcclusion,
        DisableSoftShadows = (m_settings.ShadowReach <= 0f),
        // The far-field isolator (world.far-field) ships ON, so the view's flag is the negated "disable" side.
        DisableFarBound = !m_settings.FarBound,
        ShadowDistanceScale = ((m_settings.ShadowReach >= 1f)
            ? 0f
            : m_settings.ShadowReach),
        // Dense crowds use bounded shading work; the independent crowd-radius policy controls which avatars cast.
        UseCameraTileShadowMask = (m_settings.ShadowMask switch {
            ShadowMaskMode.ExactGather => false,
            ShadowMaskMode.CameraTile => true,
            _ => (m_client.ActivePeerCount >= 16),
        }),
        UseFastSoftShadowMarch = (m_settings.ShadowMarch switch {
            ShadowMarchMode.Exact => false,
            ShadowMarchMode.Fast => true,
            _ => (m_client.ActivePeerCount >= 16),
        }),
        UseFastAmbientOcclusion = (m_settings.AmbientOcclusionQuality switch {
            AmbientOcclusionMode.Exact => false,
            AmbientOcclusionMode.Fast => true,
            _ => (m_client.ActivePeerCount >= 16),
        }),
        Temporal = m_settings.Temporal,
        MarchSeed = m_settings.MarchSeed,
    };
}
