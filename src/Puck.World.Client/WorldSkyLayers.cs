using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Session-only sky auditioning by the zero-based rows printed by world.lighting.</summary>
public sealed class WorldSkyLayers {
    private readonly HashSet<int> m_muted = [];
    private int m_solo = -1;
    /// <summary>Gets the solo row, or -1 for the complete stack.</summary>
    public int Solo => m_solo;
    /// <summary>Gets the revision consumed by the environment resolver.</summary>
    public int Revision { get; private set; }
    /// <summary>Reports whether a row is muted.</summary>
    public bool Muted(int index) => m_muted.Contains(index);
    /// <summary>Reports whether a row contributes, with mute taking precedence over solo.</summary>
    public bool Includes(int index) => ((m_solo < 0) || (m_solo == index)) && !Muted(index);
    /// <summary>Solos a row, or restores the stack with -1.</summary>
    public void SetSolo(int index) { m_solo = index; Revision++; }
    /// <summary>Mutes or restores one row.</summary>
    public void SetMuted(int index, bool muted) {
        if (muted) { m_muted.Add(index); } else { m_muted.Remove(index); }
        Revision++;
    }
    /// <summary>Removes the fallback contribution of an excluded row before included rows resolve.</summary>
    public void ClearExcluded(SdfSky sky, IReadOnlyList<WorldRenderSkyLayer> layers) {
        if (m_solo >= 0) {
            var solo = m_solo < layers.Count && Includes(m_solo) ? layers[m_solo] : null;
            if (solo is not WorldRenderSkyLayer.Gradient) { sky.StopCount = 0; }
            if (solo is not WorldRenderSkyLayer.Fog) { sky.Block.FogDensity = 0f; }
        }
        for (var index = 0; index < layers.Count; index++) {
            if (Includes(index)) { continue; }
            switch (layers[index]) {
                case WorldRenderSkyLayer.Gradient: sky.StopCount = 0; break;
                case WorldRenderSkyLayer.Fog: sky.Block.FogDensity = 0f; break;
                case WorldRenderSkyLayer.SunDisc: sky.Block.DiscLight = -1; break;
                case WorldRenderSkyLayer.Stars: sky.Block.StarBrightness = 0f; break;
                case WorldRenderSkyLayer.Clouds: sky.Block.CloudCoverage = 0f; break;
            }
        }
    }
}
