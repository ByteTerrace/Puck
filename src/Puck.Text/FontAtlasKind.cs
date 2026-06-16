namespace Puck.Text;

/// <summary>
/// Specifies how a <see cref="FontAtlas"/> encodes glyph coverage in its image, which in turn determines
/// how the atlas must be sampled at draw time.
/// </summary>
/// <remarks>
/// The distance-field kinds (<see cref="Sdf"/>, <see cref="Psdf"/>, <see cref="Msdf"/>,
/// <see cref="Mtsdf"/>) stay crisp under scaling because edges are reconstructed from encoded distances
/// rather than from rasterized coverage; see <see cref="MtsdfSampling.UsesDistanceField(FontAtlasKind)"/>.
/// The mask kinds store coverage directly and are sampled as plain alpha. The expected sampling mode for
/// each kind is given by <see cref="MtsdfSampling.ExpectedMode(FontAtlasKind)"/>.
/// </remarks>
public enum FontAtlasKind {
    /// <summary>A one-bit coverage mask: each texel is fully inside or fully outside the glyph.</summary>
    HardMask,
    /// <summary>An anti-aliased coverage mask storing fractional edge coverage as alpha.</summary>
    SoftMask,
    /// <summary>A single-channel signed distance field.</summary>
    Sdf,
    /// <summary>A single-channel pseudo signed distance field, which preserves sharp corners better than a plain <see cref="Sdf"/>.</summary>
    Psdf,
    /// <summary>A multi-channel signed distance field whose three channels are combined by median to reconstruct the edge.</summary>
    Msdf,
    /// <summary>A multi-channel signed distance field with a true signed distance carried in a fourth channel, combining the corner fidelity of <see cref="Msdf"/> with the soft-shadow capability of a plain <see cref="Sdf"/>.</summary>
    Mtsdf
}
