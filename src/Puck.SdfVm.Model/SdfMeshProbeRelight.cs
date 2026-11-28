namespace Puck.SdfVm;

/// <summary>Captured probe relight packet shared with the compute kernel.</summary>
public static class SdfMeshProbeRelight {
    /// <summary>Geometry, level, offset, cached hit slot and its two-word allocation token for a selected probe.</summary>
    public const int SelectionInputWords = 8;
    /// <summary>Scene version, change limits and three spacings preceding the geometry changes.</summary>
    public const int SelectionHeaderRows = 5;
    /// <summary>Moving-light selection cycles between visits to a level's phase. Stable light takes full sweeps.</summary>
    public static int InterleaveOf(int level) => level == 0 ? 1 : level == 1 ? 4 : 8;
    /// <summary>A level's overload age target, scaled by its interleave; zero means no authored age target.</summary>
    public static float AgeBoundOf(int level, float target) => target * InterleaveOf(level);
    /// <summary>Eligibility after geometry and publication requirements: force age, otherwise visit the cell's phase.</summary>
    public static bool RelightDue(int sweep, int phase, int interleave, float age, float bound) =>
        (bound > 0 && age >= bound) || ((uint)sweep + (uint)phase) % (uint)interleave == 0;
    /// <summary>Two exact allocation-token words followed by the cached hit words.</summary>
    public const int CacheWords = CacheResultWord + OutputWords;
    /// <summary>The retained relight result after the allocation token and hit words.</summary>
    public const int CacheResultWord = 2 + SdfMeshProbeTrace.HitWords;
    /// <summary>Relative per-channel convergence bound shared by host and device.</summary>
    public const float ConvergedShare = 0.02f;
    /// <summary>Absolute convergence bound in nits shared by host and device.</summary>
    public const float ConvergedFloor = 1.0e-3f;
    /// <summary>Six RGB radiance values, six local values, the visibility bits, then six RGB sky-light values.</summary>
    public const int OutputWords = SkyOutputWord + 18;
    /// <summary>The first word of a relight output's six RGB sky-light values.</summary>
    public const int SkyOutputWord = 36 + SdfMeshIrradianceProbe.LightWords;
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
    /// <summary>One slice's partial relight, which the combine pass sums: six RGB varying sums, six local sums, the
    /// visibility bits the slice set and those it cleared, then six RGB sky-light sums.</summary>
    public const int PartialWords = PartialSkyWord + 18;
    /// <summary>The first word of a partial relight's six RGB sky-light sums.</summary>
    public const int PartialSkyWord = 36 + (2 * SdfMeshIrradianceProbe.LightWords);
    /// <summary>The diagnostic lighting output followed by the GPU-packed publication record.</summary>
    public const int ResultWords = MovedWord + 1;
    /// <summary>The final result word: nonzero when either light cube moved beyond convergence.</summary>
    public const int MovedWord = OutputWords + SdfMeshIrradianceProbe.ProbeWords;
    /// <summary>One camera-meter answer: the light the sample receives along each of its six axes (+X, -X, +Y, -Y, +Z,
    /// -Z), RGB in nits (the radiance and sky-light cubes summed, as a probe's indirect light), then its sky visibility,
    /// then its source and trace flags.</summary>
    public const int CameraSampleOutputWords = 20;
    /// <summary>The header row whose third lane carries the lighting's epoch (<see cref="SdfMeshIrradianceProbe.EpochMask"/>),
    /// beside the last two axis totals, and whose fourth carries the count of emitting patches the rows hold after the
    /// lights (<see cref="EmitterRows"/>).</summary>
    public const int EpochRow = 10;
    /// <summary>The first of the float4 rows holding the sky's radiance about each hit texel's ray, gain applied, zero when
    /// the sky source is off: one row a texel.</summary>
    public const int SkyRow = 11;
    /// <summary>Float4 rows preceding materials: counts, sources, gains, three window pairs, axis totals and the sky's
    /// radiance per hit texel.</summary>
    public const int HeaderRows = SkyRow + SdfMeshProbeTrace.HitCount;
    /// <summary>Reflected RGB and wrap. A hit's emission is no material row: the relight samples every emitting patch from
    /// the probe's centre instead (<see cref="EmitterRows"/>).</summary>
    public const int MaterialRows = 1;
    /// <summary>Position/direction and range, colour and weight, cone, then bounce/index/shadow/kind.</summary>
    public const int LightRows = 4;
    /// <summary>One emitting patch, after the lights: its centroid and range, its area-weighted normal sum and area, then
    /// the radiance it emits in nits, the emission gain applied.</summary>
    public const int EmitterRows = 3;
}
