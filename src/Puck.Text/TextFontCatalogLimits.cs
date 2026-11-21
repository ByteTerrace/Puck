namespace Puck.Text;

/// <summary>The ceilings a font catalog is resolved under when its sender is not trusted, as a catalog another
/// authority delivered is not: what the catalog may ask this process to read, rasterize and hold. The counts that
/// depend only on the catalog's own rows are checked by <see cref="Admit(TextFontCatalogDefinition)"/> before any font
/// is read; the bytes and pixels are charged as each font resolves, so a catalog past a ceiling stops at the font
/// that crosses it.</summary>
public sealed record TextFontCatalogLimits {
    /// <summary>Gets the ceilings a catalog delivered by another authority is held to.</summary>
    public static TextFontCatalogLimits Delivered { get; } = new();

    /// <summary>Gets the most fonts the catalog may name. Defaults to 16.</summary>
    public int MaxFonts { get; init; } = 16;
    /// <summary>Gets the most font bytes the catalog's fonts may total. Defaults to 64 MiB.</summary>
    public int MaxFontBytes { get; init; } = ((64 * 1024) * 1024);
    /// <summary>Gets the most scalar values the catalog's rows may ask for in total, each range counted as written
    /// (<see cref="UnicodeCodePointRangeExpander.CountRequested"/>). Defaults to 8,192.</summary>
    public int MaxGlyphs { get; init; } = 8_192;
    /// <summary>Gets the most atlas pixels the catalog's fonts may total, which bounds the packed atlas the catalog
    /// becomes. Defaults to 16,777,216, a 64 MiB image.</summary>
    public long MaxAtlasPixels { get; init; } = 16_777_216;
    /// <summary>Gets the largest rasterization em size, in pixels, a row may ask for. Defaults to 256.</summary>
    public int MaxFontPixelSize { get; init; } = 256;
    /// <summary>Gets the work-unit ceiling of one font's generation
    /// (<see cref="FontAtlasGenerationLimits.MaxWork"/>). Defaults to 250 million.</summary>
    public long MaxWorkPerFont { get; init; } = 250_000_000;

    /// <summary>Checks everything the catalog's rows alone decide, reading no font: how many fonts it names, each
    /// row's em size, and the scalar values the rows ask for in total.</summary>
    /// <param name="definition">The catalog.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/>, its font list or a row is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The catalog is past a ceiling; the message names it.</exception>
    /// <exception cref="ArgumentException">A row's code point selection is malformed.</exception>
    public void Admit(TextFontCatalogDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Fonts);

        if (definition.Fonts.Count > MaxFonts) {
            throw new InvalidDataException(message: $"The font catalog names {definition.Fonts.Count} fonts; a delivered catalog names at most {MaxFonts}.");
        }

        var glyphs = 0L;

        foreach (var row in definition.Fonts) {
            ArgumentNullException.ThrowIfNull(row);
            glyphs += Requested(options: row.ToGenerationOptions());
        }

        if (glyphs > MaxGlyphs) {
            throw new InvalidDataException(message: $"The font catalog asks for {glyphs} glyphs; a delivered catalog asks for at most {MaxGlyphs}.");
        }
    }
    /// <summary>Counts the scalar values one font's options ask for, refusing an em size past
    /// <see cref="MaxFontPixelSize"/>.</summary>
    /// <param name="options">The font's generation options.</param>
    /// <returns>The count, each range counted as written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The em size is past the ceiling.</exception>
    /// <exception cref="ArgumentException">The code point selection is malformed.</exception>
    public long Requested(FontAtlasGenerationOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        if (options.FontPixelSize > MaxFontPixelSize) {
            throw new InvalidDataException(message: $"A font asks for a {options.FontPixelSize} pixel em; a delivered catalog asks for at most {MaxFontPixelSize}.");
        }

        var characters = 0;

        foreach (var _ in (options.AllowedCharacters ?? string.Empty).EnumerateRunes()) {
            characters++;
        }

        return (UnicodeCodePointRangeExpander.CountRequested(ranges: (options.AllowedCodePointRanges ?? [])) + characters);
    }
}
