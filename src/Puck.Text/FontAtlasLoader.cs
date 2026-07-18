using System.Text.Json;

namespace Puck.Text;

/// <summary>
/// Reads a pre-baked JSON atlas — metadata plus its image — into a
/// <see cref="FontAtlas"/>, without generating anything at runtime.
/// </summary>
/// <remarks>
/// This is the pre-baked counterpart to <see cref="IFontAtlasGenerator"/>: instead of rasterizing a font at
/// runtime, it loads an atlas image and JSON metadata produced ahead of time by the font-atlas bake
/// pipeline (<c>tools/font-atlas</c>). The metadata schema is extended with two non-standard conventions this loader honors: a
/// top-level <c>variants[]</c> array, whose first entry's <c>metrics</c>/<c>glyphs</c>/<c>kerning</c>
/// sections take precedence over the document-level ones when present; and per-glyph <c>emRange</c> /
/// <c>pxRange</c> / <c>distanceRange</c> distance-field overrides (see
/// <see cref="MtsdfSampling.ComputeUnitRange(FontAtlas, FontAtlasGlyph)"/>).
/// </remarks>
public sealed class FontAtlasLoader {
    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads a font atlas from a JSON metadata file on disk, together with its atlas image.</summary>
    /// <param name="jsonPath">The path to the font-atlas bake pipeline's JSON metadata file.</param>
    /// <param name="imagePath">
    /// The path to the atlas image, absolute or relative to <paramref name="jsonPath"/>'s directory, or
    /// <see langword="null"/> to use <paramref name="jsonPath"/> with its extension changed to <c>.png</c>.
    /// </param>
    /// <returns>The loaded <see cref="FontAtlas"/>. Its <see cref="FontAtlas.ImagePath"/> is the resolved image path; no image pixels are decoded (see <see cref="IFontAtlasImageDataLoader"/> for that).</returns>
    /// <exception cref="ArgumentException"><paramref name="jsonPath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="FileNotFoundException">The metadata file or the resolved image file was not found.</exception>
    /// <exception cref="InvalidDataException">The metadata is empty, malformed, or missing a required section.</exception>
    public FontAtlas Load(string jsonPath, string? imagePath = null) {
        if (string.IsNullOrWhiteSpace(value: jsonPath)) {
            throw new ArgumentException(
                message: "Font atlas metadata path must be provided.",
                paramName: nameof(jsonPath)
            );
        }

        if (!File.Exists(path: jsonPath)) {
            throw new FileNotFoundException(fileName: jsonPath, message: "Font atlas metadata file was not found.");
        }

        var jsonContent = File.ReadAllBytes(path: jsonPath);
        var document = (JsonSerializer.Deserialize<FontAtlasDocument>(options: SerializerOptions, utf8Json: jsonContent)
            ?? throw new InvalidDataException(message: "Font atlas metadata is empty or invalid JSON."));
        var resolvedImagePath = ResolveImagePath(
            jsonPath: jsonPath,
            imagePath: imagePath
        );

        if (!File.Exists(path: resolvedImagePath)) {
            throw new FileNotFoundException(fileName: resolvedImagePath, message: "Font atlas image file was not found.");
        }

        return CreateAtlas(
            document: document,
            imagePath: resolvedImagePath,
            imageData: null
        );
    }

    /// <summary>Loads a font atlas from in-memory JSON metadata bytes and a pre-decoded atlas image.</summary>
    /// <param name="atlasIdentifier">A content-addressed or otherwise unique identifier for the metadata, used only in error messages.</param>
    /// <param name="jsonContent">The font-atlas bake pipeline's JSON metadata bytes.</param>
    /// <param name="imageIdentifier">The identifier recorded as the loaded atlas's <see cref="FontAtlas.ImagePath"/>.</param>
    /// <param name="imageData">The already-decoded atlas image; its dimensions must agree with the metadata.</param>
    /// <returns>The loaded <see cref="FontAtlas"/>, carrying <paramref name="imageData"/> in memory.</returns>
    /// <exception cref="ArgumentException"><paramref name="atlasIdentifier"/> or <paramref name="imageIdentifier"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="imageData"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The metadata is empty, malformed, missing a required section, or its dimensions disagree with <paramref name="imageData"/>.</exception>
    public FontAtlas Load(string atlasIdentifier, ReadOnlyMemory<byte> jsonContent, string imageIdentifier, FontAtlasImageData imageData) {
        if (string.IsNullOrWhiteSpace(value: atlasIdentifier)) {
            throw new ArgumentException(
                message: "Font atlas metadata identifier must be provided.",
                paramName: nameof(atlasIdentifier)
            );
        }

        if (string.IsNullOrWhiteSpace(value: imageIdentifier)) {
            throw new ArgumentException(
                message: "Font atlas image identifier must be provided.",
                paramName: nameof(imageIdentifier)
            );
        }

        ArgumentNullException.ThrowIfNull(imageData);

        var document = (JsonSerializer.Deserialize<FontAtlasDocument>(options: SerializerOptions, utf8Json: jsonContent.Span)
            ?? throw new InvalidDataException(message: $"Font atlas metadata '{atlasIdentifier}' is empty or invalid JSON."));

        return CreateAtlas(
            document: document,
            imagePath: imageIdentifier,
            imageData: imageData
        );
    }

