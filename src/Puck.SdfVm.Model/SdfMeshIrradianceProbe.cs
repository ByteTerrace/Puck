namespace Puck.SdfVm;

/// <summary>
/// The irradiance volume's probe record and read, shared by its CPU tracer (<c>SdfMeshIrradiance</c>) and the views
/// pass's read (<c>shade/sdf-mesh-irradiance.hlsli</c>), which <see cref="SdfIsaHlsl"/> writes them into: a probe is
/// <see cref="ProbeWords"/> words, its key (each cell coordinate's low ten bits) with the flags above it, the sky
/// visibility cube as six halves, and the radiance cube as eighteen (red, green and blue of +X, -X, +Y, -Y, +Z and -Z).
/// </summary>
public static class SdfMeshIrradianceProbe {
    /// <summary>The words one probe takes in the volume table.</summary>
    public const int ProbeWords = 13;
    /// <summary>The key flag a written probe carries; a slot without it holds no probe.</summary>
    public const uint WrittenFlag = (1u << 30);
    /// <summary>The key flag of a probe inside geometry, which views never read.</summary>
    public const uint InvalidFlag = (1u << 31);
    /// <summary>The share of a cell a shaded point is pushed along its surface normal before the probes about it are
    /// read.</summary>
    public const float NormalBias = 0.35f;
}
