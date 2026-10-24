namespace Puck.SdfVm;

/// <summary>
/// The irradiance volume's probe record and read, shared by its CPU tracer (<c>SdfMeshIrradiance</c>) and the views
/// pass's read (<c>shade/sdf-mesh-irradiance.hlsli</c>), which <see cref="SdfIsaHlsl"/> writes them into: a probe is
/// <see cref="ProbeWords"/> words, its key (each cell coordinate's low ten bits) with the flags above it, the sky
/// visibility cube as six halves, the radiance cube as eighteen (red, green and blue of +X, -X, +Y, -Y, +Z and -Z), the
/// visibility of each light of the lights table from the probe, one bit a record from <see cref="LightsWord"/>, the
/// depth cube as six halves from <see cref="DepthWord"/>: how far the probe's rays travel about each axis before they
/// meet geometry, at <see cref="NeighboursWord"/> which of its 26 neighbouring probes its centre sees, and at
/// <see cref="PlanesWord"/> the first blocking plane along each of those directions.
/// </summary>
public static class SdfMeshIrradianceProbe {
    /// <summary>The words one probe takes in the volume table.</summary>
    public const int ProbeWords = 73;
    /// <summary>Two words per neighbour direction holding the first hit's plane relative to the probe: four halves
    /// (normal XYZ and plane distance). A zero plane means the ray reached open space. The normal faces away from
    /// the probe, so a receiver beyond the plane is occluded. Directions use the neighbour mask's order.</summary>
    public const int PlanesWord = 21;
    /// <summary>The word holding which neighbouring probes the probe's centre sees: bit <c>n</c> for the neighbour at
    /// offset <c>(x, y, z)</c>, each in -1, 0 and 1 and not all zero, where <c>n</c> is <c>9(x + 1) + 3(y + 1) + (z + 1)</c>
    /// less one past the origin's 13, so the neighbour at <c>25 - n</c> lies opposite.</summary>
    public const int NeighboursWord = 20;
    /// <summary>The share of the other answering probes' weight a probe about a point must see, across the lattice, to
    /// keep any of its own weight: its weight fades in from here to <see cref="MutualHigh"/>. A probe across a wall from
    /// the rest sees few of them and may hold light many times as bright, so it gives way entirely.</summary>
    public const float MutualLow = 0.25f;
    /// <summary>The share of the other answering probes' weight a probe must see to keep all of its weight
    /// (<see cref="MutualLow"/>).</summary>
    public const float MutualHigh = 0.75f;
    /// <summary>The first of the <see cref="LightWords"/> words holding the probe's light visibility: bit <c>i</c> of
    /// word <c>LightsWord + i / 32</c> is set when the probe's centre sees light <c>i</c> of the lights table.</summary>
    public const int LightsWord = 13;
    /// <summary>The words holding the light visibility bits: one bit a record of the lights table.</summary>
    public const int LightWords = 4;
    /// <summary>The first of the three words holding the depth cube: how far, in metres, the probe sees along each axis
    /// before the first geometry on that axis, an open ray counting the trace's reach, as six halves in the cube's
    /// axis order. Rays beside the axis cannot extend its blocking depth.</summary>
    public const int DepthWord = 17;
    /// <summary>The distance, in metres, over which a probe's weight at a point fades to nothing as the point's distance
    /// along an axis passes the probe's depth there: a probe across a wall or slab lights nothing beyond it. It is
    /// thinner than the thinnest floor, so a probe under a floor finish lights nothing on the wall standing on
    /// it.</summary>
    public const float DepthFade = 0.04f;
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
