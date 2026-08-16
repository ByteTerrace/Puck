using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The CPU pixels + dimensions of the single font atlas the <see cref="SdfShapeType.Glyph"/> primitive samples,
/// surfaced by an <see cref="ISdfFrameSource"/> for a one-time static upload via
/// <see cref="SdfWorldEngine.SetGlyphAtlas(System.ReadOnlyMemory{byte}, uint, uint)"/>.
/// </summary>
/// <remarks>
/// The pixels are tightly packed, row-major, top-down RGBA (<c><see cref="Width"/> × <see cref="Height"/> × 4</c>
/// bytes). The true single-channel signed distance the glyph shape marches must live in the alpha channel: the
/// managed MTSDF generator computes it exactly, the coverage-conversion fallback
/// (<c>Puck.Text.SdfCoverageAtlas</c>) replicates its single channel into every channel, and the imported fixed-UI
/// MTSDF atlas carries it by construction, so a consumer of every source samples alpha uniformly.
/// </remarks>
/// <param name="Rgba">The tightly packed, row-major, top-down RGBA atlas pixels.</param>
/// <param name="Width">The atlas width in texels.</param>
/// <param name="Height">The atlas height in texels.</param>
public sealed record SdfGlyphAtlas(ReadOnlyMemory<byte> Rgba, uint Width, uint Height);
