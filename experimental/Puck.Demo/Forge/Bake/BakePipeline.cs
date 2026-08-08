using Puck.Abstractions.Gpu;

namespace Puck.Demo.Forge.Bake;

/// <summary>
/// The bake's two halves, split on purpose: <see cref="Rasterize"/> renders ONE view on the GPU (the live preview
/// spreads views across frames on the render thread; the tool mode just loops), and <see cref="RunCpu"/> runs every
/// CPU stage — grade → reduce → palette fit → quantize → tile assembly → diagnostics → preview — deterministically
/// on whatever thread has time (the live preview uses a worker task). <see cref="Run"/> is the convenience that does
/// both for the headless tool.
/// </summary>
internal static class BakePipeline {
    // The DMG display ramp (shade 0 = lightest), the pea-green panel look — preview colours AND the wire palette.
    private static readonly ushort[] DmgRamp = [
        BakeColor.PackFrom8(b: 208, g: 248, r: 224),
        BakeColor.PackFrom8(b: 112, g: 192, r: 136),
        BakeColor.PackFrom8(b: 86, g: 104, r: 52),
        BakeColor.PackFrom8(b: 32, g: 24, r: 8),
    ];
    // The identity shade ramp: colour index i displays shade i.
    private const byte DmgIdentityRamp = 0xE4;

    // One view after grade + reduce: native-resolution pixels plus the sprite foreground mask.
    private sealed record NativeView(string Name, byte[] Rgba, bool[]? Mask);

    // Everything the target-specific quantization produced, shape-shared between the CGB and DMG paths.
    private sealed record QuantizeOutcome(
        List<QuantizedView> Views,
        IReadOnlyList<ushort[]> PreviewPalettes,
        BakedPaletteSet WirePalettes,
        DmgRegisters? Registers,
        int SourceColourCount,
        int TilePaletteCount,
        int PaletteBudget,
        float MeanError,
        float MaxTileError,
        List<string> Notes,
        List<uint> DiagnosticPalettes
    );

    /// <summary>Rasterizes ONE view of a plan through a caller-supplied render callback and wraps the readback. The
    /// raster extent is the plan's native size times the style's supersample factor.</summary>
    /// <param name="device">The GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="viewIndex">Which view to rasterize.</param>
    /// <returns>The rasterized view.</returns>
    public static RasterizedView Rasterize(IGpuDeviceContext device, IGpuComputeServices gpu, BakePlan plan, int viewIndex) {
        ArgumentNullException.ThrowIfNull(plan);

        var view = plan.Views[viewIndex];
        var width = (plan.NativeWidth * plan.Style.SupersampleFactor);
        var height = (plan.NativeHeight * plan.Style.SupersampleFactor);
        var rgba = SceneForge.Render(camera: view.Camera, device: device, gpu: gpu, height: height, program: view.Program, width: width);

        return new RasterizedView(Height: height, Name: view.Name, Rgba: rgba, Width: width);
    }

    /// <summary>Rasterizes every view and runs the CPU half — the headless tool path.</summary>
    /// <param name="device">The GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="overlayMode">The preview overlay mode (0 = bare, 1 = strip + ticks, 2 = + tile grid).</param>
    /// <param name="extraWarnings">Warnings the planner already collected (e.g. an unknown style name).</param>
    /// <returns>The finished bake.</returns>
    public static BakeResult Run(IGpuDeviceContext device, IGpuComputeServices gpu, BakePlan plan, int overlayMode = 0, IReadOnlyList<string>? extraWarnings = null) {
        ArgumentNullException.ThrowIfNull(plan);

        var views = new List<RasterizedView>(capacity: plan.Views.Count);

        for (var index = 0; (index < plan.Views.Count); index++) {
            views.Add(item: Rasterize(device: device, gpu: gpu, plan: plan, viewIndex: index));
        }

        return RunCpu(extraWarnings: extraWarnings, overlayMode: overlayMode, plan: plan, views: views);
    }

