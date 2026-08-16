namespace Puck.Text;

/// <summary>A named, hash-pinned font asset and the exact Unicode scalar set Puck generates for it.</summary>
/// <param name="Name">The stable name text runs use.</param>
/// <param name="Source">The asset path, interpreted by the owning document.</param>
/// <param name="Hash">The canonical <c>sha256-64/{16 lowercase hex}</c> pin of the font bytes.</param>
/// <param name="CodePointRanges">The scalar tokens to include, such as <c>U+0020-U+007E</c>.</param>
/// <param name="Characters">Additional non-whitespace scalars to include.</param>
/// <param name="PixelSize">The rasterization em size in pixels; null uses 48.</param>
/// <param name="DistanceRange">The SDF distance band in pixels; null uses 8.</param>
/// <param name="FaceIndex">The zero-based face index for an OpenType collection; null uses 0.</param>
/// <param name="Padding">The cell padding in pixels; null uses the distance range ceiling.</param>
/// <param name="Columns">The preferred atlas column count; null uses 16.</param>
public sealed record TextFontDefinition(
    string Name,
    string Source,
    string Hash,
    IReadOnlyList<string> CodePointRanges,
    string? Characters = null,
    int? PixelSize = null,
    float? DistanceRange = null,
    int? FaceIndex = null,
    int? Padding = null,
    int? Columns = null
) {
    /// <summary>Builds the normalized generation options consumed by the in-process generator.</summary>
    public FontAtlasGenerationOptions ToGenerationOptions() {
        var distanceRange = (DistanceRange ?? SdfCoverageAtlas.DefaultDistanceRange);
        var defaultPadding = ((float.IsFinite(f: distanceRange) && (distanceRange >= int.MinValue) && (distanceRange <= int.MaxValue))
            ? (int)MathF.Ceiling(x: distanceRange)
            : 0
        );

        return new FontAtlasGenerationOptions {
            AllowedCharacters = (Characters ?? string.Empty),
            AllowedCodePointRanges = CodePointRanges,
            Columns = (Columns ?? 16),
            DistanceRange = distanceRange,
            FaceIndex = (FaceIndex ?? 0),
            FontPixelSize = (PixelSize ?? 48),
            Padding = (Padding ?? defaultPadding),
        };
    }
}
/// <summary>The named font catalog a document exposes to all of its text-bearing surfaces.</summary>
/// <param name="DefaultFont">The font used when a text run omits an explicit name.</param>
/// <param name="Fonts">The catalog rows.</param>
public sealed record TextFontCatalogDefinition(string DefaultFont, IReadOnlyList<TextFontDefinition> Fonts);
