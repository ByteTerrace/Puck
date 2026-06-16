namespace Puck.Text;

/// <summary>
/// Produces a <see cref="FontAtlas"/> from raw font bytes and generation options. This is the extension
/// point that decouples the atlas data model from any specific rasterization or distance-field backend.
/// </summary>
/// <remarks>
/// Implementations live outside this library so that Puck.Text carries no rasterizer dependency.
/// Generators may also be composed — for example one that rasterizes coverage and another that wraps it to
/// derive a distance field — provided each honors this contract.
/// </remarks>
public interface IFontAtlasGenerator {
    /// <summary>Generates a font atlas for the supplied request.</summary>
    /// <param name="request">The font bytes, identities, and options describing the atlas to produce.</param>
    /// <returns>The generated <see cref="FontAtlas"/>.</returns>
    FontAtlas Generate(FontAtlasGenerationRequest request);
}
