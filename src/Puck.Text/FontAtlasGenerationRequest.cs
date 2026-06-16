namespace Puck.Text;

/// <summary>
/// The complete input to <see cref="IFontAtlasGenerator.Generate(FontAtlasGenerationRequest)"/>: the raw
/// font bytes, the identities to stamp on the produced atlas, and the generation options.
/// </summary>
/// <remarks>
/// The request is the in-memory, source-agnostic form of a generation job. An
/// <see cref="IFontAtlasSourceResolver"/> constructs one after reading a font from disk; callers that
/// already hold font bytes can build one directly.
/// </remarks>
public sealed class FontAtlasGenerationRequest {
    /// <summary>Gets or sets the raw bytes of the source font file (for example a TrueType or OpenType file).</summary>
    public required ReadOnlyMemory<byte> FontBytes { get; init; }
    /// <summary>Gets or sets a stable identifier for the source font, used to attribute the generated atlas to its origin.</summary>
    public required string FontIdentifier { get; init; }
    /// <summary>Gets or sets an optional identifier for the generated atlas image. When omitted, a generator derives one from <see cref="FontIdentifier"/>.</summary>
    public string? ImageIdentifier { get; init; }
    /// <summary>Gets or sets the generation options. Defaults to a new <see cref="FontAtlasGenerationOptions"/> instance.</summary>
    public FontAtlasGenerationOptions Options { get; init; } = new();
}
