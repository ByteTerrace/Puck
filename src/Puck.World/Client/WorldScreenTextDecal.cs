using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Text;

namespace Puck.World.Client;

/// <summary>Bakes an authored decal-text screen source (<see cref="WorldScreenSource.Text"/>) into the engine's
/// per-cell decal frame (<see cref="SdfScreenDecalFrame"/>): a row-major monospace cell lattice over the packed font
/// atlas, one Unicode scalar per cell.</summary>
/// <remarks>Cell layout (KEEP IN SYNC with <c>sdfSampleGlyphDecal</c> in Assets/Shaders/Sdf/sdf-world.hlsli):
/// four uints per cell — packed unorm2x16 atlas UV top-left, UV bottom-right (decal V runs top-down, the OPPOSITE
/// corner pairing from <c>SdfProgramBuilder.Text</c>'s y-up glyph quads), foreground rgba8, background rgba8 (R in
/// the low byte). Equal packed UV words mark a blank cell (background only) — whitespace, unmapped scalars, and
/// cells past a line's end stay blank. Layout is per-cell: no kerning, advance, or shaping on this tier.</remarks>
internal static class WorldScreenTextDecal {
    private const uint DefaultBackground = 0xFF000000;
    private const uint DefaultForeground = 0xFFFFFFFF;

    // Host-side unorm2x16 pack, u in the low half — the same encoding SdfProgramBuilder.PackUv and the console decal
    // bake use (KEEP IN SYNC with sdfGlyphUnpackUv in Assets/Shaders/Sdf/sdf-vm.hlsli).
    private static uint PackUv(float u, float v) {
        var packedU = ((uint)Math.Clamp(
            value: ((int)MathF.Round(x: (MathF.Max(
                x: 0f,
                y: u
            ) * 65535f))),
            min: 0,
            max: 65535
        ));
        var packedV = ((uint)Math.Clamp(
            value: ((int)MathF.Round(x: (MathF.Max(
                x: 0f,
                y: v
            ) * 65535f))),
            min: 0,
            max: 65535
        ));

        return packedU | (packedV << 16);
    }
    // #RRGGBB → rgba8 with R in the low byte and opaque alpha, matching the shader's unpack; a malformed value takes
    // the fallback (the validator already refused it at load).
    private static uint ParseHexColor(string? value, uint fallback) {
        if (
            (value is not { Length: 7 }) ||
            (value[0] != '#') ||
            !int.TryParse(
            s: value.AsSpan(start: 1),
            style: System.Globalization.NumberStyles.HexNumber,
            provider: System.Globalization.CultureInfo.InvariantCulture,
            result: out var rgb
        )
        ) {
            return fallback;
        }

        var r = ((uint)((rgb >> 16) & 0xFF));
        var g = ((uint)((rgb >> 8) & 0xFF));
        var b = ((uint)(rgb & 0xFF));

        return r | (g << 8) | (b << 16) | 0xFF000000;
    }

    /// <summary>Bakes the authored lines into a decal frame against the resolved catalog font.</summary>
    /// <param name="text">The authored decal-text source (validated: the grid fits the engine budget).</param>
    /// <param name="catalog">The world's packed font catalog.</param>
    /// <returns>The baked frame, ready for <c>SdfWorldEngine.SetScreenDecal</c>.</returns>
    public static SdfScreenDecalFrame Bake(WorldScreenSource.Text text, PackedFontAtlasCatalog catalog) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(catalog);

        var atlas = catalog.Resolve(name: text.Font);
        var lines = text.Lines;
        var widestLine = 0;

        foreach (var line in lines) {
            var count = 0;

            foreach (var _ in line.EnumerateRunes()) {
                count++;
            }

            widestLine = Math.Max(
                val1: widestLine,
                val2: count
            );
        }

        var columns = Math.Max(
            val1: (text.Columns ?? widestLine),
            val2: 1
        );
        var rows = Math.Max(
            val1: (text.Rows ?? lines.Count),
            val2: 1
        );

        // The validator refuses this at load. Keep the bake independently total for future callers: truncating rows
        // would silently alter authored content, and decrementing a one-row oversized grid would manufacture the
        // engine-invalid zero-row shape.
        if ((((long)columns) * rows) > SdfScreenDecalLayout.MaxScreenDecalCells) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(text),
                message: $"The text decal grid {columns}x{rows} exceeds the {SdfScreenDecalLayout.MaxScreenDecalCells}-cell per-screen budget."
            );
        }

        var foreground = ParseHexColor(
            value: text.Foreground,
            fallback: DefaultForeground
        );
        var background = ParseHexColor(
            value: text.Background,
            fallback: DefaultBackground
        );
        var atlasWidth = ((float)atlas.Width);
        var atlasHeight = ((float)atlas.Height);
        var cells = new uint[((columns * rows) * 4)];

        for (var cell = 0; (cell < (columns * rows)); cell++) {
            cells[((cell * 4) + 2)] = foreground;
            cells[((cell * 4) + 3)] = background;
        }

        for (var row = 0; ((row < rows) && (row < lines.Count)); row++) {
            var column = 0;

            foreach (var rune in lines[row].EnumerateRunes()) {
                if (column >= columns) {
                    break;
                }

                var index = (((row * columns) + column) * 4);

                column++;

                if (
                    System.Text.Rune.IsWhiteSpace(value: rune) ||
                    !atlas.TryGetGlyph(
                    unicode: rune.Value,
                    glyph: out var glyph
                ) ||
                    (glyph.AtlasBounds is not { } bounds)
                ) {
                    continue;
                }

                cells[(index + 0)] = PackUv(
                    u: (bounds.Left / atlasWidth),
                    v: (bounds.Top / atlasHeight)
                );
                cells[(index + 1)] = PackUv(
                    u: (bounds.Right / atlasWidth),
                    v: (bounds.Bottom / atlasHeight)
                );
            }
        }

        return new SdfScreenDecalFrame(
            Columns: columns,
            Rows: rows,
            DistanceRange: atlas.DistanceRange,
            Cells: cells
        );
    }
}