    private static FontAtlas CreateAtlas(
        FontAtlasDocument document,
        string imagePath,
        FontAtlasImageData? imageData
    ) {
        var variant = document.Variants?.FirstOrDefault();
        var atlas = (document.Atlas ?? throw new InvalidDataException(message: "Font atlas metadata is missing the atlas section."));
        var metrics = (variant?.Metrics ?? (document.Metrics
            ?? throw new InvalidDataException(message: "Font atlas metadata is missing the metrics section.")));
        var glyphs = (variant?.Glyphs ?? (document.Glyphs
            ?? throw new InvalidDataException(message: "Font atlas metadata is missing the glyphs section.")));
        var kerningPairs = (variant?.Kerning ?? (document.Kerning ?? []));

        if (
            (imageData is not null) &&
            ((imageData.Width != atlas.Width) || (imageData.Height != atlas.Height))
        ) {
            throw new InvalidDataException(
                message: $"Font atlas image dimensions {imageData.Width}x{imageData.Height} did not match metadata dimensions {atlas.Width}x{atlas.Height}.");
        }

        var kind = ParseKind(kind: atlas.Type);

        if (MtsdfSampling.UsesDistanceField(kind: kind) && (atlas.DistanceRange <= 0.0f)) {
            // Fail at load with a clear message instead of mid-frame inside
            // MtsdfSampling.ComputeUnitRange.
            throw new InvalidDataException(
                message: $"Font atlas metadata '{imagePath}' is a distance-field atlas ('{atlas.Type}') but does not define a positive distanceRange.");
        }

        return new FontAtlas(
            kind: kind,
            imagePath: imagePath,
            size: atlas.Size,
            distanceRange: atlas.DistanceRange,
            width: atlas.Width,
            height: atlas.Height,
            metrics: new FontAtlasMetrics(
                LineHeight: metrics.LineHeight,
                Ascender: metrics.Ascender,
                Descender: metrics.Descender,
                UnderlineY: metrics.UnderlineY,
                UnderlineThickness: metrics.UnderlineThickness
            ),
            glyphs: glyphs.Select(selector: glyph => CreateGlyph(
                glyph: glyph,
                atlasHeight: atlas.Height
            )),
            kerningPairs: kerningPairs.Select(selector: pair => new FontKerningPair(
                Unicode1: pair.Unicode1,
                Unicode2: pair.Unicode2,
                AdvanceAdjustment: pair.Advance
            )),
            imageData: imageData
        );
    }
    private static FontAtlasGlyph CreateGlyph(FontAtlasGlyphDocument glyph, int atlasHeight) {
        var unicode = (glyph.Unicode ?? (glyph.Index
            ?? throw new InvalidDataException(message: "Each font atlas glyph must define a unicode or index value.")));
        var emRange = NormalizeOptionalPositiveRange(
            value: glyph.EmRange,
            unicode: unicode,
            propertyName: nameof(glyph.EmRange)
        );
        var pxRange = NormalizeOptionalPositiveRange(
            value: (glyph.PxRange ?? glyph.DistanceRange),
            unicode: unicode,
            propertyName: nameof(glyph.PxRange)
        );

        return new FontAtlasGlyph(
            unicode: unicode,
            advance: glyph.Advance,
            planeBounds: CreateBounds(bounds: glyph.PlaneBounds),
            atlasBounds: CreateAtlasBounds(
                bounds: glyph.AtlasBounds,
                atlasHeight: atlasHeight
            ),
            emRange: emRange,
            pxRange: pxRange
        );
    }
    private static float? NormalizeOptionalPositiveRange(float? value, int unicode, string propertyName) {
        if (!value.HasValue) {
            return null;
        }

        if (value.Value <= 0.0f) {
            throw new InvalidDataException(message: $"Glyph U+{unicode:X4} defines invalid {propertyName}. Optional glyph range values must be greater than zero when present.");
        }

        return value.Value;
    }
    private static FontAtlasBounds? CreateBounds(FontAtlasBoundsDocument? bounds) {
        return ((bounds is null)
            ? null
            : new FontAtlasBounds(
                Left: bounds.Left,
                Bottom: bounds.Bottom,
                Right: bounds.Right,
                Top: bounds.Top
            ));
    }

