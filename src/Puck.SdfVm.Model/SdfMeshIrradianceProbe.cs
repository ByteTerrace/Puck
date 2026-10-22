namespace Puck.SdfVm;

/// <summary>
/// The irradiance volume's probe record and read, shared by its CPU tracer (<c>SdfMeshIrradiance</c>) and the views
/// pass's read (<c>shade/sdf-mesh-irradiance.hlsli</c>), which <see cref="SdfIsaHlsl"/> writes them into: a probe is
/// <see cref="ProbeWords"/> words, its key (each cell coordinate's low ten bits) with the flags above it, the sky
/// visibility cube as six halves, the radiance cube as eighteen (red, green and blue of +X, -X, +Y, -Y, +Z and -Z), the
/// visibility of each light of the lights table from the probe, one bit a record from <see cref="LightsWord"/>, and
/// the depth cube as six halves from <see cref="DepthWord"/>: how far the probe's rays travel about each axis before
/// they meet geometry.
/// </summary>
public static class SdfMeshIrradianceProbe {
    /// <summary>The words one probe takes in the volume table.</summary>
    public const int ProbeWords = 20;
    /// <summary>The first of the <see cref="LightWords"/> words holding the probe's light visibility: bit <c>i</c> of
    /// word <c>LightsWord + i / 32</c> is set when the probe's centre sees light <c>i</c> of the lights table.</summary>
    public const int LightsWord = 13;
    /// <summary>The words holding the light visibility bits: one bit a record of the lights table.</summary>
    public const int LightWords = 4;
    /// <summary>The first of the three words holding the depth cube: the mean distance, in metres, the probe's rays
    /// within <see cref="DepthCone"/> of each axis travel before they meet geometry, an open ray counting the trace's
    /// reach, as six halves in the cube's axis order.</summary>
    public const int DepthWord = 17;
    /// <summary>The cosine of the half angle of the cone about each axis whose rays the depth cube averages: 35
    /// degrees.</summary>
    public const float DepthCone = 0.81915204f;
    /// <summary>The share of a cell over which a probe's weight at a point fades to nothing as the point's distance
    /// along the axis it lies in passes the probe's depth there: a probe across a wall or slab lights nothing beyond
    /// it.</summary>
    public const float DepthFade = 0.25f;
    /// <summary>The key flag a written probe carries; a slot without it holds no probe.</summary>
    public const uint WrittenFlag = (1u << 30);
    /// <summary>The key flag of a probe inside geometry, which views never read.</summary>
    public const uint InvalidFlag = (1u << 31);
    /// <summary>The share of a cell a shaded point is pushed along its surface normal before the probes about it are
    /// read.</summary>
    public const float NormalBias = 0.35f;
    /// <summary>The share of a cell a probe may lie behind a shaded surface's plane and still light it. A probe farther
    /// behind lies past the wall or slab the surface belongs to, in a room that may be lit many times as brightly, and
    /// lights the surface only where no probe lies in front of it.</summary>
    public const float Behind = 0.1f;
}
