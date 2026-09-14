using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The CPU pixels + dimensions of the single font atlas the <see cref="SdfShapeType.Glyph"/> primitive samples,
/// surfaced by an <see cref="ISdfFrameSource"/> for a one-time static upload via
/// <see cref="SdfWorldEngine.SetGlyphAtlas(System.ReadOnlyMemory{byte}, uint, uint)"/>.
/// </summary>
/// <remarks>
/// The pixels are tightly packed, row-major, top-down RGBA (<c><see cref="Width"/> × <see cref="Height"/> × 4</c>
/// bytes). The single-channel signed-distance samples consumed by the glyph shape must live in alpha,
/// as they do in generated and imported MTSDF atlases. RGB carries corner-reconstruction channels for coverage.
/// Upload the same pixels and dimensions used by SdfProgramBuilder.Glyph: its packed sampling correction bounds
/// the reconstructed alpha field's slope. This does not recover the exact source outline from quantized samples.
/// </remarks>
/// <param name="Rgba">The tightly packed, row-major, top-down RGBA atlas pixels.</param>
/// <param name="Width">The atlas width in texels.</param>
/// <param name="Height">The atlas height in texels.</param>
public sealed record SdfGlyphAtlas(ReadOnlyMemory<byte> Rgba, uint Width, uint Height);
