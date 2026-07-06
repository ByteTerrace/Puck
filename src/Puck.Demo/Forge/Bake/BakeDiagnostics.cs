using System.Numerics;

namespace Puck.Demo.Forge.Bake;

/// <summary>
/// What one bake measured about itself — the honesty layer. Warnings REPORT budget violations and degradations;
/// the pipeline never auto-fixes them (dropping content to fit a budget is an authoring decision, not a bake's).
/// </summary>
/// <param name="SourceColourCount">Unique rounded-RGB555 colours in the graded native image(s).</param>
/// <param name="TilePaletteCount">Distinct per-tile palettes after dedupe, BEFORE the merge (the pressure gauge).</param>
/// <param name="PaletteCount">The emitted palette count.</param>
/// <param name="PaletteBudget">The palette budget the fit ran against.</param>
/// <param name="TileCountBeforeDedupe">Sliced tiles before deduplication.</param>
/// <param name="TileCount">Unique tiles after deduplication (and flip matching, on CGB).</param>
/// <param name="TileBudget">The VRAM tile budget.</param>
/// <param name="MeanError">The mean per-pixel quantization error (Euclidean, RGB555 space).</param>
/// <param name="MaxTileError">The worst single tile's mean per-pixel error.</param>
/// <param name="OamEntriesPerFrame">The largest OAM entry count any frame needs (0 for a background).</param>
/// <param name="WorstLineSprites">The worst per-scanline sprite count across every frame (0 for a background).</param>
/// <param name="Warnings">The report-only warnings (over-budget palettes/tiles, scanline pressure, unknown style).</param>
/// <param name="Palettes">The emitted palette colours as packed <c>0xRRGGBB</c>, four per palette — the preview
/// overlay's palette strip reads these.</param>
internal sealed record BakeDiagnostics(
    int SourceColourCount,
    int TilePaletteCount,
    int PaletteCount,
    int PaletteBudget,
    int TileCountBeforeDedupe,
    int TileCount,
    int TileBudget,
    float MeanError,
    float MaxTileError,
    int OamEntriesPerFrame,
    int WorstLineSprites,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<uint> Palettes
) {
    /// <summary>The one-line diagnostics summary every bake emits (the tool mode's stderr line and the live
    /// preview's console line share this exact shape).</summary>
    /// <param name="target">The bake target (for the trailing label).</param>
    /// <returns>The formatted line.</returns>
    public string Summarize(BakeTarget target) =>
        $"bake | {SourceColourCount} colours → {PaletteCount} palettes | {TileCount}/{TileBudget} tiles | mean err {MeanError:F2} max {MaxTileError:F2} | {StyleLabel} {TargetLabel(target: target)}";

    /// <summary>The style label the summary line carries (set by the pipeline when it builds the record).</summary>
    public string StyleLabel { get; init; } = "classic";

    private static string TargetLabel(BakeTarget target) =>
        ((target == BakeTarget.Dmg) ? "dmg" : "cgb");
}

/// <summary>One finished bake: the assets, the diagnostics, and the composed preview image (every view laid out in
/// one RGBA grid) plus its average colour (the easel's room glow).</summary>
/// <param name="Assets">The baked hardware assets.</param>
/// <param name="Diagnostics">What the bake measured about itself.</param>
/// <param name="PreviewRgba">The composed preview, tightly packed RGBA8, row-major.</param>
/// <param name="PreviewWidth">The preview width in pixels.</param>
/// <param name="PreviewHeight">The preview height in pixels.</param>
/// <param name="AverageColor">The preview's mean colour in [0, 1] (zero only for an empty preview).</param>
internal sealed record BakeResult(
    BakedAssetBundle Assets,
    BakeDiagnostics Diagnostics,
    byte[] PreviewRgba,
    int PreviewWidth,
    int PreviewHeight,
    Vector3 AverageColor
);
