using System.Diagnostics.CodeAnalysis;

namespace Puck.Text;

/// <summary>
/// An immutable, render-agnostic description of a generated font atlas: the encoding kind, the source
/// image and its dimensions, the em size and distance-field range it was generated at, the font-wide
/// metrics, and the set of glyphs and kerning pairs. It is the central data model that
/// <see cref="TextLayout"/> consumes and that the sampling helpers in <see cref="MtsdfSampling"/> reason
/// about.
/// </summary>
/// <remarks>
/// An atlas is produced by an <see cref="IFontAtlasGenerator"/> (typically via an
/// <see cref="IFontAtlasSourceResolver"/>) and is treated as read-only thereafter. Glyphs are indexed by
/// their Unicode scalar value and kerning pairs by their ordered code-point key, so both lookups are
/// constant time. The type carries no GPU or windowing concepts; callers that draw the atlas derive their
/// own pixel-space geometry from its em-space metrics and bounds.
/// </remarks>
public sealed class FontAtlas {
    private static long ComposeKerningKey(int leftUnicode, int rightUnicode) {
        return ((long)leftUnicode << 32) | (uint)rightUnicode;
    }

    private readonly Dictionary<int, FontAtlasGlyph> m_glyphs;
    private readonly Dictionary<long, float> m_kerningAdjustments;
    private readonly FontKerningPair[] m_kerningPairs;

    /// <summary>Gets the width, in em units, of the signed-distance band encoded around glyph edges, or the generator's nominal range for mask atlases. See <see cref="MtsdfSampling.ComputeUnitRange(FontAtlas)"/>.</summary>
    public float DistanceRange { get; }
    /// <summary>Gets the glyphs contained in the atlas, keyed internally by Unicode scalar value.</summary>
    public IReadOnlyCollection<FontAtlasGlyph> Glyphs => m_glyphs.Values;
    /// <summary>Gets the height of the atlas image, in texels.</summary>
    public int Height { get; }
    /// <summary>Gets the in-memory atlas image, or <see langword="null"/> when only <see cref="ImagePath"/> is known.</summary>
    public FontAtlasImageData? ImageData { get; }
    /// <summary>Gets the path or identifier of the atlas image.</summary>
    public string ImagePath { get; }
    /// <summary>Gets the kerning pairs contained in the atlas; consult <see cref="GetKerningAdjustment(int, int)"/> for lookups.</summary>
    public IReadOnlyCollection<FontKerningPair> KerningPairs => m_kerningPairs;
    /// <summary>Gets the encoding used by the atlas image, which determines how it must be sampled.</summary>
    public FontAtlasKind Kind { get; }
    /// <summary>Gets the font-wide vertical metrics, in em units.</summary>
    public FontAtlasMetrics Metrics { get; }
    /// <summary>Gets the em size, in pixels, at which the atlas was rasterized; the conversion factor from em units to atlas pixels.</summary>
    public float Size { get; }
    /// <summary>Gets the width of the atlas image, in texels.</summary>
    public int Width { get; }

    /// <summary>Initializes a new <see cref="FontAtlas"/> from its fully resolved components.</summary>
    /// <param name="kind">The encoding used by the atlas image.</param>
    /// <param name="imagePath">The path or identifier of the atlas image. Must not be <see langword="null"/>, empty, or whitespace.</param>
    /// <param name="size">The em size, in pixels, at which the atlas was rasterized. Must be greater than zero.</param>
    /// <param name="distanceRange">The width, in em units, of the encoded signed-distance band.</param>
    /// <param name="width">The width of the atlas image, in texels. Must be greater than zero.</param>
    /// <param name="height">The height of the atlas image, in texels. Must be greater than zero.</param>
    /// <param name="metrics">The font-wide vertical metrics.</param>
    /// <param name="glyphs">The glyphs to index. Each glyph's <see cref="FontAtlasGlyph.Unicode"/> must be unique.</param>
    /// <param name="kerningPairs">The kerning pairs to index.</param>
    /// <param name="imageData">The optional in-memory atlas image.</param>
    /// <exception cref="ArgumentException"><paramref name="imagePath"/> is <see langword="null"/>, empty, or whitespace, or <paramref name="glyphs"/> contains more than one glyph with the same <see cref="FontAtlasGlyph.Unicode"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/>, <paramref name="width"/>, or <paramref name="height"/> is not greater than zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="glyphs"/> or <paramref name="kerningPairs"/> is <see langword="null"/>.</exception>
    public FontAtlas(
        FontAtlasKind kind,
        string imagePath,
        float size,
        float distanceRange,
        int width,
        int height,
        FontAtlasMetrics metrics,
        IEnumerable<FontAtlasGlyph> glyphs,
        IEnumerable<FontKerningPair> kerningPairs,
        FontAtlasImageData? imageData = null
    ) {
        if (string.IsNullOrWhiteSpace(value: imagePath)) {
            throw new ArgumentException(
                message: "Font atlas image path must be provided.",
                paramName: nameof(imagePath)
            );
        }

        if (size <= 0.0f) {
            throw new ArgumentOutOfRangeException(
                message: "Font atlas size must be greater than zero.",
                paramName: nameof(size)
            );
        }

        if (width <= 0) {
            throw new ArgumentOutOfRangeException(
                message: "Font atlas width must be greater than zero.",
                paramName: nameof(width)
            );
        }

        if (height <= 0) {
            throw new ArgumentOutOfRangeException(
                message: "Font atlas height must be greater than zero.",
                paramName: nameof(height)
            );
        }

        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(kerningPairs);

        var glyphArray = glyphs.ToArray();

        m_kerningPairs = [.. kerningPairs];

        Kind = kind;
        ImagePath = imagePath;
        Size = size;
        DistanceRange = distanceRange;
        Width = width;
        Height = height;
        Metrics = metrics;
        ImageData = imageData;
        m_glyphs = glyphArray.ToDictionary(
            elementSelector: glyph => glyph,
            keySelector: glyph => glyph.Unicode
        );
        m_kerningAdjustments = m_kerningPairs.ToDictionary(
            elementSelector: pair => pair.AdvanceAdjustment,
            keySelector: pair => ComposeKerningKey(
                leftUnicode: pair.Unicode1,
                rightUnicode: pair.Unicode2
            )
        );
    }

    /// <summary>Returns the kerning advance adjustment, in em units, for the ordered pair of code points.</summary>
    /// <param name="leftUnicode">The Unicode scalar value of the left (preceding) glyph.</param>
    /// <param name="rightUnicode">The Unicode scalar value of the right (following) glyph.</param>
    /// <returns>The advance adjustment in em units, or <c>0</c> when the atlas defines no kerning for the pair.</returns>
    public float GetKerningAdjustment(int leftUnicode, int rightUnicode) {
        return (m_kerningAdjustments.TryGetValue(
            key: ComposeKerningKey(
                leftUnicode: leftUnicode,
                rightUnicode: rightUnicode
            ),
            value: out var adjustment
        )
            ? adjustment
            : 0.0f);
    }
    /// <summary>Attempts to retrieve the glyph for the given Unicode scalar value.</summary>
    /// <param name="unicode">The Unicode scalar value to look up.</param>
    /// <param name="glyph">When this method returns <see langword="true"/>, the matching glyph; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a glyph for <paramref name="unicode"/> exists in the atlas; otherwise <see langword="false"/>.</returns>
    public bool TryGetGlyph(int unicode, [NotNullWhen(true)] out FontAtlasGlyph? glyph) {
        return m_glyphs.TryGetValue(
            key: unicode,
            value: out glyph
        );
    }
}
