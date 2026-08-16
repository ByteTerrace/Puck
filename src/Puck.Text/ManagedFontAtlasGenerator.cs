using System.Globalization;
using System.Text;

namespace Puck.Text;

/// <summary>Generates a multi-channel true signed distance field (MTSDF) atlas from an OpenType font or collection
/// entirely in managed code: RGB median reconstructs sharp corners, alpha carries the exact marchable distance.</summary>
/// <remarks>
/// Puck reads Unicode mappings, metrics, TrueType quadratic outlines, and CFF/CFF2 cubic charstrings without native
/// font libraries. Cubics are reduced to quadratic segments before the shared analytic field evaluator runs (see
/// <see cref="MtsdfGlyphField"/>). The generated atlas preserves source glyph identifiers for a future shaping stage,
/// while this generator deliberately maps scalars directly rather than performing language-specific shaping or
/// ligature substitution. Pair kerning is flattened from GPOS pair positioning (the <c>kern</c> feature, PairPos
/// formats 1 and 2, extension lookups included) or, when GPOS yields none, the legacy horizontal <c>kern</c> table;
/// contextual positioning is not read.
/// </remarks>
public sealed class ManagedFontAtlasGenerator : IFontAtlasGenerator {
    private sealed record GlyphRaster(
        float Advance,
        FontGlyphGeometry Glyph,
        ushort GlyphId,
        int Unicode
    );

