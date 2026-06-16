namespace Puck.Text;

/// <summary>
/// Specifies the reconstruction strategy a renderer should apply when sampling a glyph's texels, derived from the
/// source <see cref="FontAtlasKind"/> by <see cref="MtsdfSampling.ExpectedMode(FontAtlasKind)"/> and
/// carried on <see cref="TextGlyphSampling.Mode"/>.
/// </summary>
/// <remarks>
/// This enumeration collapses the six storage formats of <see cref="FontAtlasKind"/> into the four
/// distinct decoding paths a shader actually implements: the two mask kinds share a single
/// <see cref="Mask"/> path, while each distance-field family maps to its own mode.
/// </remarks>
public enum TextGlyphSamplingMode {
    /// <summary>Sample stored coverage directly as alpha; used for both hard and soft masks.</summary>
    Mask,
    /// <summary>Reconstruct coverage from a single-channel signed distance.</summary>
    Sdf,
    /// <summary>Reconstruct coverage from the median of a three-channel signed distance field.</summary>
    Msdf,
    /// <summary>Reconstruct coverage from a multi-channel signed distance field with a true-distance fourth channel.</summary>
    Mtsdf
}
