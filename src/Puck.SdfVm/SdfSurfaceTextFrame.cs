using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// One frame of a surface text slot: the glyph quads a text paint (<see cref="SdfPaint.Text"/>) draws on its material's
/// surface this frame. Supplied per frame through <see cref="ISdfFrameSource.SurfaceTexts"/>; a
/// <see langword="null"/> answer clears the slot, and its paint then draws nothing.
/// </summary>
/// <param name="DistanceRange">The glyph atlas's distance range in texels, the quads' antialiasing source (0 = a raw
/// coverage atlas). Typically <see cref="Puck.Text.FontAtlas.DistanceRange"/> of the atlas the quads sample.</param>
/// <param name="Glyphs">The packed glyphs, <see cref="SdfSurfaceTextLayout.WordsPerGlyph"/> words each, at most
/// <see cref="SdfSurfaceTextLayout.MaxGlyphs"/> of them, in the packing <see cref="SdfSurfaceTextLayout"/> states.</param>
public sealed record SdfSurfaceTextFrame(float DistanceRange, ReadOnlyMemory<uint> Glyphs);
