namespace Puck.Text;

/// <summary>
/// The tunable inputs to font atlas generation: which code points to include, the rasterization size, and
/// the limits that bound the produced atlas image.
/// </summary>
/// <remarks>
/// The glyph set is the union of <see cref="AllowedCharacters"/> and the expansion of
/// <see cref="AllowedCodePointRanges"/> (see <see cref="UnicodeCodePointRangeExpander"/>), filtered to the
/// code points the source font actually maps. These options also participate in atlas cache identity:
/// <see cref="FontAtlasSourceResolver"/> hashes a normalized form of them so that two requests with
/// equivalent options reuse the same cached <see cref="FontAtlas"/>.
/// </remarks>
public sealed class FontAtlasGenerationOptions {
    /// <summary>
    /// Gets or sets the additional characters to include in the atlas regardless of the configured ranges.
    /// Whitespace is ignored. Defaults to an empty string.
    /// </summary>
    public string AllowedCharacters { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the code-point ranges to include, each a token such as <c>U+0020-U+007E</c>, a single
    /// <c>U+E0A0</c>, or <c>*</c> to request every Basic Multilingual Plane code point. Defaults to
    /// printable ASCII plus a selection of Powerline and Private Use Area glyphs.
    /// </summary>
    public IReadOnlyList<string> AllowedCodePointRanges { get; set; } = ["U+0020-U+007E", "U+E0A0", "U+E0B0", "U+E0B1", "U+F000-U+F8FF"];
    /// <summary>
    /// Gets or sets the preferred number of glyph columns in the atlas grid. A generator may use more
    /// columns when required to respect <see cref="MaxAtlasDimension"/>. Defaults to <c>16</c>.
    /// </summary>
    public int Columns { get; set; } = 16;
    /// <summary>Gets or sets the em size, in pixels, at which glyphs are rasterized. Defaults to <c>32</c>.</summary>
    public int FontPixelSize { get; set; } = 32;
    /// <summary>Gets or sets the maximum allowed width or height of the atlas image, in pixels. Defaults to <c>16384</c>.</summary>
    public int MaxAtlasDimension { get; set; } = 16_384;
    /// <summary>Gets or sets the maximum allowed total pixel count of the atlas image. Defaults to <c>67108864</c> (the equivalent of 8192 × 8192).</summary>
    public long MaxAtlasPixels { get; set; } = 67_108_864;
    /// <summary>Gets or sets the padding, in pixels, reserved around each glyph cell. Defaults to <c>8</c>.</summary>
    public int Padding { get; set; } = 8;
}
