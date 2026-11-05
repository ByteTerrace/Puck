namespace Puck.SdfVm;

/// <summary>Captured probe relight packet shared with the compute kernel.</summary>
public static class SdfMeshProbeRelight {
    /// <summary>Presence, geometry, lighting, sweep, level, offset and hit-block address for selection.</summary>
    public const int SelectionInputWords = 11;
    /// <summary>Two exact allocation-token words followed by the cached hit words.</summary>
    public const int CacheWords = 2 + SdfMeshProbeTrace.HitWords;
    /// <summary>Six RGB radiance values, six local values and the visibility bits.</summary>
    public const int OutputWords = 36 + SdfMeshIrradianceProbe.LightWords;
    /// <summary>Flags, allocation token, cache slot, optional upload address, then the previous output.</summary>
    public const int InputWords = 5 + OutputWords;
    /// <summary>Float4 rows preceding materials: counts, sources, gains, three window pairs, axis totals and sky.</summary>
    public const int HeaderRows = 20;
    /// <summary>Reflected RGB and wrap, then emitted RGB.</summary>
    public const int MaterialRows = 2;
    /// <summary>Position/direction and range, colour and weight, cone, then bounce/index/shadow/kind.</summary>
    public const int LightRows = 4;
}
