namespace Puck.Text;

/// <summary>
/// Produces a <see cref="FontAtlas"/> from raw font bytes and generation options. This is the extension
/// point that decouples the atlas data model from any specific rasterization or distance-field backend.
/// </summary>
/// <remarks>
/// Puck.Text supplies <see cref="ManagedFontAtlasGenerator"/> as its production, in-process implementation. Alternate
/// implementations remain useful as test oracles and for third-party integrations. Generators may also be composed,
/// provided each honors this contract.
/// </remarks>
public interface IFontAtlasGenerator {
    /// <summary>Generates a font atlas for the supplied request.</summary>
    /// <param name="request">The font bytes, identities, and options describing the atlas to produce.</param>
    /// <returns>The generated <see cref="FontAtlas"/>.</returns>
    FontAtlas Generate(FontAtlasGenerationRequest request);
}