    private static IReadOnlyList<int> BuildCodePoints(FontAtlasGenerationOptions options) {
        if (options.AllowedCodePointRanges is null) {
            throw new ArgumentException(
                message: "Allowed code point ranges must be provided.",
                paramName: nameof(options)
            );
        }

        var codePoints = UnicodeCodePointRangeExpander.Expand(
            ranges: options.AllowedCodePointRanges,
            wildcardSelected: out var wildcardSelected
        );

        if (wildcardSelected) {
            foreach (var codePoint in UnicodeCodePointRangeExpander.EnumerateBmpCodePoints()) {
                codePoints.Add(item: codePoint);
            }
        }

        if (!string.IsNullOrEmpty(value: options.AllowedCharacters)) {
            foreach (var rune in options.AllowedCharacters.EnumerateRunes()) {
                if (!Rune.IsWhiteSpace(value: rune)) {
                    codePoints.Add(item: rune.Value);
                }
            }
        }

        return codePoints.OrderBy(keySelector: static codePoint => codePoint).ToArray();
    }
    // A kerning pair keys on glyph ids, but the atlas keys on Unicode scalars; every scalar combination that maps
    // onto the pair's glyphs carries the same adjustment.
    private static IEnumerable<FontKerningPair> BuildKerningPairs(OpenTypeFontFace font, IReadOnlyList<GlyphRaster> glyphs) {
        var unicodesByGlyph = new Dictionary<ushort, List<int>>();

        foreach (var glyph in glyphs) {
            if (!unicodesByGlyph.TryGetValue(
                key: glyph.GlyphId,
                value: out var unicodes
            )) {
                unicodes = [];
                unicodesByGlyph.Add(
                    key: glyph.GlyphId,
                    value: unicodes
                );
            }

            unicodes.Add(item: glyph.Unicode);
        }

        foreach (var pair in font.GetKerningPairs(includedGlyphs: unicodesByGlyph.Keys)) {
            if (
                !unicodesByGlyph.TryGetValue(
                key: pair.Left,
                value: out var leftUnicodes
            ) ||
                !unicodesByGlyph.TryGetValue(
                key: pair.Right,
                value: out var rightUnicodes
            )
            ) {
                continue;
            }

            var adjustment = (((float)pair.XAdvance) / font.UnitsPerEm);

            foreach (var left in leftUnicodes) {
                foreach (var right in rightUnicodes) {
                    yield return new FontKerningPair(
                        AdvanceAdjustment: adjustment,
                        Unicode1: left,
                        Unicode2: right
                    );
                }
            }
        }
    }
    private static (int Columns, int Height, int Width) ChooseGrid(int glyphCount, int cellWidth, int cellHeight, FontAtlasGenerationOptions options) {
        if (glyphCount == 0) {
            return (Columns: 1, Height: 1, Width: 1);
        }

        var preferredColumns = Math.Clamp(
            value: options.Columns,
            min: 1,
            max: glyphCount
        );
        (int Columns, int Height, int Width)? best = null;

        for (var columns = 1; (columns <= glyphCount); columns++) {
            var rows = (((glyphCount + columns) - 1) / columns);
            var width = checked((columns * cellWidth));
            var height = checked((rows * cellHeight));
            var pixels = checked((((long)width) * height));

            if (
                (width > options.MaxAtlasDimension) ||
                (height > options.MaxAtlasDimension) ||
                (pixels > options.MaxAtlasPixels)
            ) {
                continue;
            }

            var distance = Math.Abs(value: (columns - preferredColumns));
            var bestDistance = ((best is { } current)
                ? Math.Abs(value: (current.Columns - preferredColumns))
                : int.MaxValue
            );

            if (
                (best is null) ||
                (distance < bestDistance) ||
                ((distance == bestDistance) && (pixels < (((long)best.Value.Width) * best.Value.Height)))
            ) {
                best = (Columns: columns, Height: height, Width: width);
            }
        }

        return (best ?? throw new ArgumentException(
            message: $"The selected glyphs cannot fit within the {options.MaxAtlasDimension}px dimension and {options.MaxAtlasPixels.ToString(provider: CultureInfo.InvariantCulture)}-pixel atlas limits.",
            paramName: nameof(options)
        ));
    }
    private static FontAtlasMetrics ConvertMetrics(OpenTypeFontFace font) {
        var unitsPerEm = ((float)font.UnitsPerEm);

        return new FontAtlasMetrics(
            LineHeight: (((font.Ascender - font.Descender) + font.LineGap) / unitsPerEm),
            Ascender: (font.Ascender / unitsPerEm),
            Descender: (font.Descender / unitsPerEm),
            UnderlineY: (font.UnderlinePosition / unitsPerEm),
            UnderlineThickness: (font.UnderlineThickness / unitsPerEm)
        );
    }
    private static IReadOnlyList<GlyphRaster> GetGlyphs(OpenTypeFontFace font, IReadOnlyList<int> codePoints, int fontPixelSize) {
        var glyphs = new List<GlyphRaster>(capacity: codePoints.Count);
        var rasterDataByGlyphId = new Dictionary<ushort, (float Advance, FontGlyphGeometry Glyph)>();
        var scale = (((float)fontPixelSize) / font.UnitsPerEm);

        foreach (var codePoint in codePoints) {
            var glyphId = font.GetGlyphId(codePoint: codePoint);

            if (glyphId == 0) {
                continue;
            }

            if (!rasterDataByGlyphId.TryGetValue(
                key: glyphId,
                value: out var rasterData
            )) {
                var outline = font.LoadGlyphGeometry(
                    glyphId: glyphId,
                    scale: scale
                );

                rasterData = (
                    Advance: (font.GetAdvanceWidth(glyphId: outline.MetricGlyphId) * scale),
                    Glyph: outline.Geometry
                );
                rasterDataByGlyphId.Add(
                    key: glyphId,
                    value: rasterData
                );
            }

            glyphs.Add(item: new GlyphRaster(
                Advance: rasterData.Advance,
                Glyph: rasterData.Glyph,
                GlyphId: glyphId,
                Unicode: codePoint
            ));
        }

        return glyphs;
    }
    private static (OpenTypeFontFace Font, IReadOnlyList<GlyphRaster> Glyphs) ParseFont(
        FontAtlasGenerationRequest request,
        IReadOnlyList<int> codePoints
    ) {
        try {
            var font = OpenTypeFontFace.Parse(
                faceIndex: request.Options.FaceIndex,
                fontBytes: request.FontBytes
            );

            return (
                Font: font,
                Glyphs: GetGlyphs(
                codePoints: codePoints,
                font: font,
                fontPixelSize: request.Options.FontPixelSize
            )
            );
        } catch (InvalidDataException exception) {
            throw new ArgumentException(
                message: exception.Message,
                paramName: nameof(request),
                innerException: exception
            );
        } catch (OverflowException exception) {
            throw new ArgumentException(
                message: "The supplied font contains table sizes or offsets that exceed Puck's supported limits.",
                paramName: nameof(request),
                innerException: exception
            );
        }
    }
    private static void ValidateOptions(FontAtlasGenerationOptions options) {
        if (options.Columns <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(options),
                message: "Font atlas columns must be greater than zero."
            );
        }

        if (
            !float.IsFinite(f: options.DistanceRange) ||
            (options.DistanceRange <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(options),
                message: "Font atlas distance range must be finite and greater than zero."
            );
        }

