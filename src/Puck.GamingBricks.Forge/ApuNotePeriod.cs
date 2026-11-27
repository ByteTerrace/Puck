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
    /// octave, such as <c>"C5"</c> or <c>"G#2"</c>) to its place in semitones: twelve times its octave plus its semitone
    /// above C, so C4 is 48 and every note a semitone higher counts one more.</summary>
    /// <param name="noteName">The note name, octave 2 through 8; B#8 is refused, being C9.</param>
    /// <returns>The note's semitone number, 24 (C2) through 107 (B8).</returns>
    /// <exception cref="ArgumentException"><paramref name="noteName"/> is empty, is not a note name, or lies outside
    /// octaves 2 through 8.</exception>
    public static int Semitone(string noteName) {
        ArgumentException.ThrowIfNullOrEmpty(noteName);

        var letter = (noteName[0] - 'A');
        var sharp = ((noteName.Length > 1) && (noteName[1] == '#'));
        // B# is the next octave's C, so the semitone count simply carries.
        var semitone = ((letter is >= 0 and <= 6)
            && int.TryParse(s: noteName[(sharp ? 2 : 1)..], style: System.Globalization.NumberStyles.None, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var octave)
            ? (((octave * 12) + LetterSemitones[letter]) + (sharp ? 1 : 0))
            : -1);

        if (semitone is < (2 * 12) or >= (9 * 12)) {
            throw new ArgumentException(
                message: $"'{noteName}' is not a recognized note name (expected a letter A-G, an optional '#', and an octave 2..8, such as \"C5\" or \"G#2\").",
                paramName: nameof(noteName)
            );
        }

        return semitone;
    }
    /// <summary>Resolves a note name (<see cref="Semitone"/>) to its equal-tempered frequency in millihertz. Octaves 2
    /// through 8 all fit the 11-bit period register (<see cref="Period"/>).</summary>
    /// <param name="noteName">The note name, octave 2 through 8.</param>
    /// <returns>The frequency in millihertz, rounded to the nearest.</returns>
    /// <exception cref="ArgumentException"><paramref name="noteName"/> is not a note name in that range.</exception>
    public static int MillihertzFor(string noteName) {
        var semitone = Semitone(noteName: noteName);
        var octave = (semitone / 12);
        var microhertz = ((octave >= 4)
            ? (OctaveFourMicrohertz[(semitone % 12)] << (octave - 4))
            : (OctaveFourMicrohertz[(semitone % 12)] >> (4 - octave)));

        return ((int)((microhertz + 500) / 1000));
    }
    /// <summary>Resolves the period of a divided timer, the form the Deck's tone channels take: a counter reloaded from
    /// the period that steps a waveform of <paramref name="steps"/> slots, so <c>f = clock / (steps * (period + 1))</c>
    /// and <c>period = round(clock / (steps * f)) - 1</c>, in integers over millihertz.</summary>
    /// <param name="millihertz">The note frequency, in millihertz (&gt; 0).</param>
    /// <param name="clockHertz">The timer's input clock, in hertz (&gt; 0).</param>
    /// <param name="steps">The timer periods one waveform cycle spans (&gt; 0): 16 for a pulse, 32 for a triangle.</param>
    /// <returns>The period register value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is not positive.</exception>
    public static int DividedPeriod(int millihertz, long clockHertz, int steps) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: millihertz, other: 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: clockHertz, other: 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: steps, other: 0);

        var divisor = (((long)steps) * millihertz);

        return ((int)((((clockHertz * 1000L) + (divisor / 2)) / divisor) - 1));
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
