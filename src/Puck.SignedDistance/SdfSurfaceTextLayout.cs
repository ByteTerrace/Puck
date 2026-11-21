namespace Puck.SignedDistance;

/// <summary>
/// The capacity and packing of surface text: runs of glyph quads a text paint (<see cref="SdfPaint.Text"/>) draws on
/// the surface of its material at the hit, lit as the surface is, where a screen's glyph decal
/// (<see cref="SdfScreenDecalLayout"/>) is a display's own picture. A program names each run by a slot; the host
/// supplies a slot's glyphs each frame, so the lettering changes without a new program or a bake.
/// </summary>
/// <remarks>The packing is read by <c>sdfSurfaceTextInk</c> in <c>shade/sdf-environment.hlsli</c>: each glyph is two
/// four-word entries. The first is the glyph quad's rectangle in the paint's bound box, as four floats (minimum x and
/// y, maximum x and y), each axis running from -1 at the box's low face to 1 at its high face with y up. The second is
/// the glyph's atlas cell (its top-left and bottom-right corners, each a packed unorm2x16 UV), its ink as rgba8 with
/// red in the low byte and its coverage in alpha, and its flags (<see cref="TintFlag"/>).</remarks>
public static class SdfSurfaceTextLayout {
    /// <summary>The most surface text slots one program's paints name (<c>SDF_MAX_SURFACE_TEXTS</c>).</summary>
    public const int MaxTexts = 64;
    /// <summary>The most glyph quads one slot draws (<c>SDF_MAX_SURFACE_TEXT_GLYPHS</c>); a longer text draws its first
    /// this many.</summary>
    public const int MaxGlyphs = 64;
    /// <summary>The four-word entries one glyph packs.</summary>
    public const int EntriesPerGlyph = 2;
    /// <summary>The words one glyph packs.</summary>
    public const int WordsPerGlyph = (EntriesPerGlyph * 4);
    /// <summary>The flag of a glyph whose packed ink replaces the paint's own colour (<c>SDF_SURFACE_TEXT_TINT</c>); a
    /// glyph without it draws in the paint's colour at its packed coverage.</summary>
    public const uint TintFlag = 1u;
}