    /// <summary>Runs every CPU stage over already-rasterized views. Pure and deterministic — safe on a worker
    /// thread while the render thread keeps producing frames.</summary>
    /// <param name="plan">The plan the views were rasterized from.</param>
    /// <param name="views">The rasterized views, in plan order.</param>
    /// <param name="overlayMode">The preview overlay mode.</param>
    /// <param name="extraWarnings">Warnings collected before the bake ran.</param>
    /// <returns>The finished bake.</returns>
    public static BakeResult RunCpu(BakePlan plan, IReadOnlyList<RasterizedView> views, int overlayMode = 0, IReadOnlyList<string>? extraWarnings = null) {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(views);

        var natives = GradeAndReduce(plan: plan, views: views);
        var outcome = ((plan.Target == BakeTarget.Cgb)
            ? QuantizeCgb(natives: natives, plan: plan)
            : QuantizeDmg(natives: natives, plan: plan));

        return Finish(extraWarnings: extraWarnings, outcome: outcome, overlayMode: overlayMode, plan: plan);
    }

    // ---- stage: grade + reduce + mask + outline -------------------------------------------------------------------

    private static List<NativeView> GradeAndReduce(BakePlan plan, IReadOnlyList<RasterizedView> views) {
        var natives = new List<NativeView>(capacity: views.Count);

        foreach (var view in views) {
            StyleGrade.Grade(applySaturation: (plan.Target == BakeTarget.Cgb), rgba: view.Rgba, style: plan.Style);

            var native = HgbImage.BoxReduce(factor: plan.Style.SupersampleFactor, height: view.Height, outHeight: out _, outWidth: out _, rgba: view.Rgba, width: view.Width);
            var mask = ((plan.Intent == BakeIntent.Sprite)
                ? StyleGrade.ForegroundMask(height: plan.NativeHeight, rgba: native, width: plan.NativeWidth)
                : null);

            if (plan.Style.ExtractOutline) {
                if (mask is not null) {
                    StyleGrade.OutlineSprite(height: plan.NativeHeight, mask: mask, rgba: native, width: plan.NativeWidth);
                } else {
                    StyleGrade.OutlineBackground(height: plan.NativeHeight, rgba: native, threshold: plan.Style.OutlineThreshold, width: plan.NativeWidth);
                }
            }

            natives.Add(item: new NativeView(Mask: mask, Name: view.Name, Rgba: native));
        }

        return natives;
    }

    // ---- stage: the CGB palette fit + quantization ----------------------------------------------------------------

