namespace Puck.SdfVm;

/// <summary>Captured probe relight packet shared with the compute kernel.</summary>
public static class SdfMeshProbeRelight {
    /// <summary>Presence/phase, geometry, lighting, sweep, level, offset, hit-block token and lighting age for selection.</summary>
    public const int SelectionInputWords = 12;
    /// <summary>Selection state, change limits, three spacings and three cadence rows preceding changed boxes.</summary>
    public const int SelectionHeaderRows = 8;
    /// <summary>Moving-light selection cycles between visits to a level's phase. Stable light takes full sweeps.</summary>
    public static int InterleaveOf(int level) => level == 0 ? 1 : level == 1 ? 4 : 8;
    /// <summary>A level's overload age target, scaled by its interleave; zero means no authored age target.</summary>
    public static float AgeBoundOf(int level, float target) => target * InterleaveOf(level);
    /// <summary>Eligibility after geometry and publication requirements: force age, otherwise visit the cell's phase.</summary>
    public static bool RelightDue(int version, int phase, int interleave, float age, float bound) =>
        (bound > 0 && age >= bound) || ((uint)version + (uint)phase) % (uint)interleave == 0;
    /// <summary>Two exact allocation-token words followed by the cached hit words.</summary>
    public const int CacheWords = 2 + SdfMeshProbeTrace.HitWords;
    /// <summary>Six RGB radiance values, six local values and the visibility bits.</summary>
    public const int OutputWords = 36 + SdfMeshIrradianceProbe.LightWords;
    /// <summary>Flags, allocation token, cache slot, optional upload address, then the previous output.</summary>
    public const int InputWords = RecordInputWord + SdfMeshIrradianceProbe.ProbeWords;
    /// <summary>The geometric record supplied beside the relight inputs.</summary>
    public const int RecordInputWord = 5 + OutputWords;
    /// <summary>The diagnostic lighting output followed by the GPU-packed publication record.</summary>
    public const int ResultWords = OutputWords + SdfMeshIrradianceProbe.ProbeWords;
    /// <summary>Float4 rows preceding materials: counts, sources, gains, three window pairs, axis totals and sky.</summary>
    public const int HeaderRows = 20;
    /// <summary>Reflected RGB and wrap, then emitted RGB.</summary>
    public const int MaterialRows = 2;
    /// <summary>Position/direction and range, colour and weight, cone, then bounce/index/shadow/kind.</summary>
    public const int LightRows = 4;
}
