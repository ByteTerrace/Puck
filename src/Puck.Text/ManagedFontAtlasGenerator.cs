using System.Globalization;
using System.Text;

namespace Puck.Text;

/// <summary>Generates a multi-channel true signed distance field (MTSDF) atlas from an OpenType font or collection
/// entirely in managed code: RGB median reconstructs sharp corners, alpha samples distance to the filled boundary.</summary>
/// <remarks>
/// Puck reads Unicode mappings, metrics, TrueType quadratic outlines, and CFF/CFF2 cubic charstrings without native
/// font libraries. Cubics are reduced to quadratics, then outlines are polygonized to a 0.01-pixel chord tolerance
/// and their nonzero-fill boundaries resolved before field evaluation (see
/// <see cref="MtsdfGlyphField"/>). The generated atlas preserves source glyph identifiers for a future shaping stage,
/// while this generator deliberately maps scalars directly rather than performing language-specific shaping or
/// ligature substitution. Pair kerning is flattened from GPOS pair positioning (the <c>kern</c> feature, PairPos
/// formats 1 and 2, extension lookups included) or, when GPOS yields none, the legacy horizontal <c>kern</c> table;
/// contextual positioning is not read. CFF execution, boundary processing, and per-glyph raster work have fixed
/// safety limits; exceeding a limit throws rather than returning a partial atlas. Quantized and filtered samples
/// are not guaranteed conservative sphere-tracing steps.
/// <para>Parsing, budget reservations, and packing are ordered. Rasterization uses at most four workers over
/// disjoint glyph rectangles; worker scheduling does not change pixel arithmetic or metadata order.</para>
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
    private static IEnumerable<FontKerningPair> BuildKerningPairs(OpenTypeFontFace font, IReadOnlyList<GlyphRaster> glyphs, FontGenerationBudget budget) {
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
                    budget.KerningPairs();
                    yield return new FontKerningPair(
                        AdvanceAdjustment: adjustment,
                        Unicode1: left,
                        Unicode2: right
                    );
                }
            }
        }
    }
    private static (int Width, int Height, GlyphPlacement[] Placements) ChooseShelves(
        IReadOnlyList<GlyphRaster> glyphs,
        int padding,
        FontAtlasGenerationOptions options
    ) {
        var glyphCount = glyphs.Count;

        if (glyphCount == 0) {
            return (Width: 1, Height: 1, Placements: []);
        }

        var widths = new int[glyphCount];
        var heights = new int[glyphCount];

        for (var index = 0; (index < glyphCount); index++) {
            var glyph = glyphs[index];
            var glyphLeft = MathF.Floor(x: glyph.Glyph.Left);
            var glyphTop = MathF.Floor(x: glyph.Glyph.Top);

            widths[index] = checked((((int)(MathF.Ceiling(x: glyph.Glyph.Right) - glyphLeft)) + (2 * padding)));
            heights[index] = checked((((int)(MathF.Ceiling(x: glyph.Glyph.Bottom) - glyphTop)) + (2 * padding)));
        }

        var preferredColumns = Math.Clamp(
            value: options.Columns,
            min: 1,
            max: glyphCount
        );
        var selectedColumns = 0;
        var selectedWidth = 0;
        var selectedHeight = 0;

        for (var distance = 0; (distance < MaxShelfCandidateDistance); distance++) {
            var candidateColumns = (preferredColumns - distance);

            if (
                (candidateColumns >= 1) &&
                IsShelfFit(
                columns: candidateColumns,
                height: out var candidateHeight,
                heights: heights,
                options: options,
                width: out var candidateWidth,
                widths: widths
            )
            ) {
                selectedColumns = candidateColumns;
                selectedWidth = candidateWidth;
                selectedHeight = candidateHeight;
                break;
            }

            candidateColumns = (preferredColumns + distance);
            if (
                (distance != 0) &&
                (candidateColumns <= glyphCount) &&
                IsShelfFit(
                columns: candidateColumns,
                height: out candidateHeight,
                heights: heights,
                options: options,
                width: out candidateWidth,
                widths: widths
            )
            ) {
                selectedColumns = candidateColumns;
                selectedWidth = candidateWidth;
                selectedHeight = candidateHeight;
                break;
            }
        }

        if (selectedColumns == 0) {
            var requiredByHeight = ((int)Math.Ceiling(a: (heights.Sum() / ((double)options.MaxAtlasDimension))));
            var fallbackColumns = Math.Clamp(
                max: glyphCount,
                min: 1,
                value: requiredByHeight
            );

            if (IsShelfFit(
                columns: fallbackColumns,
                height: out selectedHeight,
                heights: heights,
                options: options,
                width: out selectedWidth,
                widths: widths
            )) {
                selectedColumns = fallbackColumns;
            }
        }

        if (selectedColumns == 0) {
            throw new ArgumentException(
                message: $"The selected glyphs cannot fit within the {options.MaxAtlasDimension}px dimension and {options.MaxAtlasPixels.ToString(provider: CultureInfo.InvariantCulture)}-pixel atlas limits, or exceeded the bounded shelf search.",
                paramName: nameof(options)
            );
        }

        var placements = new GlyphPlacement[glyphCount];
        var y = 0;

        for (var rowStart = 0; (rowStart < glyphCount); rowStart += selectedColumns) {
            var rowEnd = Math.Min(
                val1: (rowStart + selectedColumns),
                val2: glyphCount
            );
            var rowWidth = 0;
            var rowHeight = 0;

            for (var index = rowStart; (index < rowEnd); index++) {
                placements[index] = new GlyphPlacement(
                    rowWidth,
                    y,
                    widths[index],
                    heights[index]
                );
                rowWidth = checked((rowWidth + widths[index]));
                rowHeight = Math.Max(
                    val1: rowHeight,
                    val2: heights[index]
                );
            }
            y = checked((y + rowHeight));
        }

        return (selectedWidth, selectedHeight, placements);
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
    private static IReadOnlyList<GlyphRaster> GetGlyphs(OpenTypeFontFace font, IReadOnlyList<int> codePoints, int fontPixelSize, FontGenerationBudget budget) {
        var glyphs = new List<GlyphRaster>(capacity: codePoints.Count);
        var rasterDataByGlyphId = new Dictionary<ushort, (float Advance, FontGlyphGeometry Glyph)>();
        var scale = (((float)fontPixelSize) / font.UnitsPerEm);

        foreach (var codePoint in codePoints) {
            budget.Work();
            var glyphId = font.GetGlyphId(codePoint: codePoint);

            if (glyphId == 0) {
                continue;
            }

            if (!rasterDataByGlyphId.TryGetValue(
                key: glyphId,
                value: out var rasterData
            )) {
                budget.Glyph();
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
    private static bool IsShelfFit(
        int columns,
        IReadOnlyList<int> widths,
        IReadOnlyList<int> heights,
        FontAtlasGenerationOptions options,
        out int width,
        out int height
    ) {
        width = 0;
        height = 0;
        for (var rowStart = 0; (rowStart < widths.Count); rowStart += columns) {
            var rowEnd = Math.Min(
                val1: (rowStart + columns),
                val2: widths.Count
            );
            var rowWidth = 0;
            var rowHeight = 0;

            for (var index = rowStart; (index < rowEnd); index++) {
                rowWidth = checked((rowWidth + widths[index]));
                rowHeight = Math.Max(
                    val1: rowHeight,
                    val2: heights[index]
                );
            }
            width = Math.Max(
                val1: width,
                val2: rowWidth
            );
            height = checked((height + rowHeight));
        }
        return (
            (width <= options.MaxAtlasDimension) &&
            (height <= options.MaxAtlasDimension) &&
            ((((long)width) * height) <= options.MaxAtlasPixels)
        );
    }
    private static (OpenTypeFontFace Font, IReadOnlyList<GlyphRaster> Glyphs) ParseFont(
        FontAtlasGenerationRequest request,
        IReadOnlyList<int> codePoints,
        FontGenerationBudget budget
    ) {
        try {
            var font = OpenTypeFontFace.Parse(
                budget: budget,
                faceIndex: request.Options.FaceIndex,
                fontBytes: request.FontBytes
            );

            return (
                Font: font,
                Glyphs: GetGlyphs(
                budget: budget,
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
        var budget = new FontGenerationBudget(
            request.Limits,
            request.CancellationToken
        );

        if (request.FontBytes.Length > request.Limits.MaxFontBytes) {
            throw new ArgumentException(
                message: "The supplied font exceeds the whole-job input byte limit.",
                paramName: nameof(request)
            );
        }
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
            budget: budget,
            codePoints: BuildCodePoints(options: request.Options),
            request: request
        );
        var font = parsed.Font;
        var glyphs = parsed.Glyphs;
        var drawableGlyphs = glyphs
            .Where(predicate: static glyph => !glyph.Glyph.IsEmpty)
            .DistinctBy(keySelector: static glyph => glyph.GlyphId)
            .ToArray();
        var shelves = ChooseShelves(
            glyphs: drawableGlyphs,
            padding: request.Options.Padding,
            options: request.Options
        );
        var prepared = new MtsdfGlyphField.PreparedCell[drawableGlyphs.Length];

        for (var index = 0; (index < drawableGlyphs.Length); index++) {
            var placement = shelves.Placements[index];

            prepared[index] = MtsdfGlyphField.PrepareCell(
                drawableGlyphs[index].Glyph,
                placement.Width,
                placement.Height,
                budget
            );
        }
        var kerningPairs = BuildKerningPairs(
            budget: budget,
            font: font,
            glyphs: glyphs
        ).ToArray();

        budget.CancellationToken.ThrowIfCancellationRequested();
        var rgba = new byte[checked(((shelves.Width * shelves.Height) * 4))];
        var cellsByGlyphId = new Dictionary<ushort, (FontAtlasBounds Atlas, FontAtlasBounds Plane)>();

        void Rasterize(int index) {
            var glyph = drawableGlyphs[index];
            var placement = shelves.Placements[index];

            MtsdfGlyphField.EvaluateCell(
                budget: budget,
                atlasRgba: rgba,
                atlasWidth: shelves.Width,
                cellHeight: placement.Height,
                cellWidth: placement.Width,
                cellX: placement.X,
                cellY: placement.Y,
                distanceRange: request.Options.DistanceRange,
                prepared: prepared[index],
                offsetX: (request.Options.Padding - MathF.Floor(x: glyph.Glyph.Left)),
                offsetY: (request.Options.Padding - MathF.Floor(x: glyph.Glyph.Top))
            );
        }

        // PrepareCell reserved ALL raster work before allocating the image. Evaluation only reads the budget's
        // cancellation token and its prepared geometry, and each cell owns a disjoint rectangle in rgba.
        // Keep metadata assembly below serial so scheduling cannot affect atlas order or kerning.
        if ((drawableGlyphs.Length > 1) && (Environment.ProcessorCount > 1)) {
            Parallel.For(0, drawableGlyphs.Length, new ParallelOptions {
                CancellationToken = budget.CancellationToken,
                MaxDegreeOfParallelism = Math.Min(val1: 4, val2: Environment.ProcessorCount),
            }, Rasterize);
        } else {
            for (var index = 0; (index < drawableGlyphs.Length); index++) {
                Rasterize(index: index);
            }
        }

        for (var index = 0; (index < drawableGlyphs.Length); index++) {
            var glyph = drawableGlyphs[index];
            var placement = shelves.Placements[index];
            var cellX = placement.X;
            var cellY = placement.Y;
            var glyphLeft = MathF.Floor(x: glyph.Glyph.Left);
            var glyphTop = MathF.Floor(x: glyph.Glyph.Top);
            // The glyph is drawn at the padded rectangle's top-left. Only that rectangle is sampled, so no pixels
            // are spent on the widest glyph's unused remainder and layout extents remain the letter's own bounds.
            var glyphWidth = placement.Width;
            var glyphHeight = placement.Height;
            var planeLeft = ((glyphLeft - request.Options.Padding) / request.Options.FontPixelSize);
            var planeTop = (-(glyphTop - request.Options.Padding) / request.Options.FontPixelSize);

            cellsByGlyphId.Add(
                key: glyph.GlyphId,
                value: (
                    Atlas: new FontAtlasBounds(
                    Bottom: (cellY + glyphHeight),
                    Left: cellX,
                    Right: (cellX + glyphWidth),
                    Top: cellY
                ),
                    Plane: new FontAtlasBounds(
                    Left: planeLeft,
                    Bottom: (planeTop - (((float)glyphHeight) / request.Options.FontPixelSize)),
                    Right: (planeLeft + (((float)glyphWidth) / request.Options.FontPixelSize)),
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
            width: shelves.Width,
            height: shelves.Height,
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
            kerningPairs: kerningPairs,
            imageData: FontAtlasImageData.TakeOwnership(
                height: shelves.Height,
                rgbaPixels: rgba,
                width: shelves.Width
            )
        );
    }

    private readonly record struct GlyphPlacement(int X, int Y, int Width, int Height);

    private const int MaxShelfCandidateDistance = 256;
}