    private static QuantizeOutcome QuantizeCgb(List<NativeView> natives, BakePlan plan) {
        var sprite = (plan.Intent == BakeIntent.Sprite);
        var usableColours = (sprite ? 3 : 4);
        var budget = Math.Min(
            val1: (sprite ? plan.Budget.MaxObjectPalettes : plan.Budget.MaxBackgroundPalettes),
            val2: (sprite ? plan.Style.MaxObjectPalettes : plan.Style.MaxBackgroundPalettes)
        );
        var histograms = BuildHistograms(natives: natives, plan: plan, sourceColours: out var sourceColours);
        var fit = PaletteFitter.Fit(paletteBudget: budget, tiles: histograms, usableColours: usableColours);
        var quantized = new List<QuantizedView>(capacity: natives.Count);
        var tilesPerView = ((plan.NativeWidth / 8) * (plan.NativeHeight / 8));
        var errors = new ErrorAccumulator();

        // A degenerate sprite can render nothing quantizable — a fully subtractive scene, or a fill matching the
        // corner sample — leaving an empty foreground mask in every facing and thus a zero-palette fit. Substitute
        // one transparent palette so the per-tile lookup resolves (its colours are never sampled: every pixel stays
        // index 0, a blank view) and surface the emptiness plainly instead of a stale easel + IndexOutOfRange.
        var palettes = fit.Palettes;
        var notes = new List<string>();

        if (palettes.Count == 0) {
            palettes = [new ushort[usableColours]];
            notes.Add(item: "creation rendered empty — nothing to bake (a fully subtractive or corner-matching scene?)");
        }

        for (var viewIndex = 0; (viewIndex < natives.Count); viewIndex++) {
            quantized.Add(item: QuantizeCgbView(
                assignments: fit.Assignments.AsSpan(start: (viewIndex * tilesPerView), length: tilesPerView).ToArray(),
                errors: errors,
                native: natives[viewIndex],
                palettes: palettes,
                plan: plan,
                sprite: sprite
            ));
        }

        return new QuantizeOutcome(
            DiagnosticPalettes: FlattenPalettes(palettes: palettes, transparentSlot: sprite),
            MaxTileError: errors.MaxTileMean,
            MeanError: errors.Mean,
            Notes: notes,
            PaletteBudget: budget,
            PreviewPalettes: palettes,
            Registers: null,
            SourceColourCount: sourceColours,
            TilePaletteCount: fit.TilePaletteCount,
            Views: quantized,
            WirePalettes: BakeColor.EncodePalettes(palettes: palettes, reserveTransparentSlot: sprite)
        );
    }
    private static QuantizedView QuantizeCgbView(int[] assignments, ErrorAccumulator errors, NativeView native, IReadOnlyList<ushort[]> palettes, BakePlan plan, bool sprite) {
        var indices = new byte[(plan.NativeWidth * plan.NativeHeight)];
        var tilesWide = (plan.NativeWidth / 8);

        for (var tileY = 0; (tileY < (plan.NativeHeight / 8)); tileY++) {
            for (var tileX = 0; (tileX < tilesWide); tileX++) {
                var palette = palettes[assignments[((tileY * tilesWide) + tileX)]];

                errors.BeginTile();

                for (var row = 0; (row < 8); row++) {
                    for (var column = 0; (column < 8); column++) {
                        var x = ((tileX * 8) + column);
                        var y = ((tileY * 8) + row);
                        var pixel = ((y * plan.NativeWidth) + x);

                        if (sprite && !native.Mask![pixel]) {
                            continue; // transparent — index 0, no error contribution
                        }

                        indices[pixel] = QuantizeCgbPixel(errors: errors, native: native, palette: palette, pixel: pixel, plan: plan, sprite: sprite, x: x, y: y);
                    }
                }

                errors.EndTile();
            }
        }

        return new QuantizedView(Height: plan.NativeHeight, Indices: indices, Mask: native.Mask, Name: native.Name, TilePalettes: assignments, Width: plan.NativeWidth);
    }
    private static byte QuantizeCgbPixel(ErrorAccumulator errors, NativeView native, ushort[] palette, int pixel, BakePlan plan, bool sprite, int x, int y) {
        var offset = (pixel * 4);
        var dither = StyleGrade.DitherOffset(style: plan.Style, x: x, y: y);
        var dithered = BakeColor.PackFrom8(
            b: (int)MathF.Round(x: (native.Rgba[(offset + 2)] + dither)),
            g: (int)MathF.Round(x: (native.Rgba[(offset + 1)] + dither)),
            r: (int)MathF.Round(x: (native.Rgba[offset] + dither))
        );
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var candidate = 0; (candidate < palette.Length); candidate++) {
            var distance = BakeColor.DistanceSquared(a: dithered, b: palette[candidate]);

            if (distance < bestDistance) {
                bestDistance = distance;
                best = candidate;
            }
        }

        var source = BakeColor.PackFrom8(b: native.Rgba[(offset + 2)], g: native.Rgba[(offset + 1)], r: native.Rgba[offset]);

        errors.Add(distance: MathF.Sqrt(x: BakeColor.DistanceSquared(a: source, b: palette[best])));

