namespace Puck.HumbleGamingDeck;

/// <summary>
/// Converts the PPU's nine-bit pixel codes to <c>0x00RRGGBB</c> colours for presentation. The table is decoded once from
/// a model of the RP2C02G's composite output: each code is a square wave between two of the chip's measured voltage
/// levels at one of twelve hue phases, the emphasis bits attenuate parts of the wave, and averaging twelve samples of a
/// colour cycle gives its luma and chroma, which convert to RGB. The arithmetic is floating point and runs only here, on
/// the presentation side; the machine's state never sees it.
/// https://www.nesdev.org/wiki/NTSC_video
/// </summary>
public static class HgdPalette {
    // Voltage levels relative to sync, the colour-burst black and white points, and the emphasis attenuation.
    private const double Black = 0.518;
    private const double Attenuation = 0.746;
    private const double White = 1.962;

    private static readonly uint[] ColourTable = Decode();

    /// <summary>Gets the colour of every pixel code: 512 entries, indexed by the code.</summary>
    public static ReadOnlySpan<uint> Colours => ColourTable;

    /// <summary>Converts a frame of pixel codes to <c>0x00RRGGBB</c> colours.</summary>
    /// <param name="codes">The pixel codes.</param>
    /// <param name="colours">The destination, at least as long as <paramref name="codes"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="colours"/> is shorter than <paramref name="codes"/>.</exception>
    public static void Convert(ReadOnlySpan<ushort> codes, Span<uint> colours) {
        if (colours.Length < codes.Length) {
            throw new ArgumentException(
                message: "The colour destination is shorter than the frame.",
                paramName: nameof(colours)
            );
        }

        var table = ColourTable;

        for (var index = 0; (index < codes.Length); ++index) {
            colours[index] = table[codes[index] & 0x1FF];
        }
    }

    private static uint[] Decode() {
        ReadOnlySpan<double> levels = [0.350, 0.518, 0.962, 1.550, 1.094, 1.506, 1.962, 1.962];
        var colours = new uint[512];

        for (var code = 0; (code < 512); ++code) {
            var colour = code & 0x0F;
            var level = (code >> 4) & 3;
            var emphasis = (code >> 6);

            // Colours 14 and 15 are forced to level 1; colour 0 emits only its high voltage and 13-15 only their low.
            if (colour > 13) {
                level = 1;
            }

            var low = levels[level];
            var high = levels[(4 + level)];

            if (colour == 0) {
                low = high;
            }
            if (colour > 12) {
                high = low;
            }

            var y = 0D;
            var i = 0D;
            var q = 0D;

            for (var phase = 0; (phase < 12); ++phase) {
                var signal = (InColourPhase(colour: colour, phase: phase) ? high : low);

                if (
                    (((emphasis & 1) != 0) && InColourPhase(colour: 0, phase: phase)) ||
                    (((emphasis & 2) != 0) && InColourPhase(colour: 4, phase: phase)) ||
                    (((emphasis & 4) != 0) && InColourPhase(colour: 8, phase: phase))
                ) {
                    signal *= Attenuation;
                }

                var normalized = ((signal - Black) / (White - Black));
                var angle = ((Math.PI * (phase + 0.5)) / 6.0);

                y += normalized;
                i += (normalized * Math.Cos(d: angle));
                q += (normalized * Math.Sin(a: angle));
            }
            y /= 12.0;
            i /= 12.0;
            q /= 12.0;
            colours[code] = (Channel(value: ((y + (0.946882 * i)) + (0.623557 * q))) << 16) |
                (Channel(value: ((y - (0.274788 * i)) - (0.635691 * q))) << 8) |
                Channel(value: ((y - (1.108545 * i)) + (1.709007 * q)));
        }

        return colours;
    }
    private static bool InColourPhase(int colour, int phase) =>
        (((colour + phase) % 12) < 6);
    private static uint Channel(double value) =>
        ((uint)Math.Clamp(
            max: 255.0,
            min: 0.0,
            value: Math.Round(a: (value * 255.0))
        ));
}