    /// <summary>Normalizes atlas bounds to the one supported convention (see
    /// <see cref="FontAtlasBounds"/>: top-down rows, Bottom = larger row edge).
    /// A top-origin bake already matches; a bottom-origin bake measures rows from the image's bottom edge
    /// (Bottom &lt; Top) and is flipped against the atlas height here.</summary>
    private static FontAtlasBounds? CreateAtlasBounds(FontAtlasBoundsDocument? bounds, int atlasHeight) {
        if (bounds is null) {
            return null;
        }

        return ((bounds.Bottom >= bounds.Top)
            ? new FontAtlasBounds(
                Left: bounds.Left,
                Bottom: bounds.Bottom,
                Right: bounds.Right,
                Top: bounds.Top
            )
            : new FontAtlasBounds(
                Left: bounds.Left,
                Bottom: (atlasHeight - bounds.Bottom),
                Right: bounds.Right,
                Top: (atlasHeight - bounds.Top)
            ));
    }
    private static FontAtlasKind ParseKind(string? kind) {
        return kind?.ToLowerInvariant() switch {
            "hardmask" => FontAtlasKind.HardMask,
            "softmask" => FontAtlasKind.SoftMask,
            "sdf" => FontAtlasKind.Sdf,
            "psdf" => FontAtlasKind.Psdf,
            "msdf" => FontAtlasKind.Msdf,
            "mtsdf" => FontAtlasKind.Mtsdf,
            _ => throw new InvalidDataException(message: $"Unsupported font atlas type '{kind}'.")
        };
    }
    private static string ResolveImagePath(string jsonPath, string? imagePath) {
        if (string.IsNullOrWhiteSpace(value: imagePath)) {
            return Path.ChangeExtension(path: jsonPath, extension: ".png");
        }

        return (Path.IsPathRooted(path: imagePath)
            ? imagePath
            : Path.Combine(path1: Path.GetDirectoryName(path: jsonPath)!, path2: imagePath));
    }

    private sealed class FontAtlasDocument {
        public FontAtlasSectionDocument? Atlas { get; set; }
        public List<FontAtlasGlyphDocument>? Glyphs { get; set; }
        public List<FontAtlasKerningDocument>? Kerning { get; set; }
        public FontAtlasMetricsDocument? Metrics { get; set; }
        public List<FontAtlasVariantDocument>? Variants { get; set; }
    }
    private sealed class FontAtlasVariantDocument {
        public List<FontAtlasGlyphDocument>? Glyphs { get; set; }
        public List<FontAtlasKerningDocument>? Kerning { get; set; }
        public FontAtlasMetricsDocument? Metrics { get; set; }
    }
    private sealed class FontAtlasSectionDocument {
        public float DistanceRange { get; set; }
        public int Height { get; set; }
        public float Size { get; set; }
        public string? Type { get; set; }
        public int Width { get; set; }
    }
    private sealed class FontAtlasMetricsDocument {
        public float Ascender { get; set; }
        public float Descender { get; set; }
        public float LineHeight { get; set; }
        public float UnderlineThickness { get; set; }
        public float UnderlineY { get; set; }
    }
    private sealed class FontAtlasGlyphDocument {
        public float Advance { get; set; }
        public FontAtlasBoundsDocument? AtlasBounds { get; set; }
        public float? DistanceRange { get; set; }
        public float? EmRange { get; set; }
        public int? Index { get; set; }
        public FontAtlasBoundsDocument? PlaneBounds { get; set; }
        public float? PxRange { get; set; }
        public int? Unicode { get; set; }
    }
    private sealed class FontAtlasKerningDocument {
        public float Advance { get; set; }
        public int Unicode1 { get; set; }
        public int Unicode2 { get; set; }
    }
    private sealed class FontAtlasBoundsDocument {
        public float Bottom { get; set; }
        public float Left { get; set; }
        public float Right { get; set; }
        public float Top { get; set; }
    }
}
