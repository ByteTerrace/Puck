namespace Puck.Text;

/// <summary>Named logical font atlases remapped into one combined texture suitable for a single GPU binding.</summary>
public sealed class PackedFontAtlasCatalog {
    private readonly IReadOnlyDictionary<string, FontAtlas> m_fonts;

    internal PackedFontAtlasCatalog(string defaultFont, IReadOnlyDictionary<string, FontAtlas> fonts, FontAtlasImageData imageData) {
        DefaultFont = defaultFont;
        ImageData = imageData;
        m_fonts = fonts;
    }

    /// <summary>Gets the default logical font name.</summary>
    public string DefaultFont { get; }
    /// <summary>Gets the named logical fonts, each remapped into <see cref="ImageData"/>.</summary>
    public IReadOnlyDictionary<string, FontAtlas> Fonts => m_fonts;
    /// <summary>Gets the one combined RGBA atlas image.</summary>
    public FontAtlasImageData ImageData { get; }

    /// <summary>Resolves a named font, or the catalog default when <paramref name="name"/> is null.</summary>
    public FontAtlas Resolve(string? name) {
        var resolvedName = (name ?? DefaultFont);

        return (m_fonts.TryGetValue(
            key: resolvedName,
            value: out var atlas
        )
            ? atlas
            : throw new KeyNotFoundException(message: $"Font '{resolvedName}' is not declared by this catalog.")
        );
    }
}
