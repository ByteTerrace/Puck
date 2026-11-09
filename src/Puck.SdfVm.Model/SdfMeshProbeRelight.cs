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
    public static bool RelightDue(int sweep, int phase, int interleave, float age, float bound) =>
        (bound > 0 && age >= bound) || ((uint)sweep + (uint)phase) % (uint)interleave == 0;
    /// <summary>Two exact allocation-token words followed by the cached hit words.</summary>
    public const int CacheWords = 2 + SdfMeshProbeTrace.HitWords;
    /// <summary>Six RGB radiance values, six local values and the visibility bits.</summary>
    public const int OutputWords = 36 + SdfMeshIrradianceProbe.LightWords;
    /// <summary>Flags, allocation token, cache slot, the previous output, then the geometric record.</summary>
    public const int InputWords = RecordInputWord + SdfMeshIrradianceProbe.ProbeWords;
    /// <summary>The first word of the previous output a relight that reuses its local cube reads.</summary>
    public const int PreviousWord = 4;
    /// <summary>The geometric record supplied beside the relight inputs.</summary>
    public const int RecordInputWord = PreviousWord + OutputWords;
    /// <summary>Threads per selection group.</summary>
    public const int SelectGroupSize = 64;
    /// <summary>The thread groups one probe's relight spreads over, each shading <see cref="SdfMeshProbeTrace.GroupSize"/>
    /// of its hit texels, one a lane, and every slice's share of the light visibility rays, so a probe takes one texel's
    /// light loop rather than one lane's several.</summary>
    public const int Slices = ((SdfMeshProbeTrace.HitCount + SdfMeshProbeTrace.GroupSize) - 1) / SdfMeshProbeTrace.GroupSize;
    /// <summary>One slice's partial relight, which the combine pass sums: six RGB varying sums, six local sums, then the
    /// visibility bits the slice set and those it cleared.</summary>
    public const int PartialWords = 36 + (2 * SdfMeshIrradianceProbe.LightWords);
    /// <summary>The diagnostic lighting output followed by the GPU-packed publication record.</summary>
    public const int ResultWords = OutputWords + SdfMeshIrradianceProbe.ProbeWords;
    /// <summary>Six axis luminances, sky visibility and source/trace flags in one camera-meter answer.</summary>
    public const int MeterOutputWords = 8;
    /// <summary>Float4 rows preceding materials: counts, sources, gains, three window pairs, axis totals and sky.</summary>
    public const int HeaderRows = 20;
    /// <summary>Reflected RGB and wrap, then emitted RGB.</summary>
    public const int MaterialRows = 2;
    /// <summary>Position/direction and range, colour and weight, cone, then bounce/index/shadow/kind.</summary>
    public const int LightRows = 4;
}
