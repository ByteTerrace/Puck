namespace Puck.Text;

/// <summary>
/// Resolves a <see cref="FontAtlas"/> from a font file path, reading the font and driving generation on
/// the caller's behalf. This is the path-oriented front door to atlas generation.
/// </summary>
/// <remarks>
/// Whereas <see cref="IFontAtlasGenerator"/> works from font bytes already in memory, a resolver owns the
/// I/O of locating and reading the font, and is the natural place to add caching. See
/// <see cref="FontAtlasSourceResolver"/> for the content-addressed, caching implementation.
/// </remarks>
public interface IFontAtlasSourceResolver {
    /// <summary>Resolves the atlas for the font at the given path, generating it if necessary.</summary>
    /// <param name="fontPath">The font file path. May be absolute or relative to <paramref name="basePath"/>.</param>
    /// <param name="generationOptions">The options controlling glyph selection and atlas sizing.</param>
    /// <param name="basePath">The base directory used to resolve a relative <paramref name="fontPath"/>.</param>
    /// <returns>The resolved <see cref="FontAtlas"/>.</returns>
    FontAtlas Resolve(
        string fontPath,
        FontAtlasGenerationOptions generationOptions,
        string basePath
    );
}