        return (byte)(sprite ? (best + 1) : best);
    }
    private static List<List<HistogramEntry>> BuildHistograms(List<NativeView> natives, BakePlan plan, out int sourceColours) {
        var histograms = new List<List<HistogramEntry>>();
        var distinct = new HashSet<ushort>();

        foreach (var native in natives) {
            for (var tileY = 0; (tileY < (plan.NativeHeight / 8)); tileY++) {
                for (var tileX = 0; (tileX < (plan.NativeWidth / 8)); tileX++) {
                    histograms.Add(item: TileHistogram(distinct: distinct, native: native, plan: plan, tileX: tileX, tileY: tileY));
                }
            }
        }

        sourceColours = distinct.Count;

        return histograms;
    }
    private static List<HistogramEntry> TileHistogram(HashSet<ushort> distinct, NativeView native, BakePlan plan, int tileX, int tileY) {
        // Counted through a local dictionary but EMITTED sorted by colour — no dictionary order ever escapes.
        var counts = new Dictionary<ushort, int>();

        for (var row = 0; (row < 8); row++) {
            for (var column = 0; (column < 8); column++) {
                var pixel = ((((tileY * 8) + row) * plan.NativeWidth) + ((tileX * 8) + column));

                if ((native.Mask is { } mask) && !mask[pixel]) {
                    continue;
                }

                var offset = (pixel * 4);
                var colour = BakeColor.PackFrom8(b: native.Rgba[(offset + 2)], g: native.Rgba[(offset + 1)], r: native.Rgba[offset]);

                counts[colour] = (counts.GetValueOrDefault(key: colour) + 1);
                _ = distinct.Add(item: colour);
            }
        }

        var histogram = new List<HistogramEntry>(capacity: counts.Count);

        foreach (var colour in counts.Keys.Order()) {
            histogram.Add(item: new HistogramEntry(Colour: colour, Count: counts[colour]));
        }

        return histogram;
    }

    // ---- stage: the DMG luma quantization -------------------------------------------------------------------------

    private static QuantizeOutcome QuantizeDmg(List<NativeView> natives, BakePlan plan) {
        var sprite = (plan.Intent == BakeIntent.Sprite);
        var quantized = new List<QuantizedView>(capacity: natives.Count);
        var tilesPerView = ((plan.NativeWidth / 8) * (plan.NativeHeight / 8));
        var errors = new ErrorAccumulator();
        var distinct = new HashSet<ushort>();

        foreach (var native in natives) {
            quantized.Add(item: QuantizeDmgView(distinct: distinct, errors: errors, native: native, plan: plan, sprite: sprite, tilesPerView: tilesPerView));
        }

        // The preview palette: the full ramp for a background; for a sprite, indices 1..3 map ramp shades 1..3
        // (colour 0 is transparent — the 3-usable-shades reality the note reports).
        var previewPalettes = new List<ushort[]> { (sprite ? [DmgRamp[1], DmgRamp[2], DmgRamp[3]] : DmgRamp) };
        var notes = new List<string>();

        if (sprite) {
            notes.Add(item: "dmg sprite: colour 0 is transparent — 3 usable shades (the luma curve is the knob)");
        }

        return new QuantizeOutcome(
            DiagnosticPalettes: FlattenPalettes(palettes: previewPalettes, transparentSlot: sprite),
            MaxTileError: errors.MaxTileMean,
            MeanError: errors.Mean,
            Notes: notes,
            PaletteBudget: 1,
            PreviewPalettes: previewPalettes,
            Registers: new DmgRegisters(Bgp: DmgIdentityRamp, Obp0: DmgIdentityRamp, Obp1: DmgIdentityRamp),
            SourceColourCount: distinct.Count,
            TilePaletteCount: 1,
            Views: quantized,
            WirePalettes: BakeColor.EncodePalettes(palettes: previewPalettes, reserveTransparentSlot: sprite)
        );
    }
    private static QuantizedView QuantizeDmgView(HashSet<ushort> distinct, ErrorAccumulator errors, NativeView native, BakePlan plan, bool sprite, int tilesPerView) {
        var indices = new byte[(plan.NativeWidth * plan.NativeHeight)];

        for (var tile = 0; (tile < tilesPerView); tile++) {
            errors.BeginTile();
            QuantizeDmgTile(distinct: distinct, errors: errors, indices: indices, native: native, plan: plan, sprite: sprite, tile: tile);
            errors.EndTile();
        }

        return new QuantizedView(Height: plan.NativeHeight, Indices: indices, Mask: native.Mask, Name: native.Name, TilePalettes: new int[tilesPerView], Width: plan.NativeWidth);
    }
    private static void QuantizeDmgTile(HashSet<ushort> distinct, ErrorAccumulator errors, byte[] indices, NativeView native, BakePlan plan, bool sprite, int tile) {
        var tilesWide = (plan.NativeWidth / 8);
        var tileX = (tile % tilesWide);
        var tileY = (tile / tilesWide);

        for (var row = 0; (row < 8); row++) {
            for (var column = 0; (column < 8); column++) {
                var x = ((tileX * 8) + column);
                var y = ((tileY * 8) + row);
                var pixel = ((y * plan.NativeWidth) + x);

                if (sprite && !native.Mask![pixel]) {
                    continue;
                }

                var offset = (pixel * 4);

                _ = distinct.Add(item: BakeColor.PackFrom8(b: native.Rgba[(offset + 2)], g: native.Rgba[(offset + 1)], r: native.Rgba[offset]));

                var dither = (StyleGrade.DitherOffset(style: plan.Style, x: x, y: y) / 255f);
                var luma = Math.Clamp(
                    max: 1f,
                    min: 0f,
                    value: (StyleGrade.Luma(b: (native.Rgba[(offset + 2)] / 255f), g: (native.Rgba[(offset + 1)] / 255f), r: (native.Rgba[offset] / 255f)) + dither)
                );

                // A background uses all four shades; a sprite folds into the 3 usable ones (colour 0 = transparent).
                var shade = (sprite
                    ? (1 + Math.Min(val1: 2, val2: (int)MathF.Floor(x: ((1f - luma) * 3f))))
                    : StyleGrade.DmgShade(luma: luma));

                indices[pixel] = (byte)shade;
                // The honest DMG error: how far the pixel's grey level sits from its shade's nominal grey.
                errors.Add(distance: MathF.Abs(x: ((luma * 31f) - (((3 - shade) / 3f) * 31f))));
            }
        }
    }

    // ---- stage: assembly + diagnostics + preview ------------------------------------------------------------------

    private static BakeResult Finish(IReadOnlyList<string>? extraWarnings, QuantizeOutcome outcome, int overlayMode, BakePlan plan) {
        var sprite = (plan.Intent == BakeIntent.Sprite);
        BakedBackground? background = null;
        List<BakedSpriteSet> sprites = [];
        int tileCountBefore, tileCount, maxEntries = 0, worstLine = 0;

        if (sprite) {
            var assembly = TileAssembler.AssembleSprites(palettes: outcome.WirePalettes, registers: outcome.Registers, target: plan.Target, views: outcome.Views);

            sprites.Add(item: assembly.Sprites);
            tileCountBefore = assembly.TileCountBeforeDedupe;
            tileCount = assembly.TileCount;
            maxEntries = assembly.MaxEntriesPerFrame;
            worstLine = assembly.WorstLineSprites;
        } else {
            var assembly = TileAssembler.AssembleBackground(palettes: outcome.WirePalettes, registers: outcome.Registers, target: plan.Target, view: outcome.Views[0]);

            background = assembly.Background;
            tileCountBefore = assembly.TileCountBeforeDedupe;
            tileCount = assembly.TileCount;
        }

        var warnings = CollectWarnings(extraWarnings: extraWarnings, maxEntries: maxEntries, outcome: outcome, plan: plan, tileCount: tileCount, worstLine: worstLine);
        var diagnostics = new BakeDiagnostics(
            MaxTileError: outcome.MaxTileError,
            MeanError: outcome.MeanError,
            OamEntriesPerFrame: maxEntries,
            PaletteBudget: outcome.PaletteBudget,
            PaletteCount: outcome.WirePalettes.Count,
            Palettes: outcome.DiagnosticPalettes,
            SourceColourCount: outcome.SourceColourCount,
            TileBudget: plan.Budget.MaxTiles,
            TileCount: tileCount,
            TileCountBeforeDedupe: tileCountBefore,
            TilePaletteCount: outcome.TilePaletteCount,
            Warnings: warnings,
            WorstLineSprites: worstLine
        ) {
            StyleLabel = plan.Style.Name,
        };

        var cells = new List<PreviewCell>(capacity: outcome.Views.Count);

        foreach (var view in outcome.Views) {
            cells.Add(item: PreviewComposer.ExpandView(palettes: outcome.PreviewPalettes, transparentSlotReserved: sprite, view: view));
        }

        var (previewRgba, previewWidth, previewHeight, average) = PreviewComposer.Compose(cells: cells, diagnostics: diagnostics, overlayMode: overlayMode, rows: (sprite ? 4 : 1));

        return new BakeResult(
            Assets: new BakedAssetBundle(Background: background, Sprites: sprites, StyleName: plan.Style.Name, Target: plan.Target),
            AverageColor: average,
            Diagnostics: diagnostics,
            PreviewHeight: previewHeight,
            PreviewRgba: previewRgba,
            PreviewWidth: previewWidth
        );
    }
    private static List<string> CollectWarnings(IReadOnlyList<string>? extraWarnings, int maxEntries, QuantizeOutcome outcome, BakePlan plan, int tileCount, int worstLine) {
        var warnings = new List<string>(collection: (extraWarnings ?? []));

        warnings.AddRange(collection: outcome.Notes);

        if (outcome.TilePaletteCount > outcome.PaletteBudget) {
            warnings.Add(item: $"palette pressure: {outcome.TilePaletteCount} tile palettes → {outcome.WirePalettes.Count} (budget {outcome.PaletteBudget}; check the preview for banding)");
        }

        if (outcome.WirePalettes.Count > outcome.PaletteBudget) {
            warnings.Add(item: $"over-budget palettes after merge: {outcome.WirePalettes.Count} > {outcome.PaletteBudget}");
        }

        if (tileCount > plan.Budget.MaxTiles) {
            warnings.Add(item: $"tile budget exceeded: {tileCount} unique tiles > {plan.Budget.MaxTiles} (tile ids wrap — simplify the scene)");
        }

        if (maxEntries > plan.Budget.OamTotal) {
            warnings.Add(item: $"OAM budget exceeded: a frame needs {maxEntries} entries > {plan.Budget.OamTotal}");
        }

        if (worstLine > plan.Budget.OamPerLine) {
            warnings.Add(item: $"scanline pressure: {worstLine} sprites share a line > the {plan.Budget.OamPerLine}-per-line limit (expect dropout)");
        }

        return warnings;
    }

    // Streams per-pixel errors and tracks the per-tile mean maximum — one pass, no allocation per pixel.
    private sealed class ErrorAccumulator {
        private double m_total;
        private long m_count;
        private double m_tileTotal;
        private long m_tileCount;

        public float MaxTileMean { get; private set; }
        public float Mean => ((m_count > 0) ? (float)(m_total / m_count) : 0f);

        public void BeginTile() {
            m_tileTotal = 0.0;
            m_tileCount = 0L;
        }
        public void Add(float distance) {
            m_total += distance;
            m_count++;
            m_tileTotal += distance;
            m_tileCount++;
        }
        public void EndTile() {
            if (m_tileCount > 0) {
                MaxTileMean = Math.Max(val1: MaxTileMean, val2: (float)(m_tileTotal / m_tileCount));
            }
        }
    }

    // Four 0xRRGGBB entries per palette (slot 0 black when it is the transparent OBJ slot; short palettes repeat
    // their last colour) — the fixed shape the preview overlay's strip expects.
    private static List<uint> FlattenPalettes(IReadOnlyList<ushort[]> palettes, bool transparentSlot) {
        var flattened = new List<uint>(capacity: (palettes.Count * 4));

        foreach (var palette in palettes) {
            var emitted = 0;

            if (transparentSlot) {
                flattened.Add(item: 0x000000u);
                emitted++;
            }

            for (var slot = 0; ((slot < palette.Length) && (emitted < 4)); slot++, emitted++) {
                flattened.Add(item: BakeColor.ToRgb24(colour: palette[slot]));
            }

            while (emitted < 4) {
                flattened.Add(item: ((palette.Length > 0) ? BakeColor.ToRgb24(colour: palette[^1]) : 0x000000u));
                emitted++;
            }
        }

        return flattened;
    }
}
