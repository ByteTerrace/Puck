namespace Puck.SdfVm;

/// <summary>
/// One frame of a GLYPH DECAL bound to a screen slot: a grid of glyph cells + colours the screen's
/// <see cref="SdfShapeType.ScreenSlab"/> face samples AT THE HIT as dense reading text (the material-level text tier —
/// see <see cref="SdfWorldEngine.SetScreenDecal"/>), instead of a bound image. Supplied per frame through
/// <see cref="ISdfFrameSource.ScreenDecals"/>; a <see langword="null"/> provider result clears the slot back to the
/// image/procedural path (<see cref="SdfWorldEngine.ClearScreenDecal"/>) — the documented non-atlas-host degrade.
/// </summary>
/// <param name="Columns">The grid column count (&gt; 0).</param>
/// <param name="Rows">The grid row count (&gt; 0).</param>
/// <param name="DistanceRange">The glyph atlas's SDF distance range in texels (the decal's analytic-AA source; 0 = a
/// raw coverage atlas). Typically <see cref="Puck.Text.FontAtlas.DistanceRange"/> of the atlas the decal samples.</param>
/// <param name="Cells">The packed cells, row-major (<paramref name="Rows"/> × <paramref name="Columns"/>), four uints
/// each: (packedUvTopLeft, packedUvBottomRight [unorm2x16], fgRgba8, bgRgba8). A blank cell packs equal UV corners.</param>
public sealed record SdfScreenDecalFrame(int Columns, int Rows, float DistanceRange, ReadOnlyMemory<uint> Cells);
