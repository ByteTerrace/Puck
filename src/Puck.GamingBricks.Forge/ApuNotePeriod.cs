namespace Puck.GamingBricks.Forge;

/// <summary>
/// The shared integer note-period math every APU stream compiler needs: an 11-bit pulse/wave period register value
/// for a note frequency, resolved at BUILD time so the in-ROM driver stays a dumb register pump. Both target compilers share it, so a document
/// resolves to the same periods on either machine —
/// pure millihertz arithmetic, so the same document compiles to byte-identical streams on every run, on every
/// machine, forever (no floats anywhere in the compile path).
/// </summary>
public static class ApuNotePeriod {
    // The twelve equal-tempered notes of octave 4 (A4 = 440 Hz), C first, in microhertz: every other octave is these
    // doubled or halved, so one octave carries the whole range exactly to the millihertz a period needs.
    private static readonly long[] OctaveFourMicrohertz = [
        261_625_565, 277_182_631, 293_664_768, 311_126_984, 329_627_557, 349_228_231,
        369_994_423, 391_995_436, 415_304_698, 440_000_000, 466_163_762, 493_883_301,
    ];
    // Semitones above C for the seven letters, A through G.
    private static readonly int[] LetterSemitones = [9, 11, 0, 2, 4, 5, 7];

    /// <summary>Resolves a note name in scientific pitch notation (a letter A through G, an optional <c>#</c>, and an
    /// octave, such as <c>"C5"</c> or <c>"G#2"</c>) to its equal-tempered frequency in millihertz.</summary>
    /// <param name="noteName">The note name. Its octave is any whose period fits the 11-bit register
    /// (<see cref="Period"/>): C2 through B8.</param>
    /// <returns>The frequency in millihertz, rounded to the nearest.</returns>
    /// <exception cref="ArgumentException"><paramref name="noteName"/> is empty, is not a note name, or names a note
    /// too low for the period register.</exception>
    public static int MillihertzFor(string noteName) {
        ArgumentException.ThrowIfNullOrEmpty(noteName);

        var letter = (noteName[0] - 'A');
        var sharp = ((noteName.Length > 1) && (noteName[1] == '#'));
        var octaveText = noteName[(sharp ? 2 : 1)..];

        if (
            (letter is < 0 or > 6) ||
            !int.TryParse(s: octaveText, style: System.Globalization.NumberStyles.None, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var octave) ||
            (octave is < 2 or > 8)
        ) {
            throw new ArgumentException(
                message: $"'{noteName}' is not a recognized note name (expected a letter A-G, an optional '#', and an octave 2..8, such as \"C5\" or \"G#2\").",
                paramName: nameof(noteName)
            );
        }

        var semitone = (LetterSemitones[letter] + (sharp ? 1 : 0));

        // B# is the next octave's C.
        if (semitone == 12) {
            semitone = 0;
            ++octave;
        }

        var microhertz = ((octave >= 4)
            ? (OctaveFourMicrohertz[semitone] << (octave - 4))
            : (OctaveFourMicrohertz[semitone] >> (4 - octave)));
        var millihertz = ((int)((microhertz + 500) / 1000));

        if ((131_072_000L / millihertz) > 2048) {
            throw new ArgumentException(
                message: $"'{noteName}' is below the lowest note the period register reaches (C2).",
                paramName: nameof(noteName)
            );
        }

        return millihertz;
    }
    /// <summary>Resolves the 11-bit period register value for a frequency: <c>period = 2048 - round(131072 Hz / f)</c>
    /// — pure integer math over millihertz so every build emits identical bytes.</summary>
    /// <param name="millihertz">The note frequency, in millihertz (&gt; 0).</param>
    /// <returns>The period value (0..2047).</returns>
    public static int Period(int millihertz) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            value: millihertz,
            other: 0,
            paramName: nameof(millihertz)
        );

        return (2048 - ((int)(((131_072_000L + (millihertz / 2)) / millihertz))));
    }
}