        if (options.FontPixelSize <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(options),
                message: "Font pixel size must be greater than zero."
            );
        }

        if (options.FaceIndex < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(options),
                message: "Font face index must not be negative."
            );
        }

        if (options.MaxAtlasDimension <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(options),
                message: "Maximum atlas dimension must be greater than zero."
            );
        }

        if (options.MaxAtlasPixels <= 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(options),
                message: "Maximum atlas pixel count must be greater than zero."
            );
        }

        if (options.Padding < MathF.Ceiling(x: options.DistanceRange)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(options),
                message: "Font atlas padding must be at least the ceiling of the distance range so adjacent glyph fields cannot bleed together."
            );
        }
    }

    /// <inheritdoc/>
    public FontAtlas Generate(FontAtlasGenerationRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);
        ValidateOptions(options: request.Options);

        if (request.FontBytes.IsEmpty) {
            throw new ArgumentException(
                message: "Font bytes must not be empty.",
                paramName: nameof(request)
            );
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(argument: request.FontIdentifier);
        var imageIdentifier = (request.ImageIdentifier ?? $"{request.FontIdentifier}#generated-atlas");
        var parsed = ParseFont(
            codePoints: BuildCodePoints(options: request.Options),
            request: request
        );
        var font = parsed.Font;
        var glyphs = parsed.Glyphs;
        var drawableGlyphs = glyphs
            .Where(predicate: static glyph => !glyph.Glyph.IsEmpty)
            .DistinctBy(keySelector: static glyph => glyph.GlyphId)
            .ToArray();
        var cellWidth = ((drawableGlyphs.Length == 0)
            ? 1
            : drawableGlyphs.Max(selector: glyph => checked(((((int)MathF.Ceiling(x: glyph.Glyph.Right)) - ((int)MathF.Floor(x: glyph.Glyph.Left))) + (2 * request.Options.Padding))))
        );
        var cellHeight = ((drawableGlyphs.Length == 0)
            ? 1
            : drawableGlyphs.Max(selector: glyph => checked(((((int)MathF.Ceiling(x: glyph.Glyph.Bottom)) - ((int)MathF.Floor(x: glyph.Glyph.Top))) + (2 * request.Options.Padding))))
        );
        var grid = ChooseGrid(
            cellHeight: cellHeight,
            cellWidth: cellWidth,
            glyphCount: drawableGlyphs.Length,
            options: request.Options
        );
        var rgba = new byte[checked(((grid.Width * grid.Height) * 4))];
        var cellsByGlyphId = new Dictionary<ushort, (FontAtlasBounds Atlas, FontAtlasBounds Plane)>();

        for (var index = 0; (index < drawableGlyphs.Length); index++) {
            var glyph = drawableGlyphs[index];
            var cellX = ((index % grid.Columns) * cellWidth);
            var cellY = ((index / grid.Columns) * cellHeight);
            var glyphLeft = MathF.Floor(x: glyph.Glyph.Left);
            var glyphTop = MathF.Floor(x: glyph.Glyph.Top);
            var planeLeft = ((glyphLeft - request.Options.Padding) / request.Options.FontPixelSize);
            var planeTop = (-(glyphTop - request.Options.Padding) / request.Options.FontPixelSize);

            MtsdfGlyphField.EvaluateCell(
                atlasRgba: rgba,
                atlasWidth: grid.Width,
                cellHeight: cellHeight,
                cellWidth: cellWidth,
                cellX: cellX,
                cellY: cellY,
                distanceRange: request.Options.DistanceRange,
                geometry: glyph.Glyph,
                offsetX: (request.Options.Padding - glyphLeft),
                offsetY: (request.Options.Padding - glyphTop)
            );
            cellsByGlyphId.Add(
                key: glyph.GlyphId,
                value: (
                    Atlas: new FontAtlasBounds(
                    Bottom: (cellY + cellHeight),
                    Left: cellX,
                    Right: (cellX + cellWidth),
                    Top: cellY
                ),
                    Plane: new FontAtlasBounds(
                    Left: planeLeft,
                    Bottom: (planeTop - (((float)cellHeight) / request.Options.FontPixelSize)),
                    Right: (planeLeft + (((float)cellWidth) / request.Options.FontPixelSize)),
                    Top: planeTop
                )
                )
            );
        }

        return new FontAtlas(
            kind: FontAtlasKind.Mtsdf,
            imagePath: imageIdentifier,
            size: request.Options.FontPixelSize,
            distanceRange: request.Options.DistanceRange,
            width: grid.Width,
            height: grid.Height,
            metrics: ConvertMetrics(font: font),
            glyphs: glyphs.Select(selector: glyph => {
                var hasCell = cellsByGlyphId.TryGetValue(
                    key: glyph.GlyphId,
                    value: out var cell
                );

                return new FontAtlasGlyph(
                    unicode: glyph.Unicode,
                    advance: (glyph.Advance / request.Options.FontPixelSize),
                    planeBounds: (hasCell
                    ? cell.Plane
                    : null),
                    atlasBounds: (hasCell
                    ? cell.Atlas
                    : null),
                    glyphId: glyph.GlyphId
                );
            }),
            kerningPairs: BuildKerningPairs(
                font: font,
                glyphs: glyphs
            ),
            imageData: new FontAtlasImageData(
                rgbaPixels: rgba,
                height: grid.Height,
                width: grid.Width
            )
        );
    }
}
