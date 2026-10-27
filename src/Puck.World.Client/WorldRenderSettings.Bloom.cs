using Puck.Hosting;

namespace Puck.World;

public sealed partial class WorldRenderSettings {
    private bool? m_bloomOverride;

    private BloomQuality m_bloomQuality = BloomQuality.High;
    private readonly Dictionary<string, BloomQuality> m_bloomViews = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<int, float> m_bloomScales = [];

    /// <summary>Reads one seat's independent accessibility preference.</summary>
    public float BloomScale(int seat) => m_bloomScales.GetValueOrDefault(defaultValue: 1f, key: seat);
    /// <summary>Changes only the named seat's scatter scale.</summary>
    public void SetBloomScale(int seat, float scale) {
        if ((seat < 0) || !float.IsFinite(f: scale) || (scale < 0f) || (scale > 1f)) { throw new ArgumentOutOfRangeException(paramName: nameof(scale)); }
        m_bloomScales[seat] = scale;
        m_revision++;
    }

    /// <summary>The live operator enable override; null follows each camera's authored settings.</summary>
    public bool? BloomOverride { get => m_bloomOverride; set { m_bloomOverride = value; m_revision++; } }
    /// <summary>The quality preset's bloom gate.</summary>
    public BloomQuality BloomQuality { get => m_bloomQuality; set { m_bloomQuality = value; m_revision++; } }

    /// <summary>Sets or clears a named view's quality override.</summary>
    public void SetBloomQuality(string view, BloomQuality? quality) {
        if (quality is { } chosen) { m_bloomViews[view] = chosen; } else { m_bloomViews.Remove(key: view); }
        m_revision++;
    }
    /// <summary>The resolved per-view gate.</summary>
    public BloomQuality BloomQualityOf(string view) => m_bloomViews.GetValueOrDefault(view, m_bloomViews.GetValueOrDefault(defaultValue: m_bloomQuality, key: "*"));
    /// <summary>Resolves the authored world/camera choice with this seat's independent accessibility scale.</summary>
    public CameraBloom ResolveBloom(WorldRenderDefaults render, CameraBloom? camera = null, float scale = 1f) {
        var authored = (camera ?? (render.Bloom ?? new CameraBloom()));
        var scatter = m_bloomOverride switch { false => 0f, true => ((authored.Scatter > 0f) ? authored.Scatter : .04f), _ => authored.Scatter };

        return authored with { Scatter = Math.Clamp(max: 1f, min: 0f, value: (scatter * scale)) };
    }
}
