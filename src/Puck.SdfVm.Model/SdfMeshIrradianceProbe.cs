namespace Puck.SdfVm;

/// <summary>
/// The irradiance volume's probe record and read, shared by its CPU tracer (<c>SdfMeshIrradiance</c>) and the views
/// pass's read (<c>shade/sdf-mesh-irradiance.hlsli</c>), which <see cref="SdfIsaHlsl"/> writes them into: a probe is
/// <see cref="ProbeWords"/> words, its key (each cell coordinate's low ten bits) with the flags above it, the sky
/// visibility cube as six halves, the radiance cube as eighteen (red, green and blue of +X, -X, +Y, -Y, +Z and -Z), the
/// visibility of each light of the lights table from the probe, one bit a record from <see cref="LightsWord"/>, at
/// <see cref="NeighboursWord"/> which of its 26 neighbouring probes its centre sees, and at <see cref="PlanesWord"/> the
/// first blocking plane along each of those 26 lattice directions, which bound what the probe lights.
/// </summary>
public static class SdfMeshIrradianceProbe {
    /// <summary>The words one probe takes in the volume table.</summary>
    public const int ProbeWords = 70;
    /// <summary>Two words per lattice direction holding the first hit's plane relative to the probe: four halves
    /// (normal XYZ and plane distance). A zero plane means the ray reached open space. The normal faces away from
    /// the probe, so a receiver beyond the plane is occluded. Directions use the neighbour mask's order, and the 26
    /// of them are the 3×3 directions of each face of the cube about the probe, so a read blends the four about a
    /// receiver's direction (<c>SdfMeshIrradiance.PlaneVisibility</c>, <c>sdfMeshProbePlaneVisibility</c>).</summary>
    public const int PlanesWord = 18;
    /// <summary>The word holding which neighbouring probes the probe's centre sees: bit <c>n</c> for the neighbour at
    /// offset <c>(x, y, z)</c>, each in -1, 0 and 1 and not all zero, where <c>n</c> is <c>9(x + 1) + 3(y + 1) + (z + 1)</c>
    /// less one past the origin's 13, so the neighbour at <c>25 - n</c> lies opposite.</summary>
    public const int NeighboursWord = 17;
    /// <summary>The share of the other answering probes' weight a probe about a point must see, across the lattice, to
    /// keep any of its own weight: its weight fades in from here to <see cref="MutualHigh"/>. A probe across a wall from
    /// the rest sees few of them and may hold light many times as bright, so it gives way entirely.</summary>
    public const float MutualLow = 0.25f;
    /// <summary>The share of the other answering probes' weight a probe must see to keep all of its weight
    /// (<see cref="MutualLow"/>).</summary>
    public const float MutualHigh = 0.75f;
    /// <summary>The share of the answering probes' total weight at which a separating probe's veto reaches full
    /// strength: an eighth, an equal corner's share at a cell's centre. A lighter separator vetoes by its share of
    /// that, and is separated by others the same way, so a probe entering the eight-corner set at a cell boundary with
    /// no trilinear weight vetoes nothing and the gather is continuous across the lattice planes.</summary>
    public const float SeparatorShare = 0.125f;
    /// <summary>The first of the <see cref="LightWords"/> words holding the probe's light visibility: bit <c>i</c> of
    /// word <c>LightsWord + i / 32</c> is set when the probe's centre sees light <c>i</c> of the lights table.</summary>
    public const int LightsWord = 13;
    /// <summary>The words holding the light visibility bits: one bit a record of the lights table.</summary>
    public const int LightWords = 4;
    /// <summary>The distance, in metres, over which a probe's weight at a point fades to nothing as the point passes
    /// one of the probe's blocking planes: a probe across a wall or slab lights nothing beyond it. It is thinner than
    /// the thinnest floor, so a probe under a floor finish lights nothing on the wall standing on it.</summary>
    public const float DepthFade = 0.04f;
    /// <summary>The key flag a written probe carries; a slot without it holds no probe.</summary>
    public const uint WrittenFlag = (1u << 30);
    /// <summary>The key flag of a probe inside geometry, which views never read.</summary>
    public const uint InvalidFlag = (1u << 31);
    /// <summary>How far, in metres, a shaded point is pushed along its surface normal before the probes about it are
    /// read and tested against their blocking planes: a little more than <see cref="DepthFade"/>, so a probe behind the
    /// surface's own wall, whose plane the surface lies on, rejects the pushed point outright, and less than the
    /// thinnest wall, so a surface facing a near wall never reads the room beyond it; it is metres, not a share of the
    /// probe spacing, because the walls it must stay inside do not scale with the lattice.</summary>
    public const float NormalBias = 0.05f;
    /// <summary>The share of a cell a probe may lie behind a shaded surface's plane and still light it. A probe farther
    /// behind lies past the wall or slab the surface belongs to, in a room that may be lit many times as brightly, and
    /// lights the surface only where no probe lies in front of it.</summary>
    public const float Behind = 0.1f;
}
