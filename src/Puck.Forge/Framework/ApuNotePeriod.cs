namespace Puck.Forge.Framework;

/// <summary>
/// The shared integer note-period math every APU stream compiler needs: an 11-bit pulse/wave period register value
/// for a note frequency, resolved at BUILD time so the in-ROM driver stays a dumb register pump. <see cref="SoundTables"/>
/// delegates to this, the shared integer math, so <c>AudioDocumentCompiler</c> shares the exact same integer math —
/// pure millihertz arithmetic, so the same document compiles to byte-identical streams on every run, on every
/// machine, forever (no floats anywhere in the compile path).
/// </summary>
internal static class ApuNotePeriod {
    /// <summary>Common note frequencies in millihertz (equal temperament, A4 = 440 Hz) — integers so the period math
    /// is exact and the built streams are byte-identical across runs. Named by scientific pitch notation.</summary>
    public static readonly IReadOnlyDictionary<string, int> Millihertz = new Dictionary<string, int>(comparer: StringComparer.Ordinal) {
        ["C3"] = 130_813,
        ["C#3"] = 138_591,
        ["D3"] = 146_832,
        ["D#3"] = 155_563,
        ["E3"] = 164_814,
        ["F3"] = 174_614,
        ["F#3"] = 184_997,
        ["G3"] = 195_998,
        ["G#3"] = 207_652,
        ["A3"] = 220_000,
        ["A#3"] = 233_082,
        ["B3"] = 246_942,
        ["C4"] = 261_626,
        ["C#4"] = 277_183,
        ["D4"] = 293_665,
        ["D#4"] = 311_127,
        ["E4"] = 329_628,
        ["F4"] = 349_228,
        ["F#4"] = 369_994,
        ["G4"] = 391_995,
        ["G#4"] = 415_305,
        ["A4"] = 440_000,
        ["A#4"] = 466_164,
        ["B4"] = 493_883,
        ["C5"] = 523_251,
        ["C#5"] = 554_365,
        ["D5"] = 587_330,
        ["D#5"] = 622_254,
        ["E5"] = 659_255,
        ["F5"] = 698_456,
        ["F#5"] = 739_989,
        ["G5"] = 783_991,
        ["G#5"] = 830_609,
        ["A5"] = 880_000,
        ["A#5"] = 932_328,
        ["B5"] = 987_767,
        ["C6"] = 1_046_502,
    };

    /// <summary>Resolves the 11-bit period register value for a frequency: <c>period = 2048 - round(131072 Hz / f)</c>
    /// — pure integer math over millihertz so every build emits identical bytes.</summary>
    /// <param name="millihertz">The note frequency, in millihertz (&gt; 0).</param>
    /// <returns>The period value (0..2047).</returns>
    public static int Period(int millihertz) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: millihertz, other: 0, paramName: nameof(millihertz));

        return (2048 - (int)(((131_072_000L + (millihertz / 2)) / millihertz)));
    }

    /// <summary>Resolves a note name (e.g. <c>"C5"</c>, <c>"G#4"</c>) to its millihertz frequency.</summary>
    /// <param name="noteName">The note name (scientific pitch notation, octaves 3..6).</param>
    /// <returns>The frequency in millihertz.</returns>
    public static int MillihertzFor(string noteName) {
        ArgumentException.ThrowIfNullOrEmpty(noteName);

        return (Millihertz.TryGetValue(key: noteName, value: out var value)
            ? value
            : throw new ArgumentException(message: $"'{noteName}' is not a recognized note name (expected e.g. \"C5\", \"G#4\", octaves 3..6).", paramName: nameof(noteName)));
    }
}
