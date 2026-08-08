using Puck.Demo.Forge;
using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Tracker;

/// <summary>
/// The tracker's note-nudge vocabulary: a single linear cycle over every row value <see cref="AudioDocumentCompiler"/>
/// understands, so semitone-up/down and octave-up/down verbs are pure index arithmetic with a documented wrap. The
/// cycle order (low to high index) is <see cref="AudioRowDocument.Off"/>, then <see cref="AudioRowDocument.Hold"/>,
/// then every pitched note in <see cref="ApuNotePeriod.Millihertz"/> from <c>C3</c> to <c>C6</c> in pitch order.
/// Nudging up from the top pitch (<c>C6</c>) wraps to <c>OFF</c>; nudging down from <c>OFF</c> wraps to <c>C6</c> —
/// a single ring, so a player can reach every value from either direction without a dead end.
/// </summary>
internal static class TrackerNote {
    // The pitched half of the cycle, C3..C6 inclusive (37 notes — matches ApuNotePeriod.Millihertz's key set
    // exactly), written out explicitly rather than trusting Dictionary enumeration order.
    private static readonly string[] Pitches = [
        "C3", "C#3", "D3", "D#3", "E3", "F3", "F#3", "G3", "G#3", "A3", "A#3", "B3",
        "C4", "C#4", "D4", "D#4", "E4", "F4", "F#4", "G4", "G#4", "A4", "A#4", "B4",
        "C5", "C#5", "D5", "D#5", "E5", "F5", "F#5", "G5", "G#5", "A5", "A#5", "B5",
        "C6",
    ];

    /// <summary>The full cycle: <see cref="AudioRowDocument.Off"/>, <see cref="AudioRowDocument.Hold"/>, then every
    /// pitch in <see cref="Pitches"/> — the order <see cref="IndexOf"/>/<see cref="AtIndex"/> index against.</summary>
    private static readonly string[] Cycle = [AudioRowDocument.Off, AudioRowDocument.Hold, .. Pitches];

    /// <summary>The number of octaves the cycle spans (the octave-nudge step size in semitone-cycle positions).</summary>
    private const int SemitonesPerOctave = 12;

    /// <summary>Gets the cycle's total step count.</summary>
    public static int Count => Cycle.Length;

    /// <summary>Resolves a row note's position in the cycle (0 = <see cref="AudioRowDocument.Off"/>, 1 =
    /// <see cref="AudioRowDocument.Hold"/>, 2.. = the pitches in ascending order). An unrecognized note (should not
    /// occur after <see cref="AudioDocumentStore"/> normalization) falls back to <see cref="AudioRowDocument.Hold"/>'s
    /// index.</summary>
    /// <param name="note">The row's note text.</param>
    /// <returns>The cycle index.</returns>
    public static int IndexOf(string note) {
        for (var index = 0; (index < Cycle.Length); index++) {
            if (string.Equals(a: Cycle[index], b: note, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return index;
            }
        }

        return 1; // Hold — the safe fallback for a value the cycle doesn't recognize.
    }

    /// <summary>Resolves a cycle index (wrapped) back to its note text.</summary>
    /// <param name="index">The cycle index (any integer — wrapped into range).</param>
    /// <returns>The note text at that (wrapped) position.</returns>
    public static string AtIndex(int index) {
        var wrapped = (((index % Cycle.Length) + Cycle.Length) % Cycle.Length);

        return Cycle[wrapped];
    }

    /// <summary>Nudges a row note by one semitone step (wrapping ring-style — see the class remarks).</summary>
    /// <param name="note">The current note text.</param>
    /// <param name="direction">+1 for up, -1 for down.</param>
    /// <returns>The nudged note text.</returns>
    public static string Nudge(string note, int direction) =>
        AtIndex(index: (IndexOf(note: note) + Math.Sign(value: direction)));

    /// <summary>Nudges a row note by one octave (12 cycle steps), clamped so it lands on <c>OFF</c>/<c>---</c> only
    /// when it started there — an octave nudge off a real pitch always lands on another real pitch (clamped to the
    /// C3..C6 span rather than wrapping into OFF/hold, which would surprise a player mid-edit).</summary>
    /// <param name="note">The current note text.</param>
    /// <param name="direction">+1 for up, -1 for down.</param>
    /// <returns>The nudged note text.</returns>
    public static string NudgeOctave(string note, int direction) {
        var index = IndexOf(note: note);

        if (index < 2) {
            // OFF/hold have no octave — an octave nudge from either just steps to the nearest end of the pitch range.
            return ((direction > 0) ? Pitches[0] : Pitches[^1]);
        }

        var pitchIndex = (index - 2);
        var nudged = Math.Clamp(value: (pitchIndex + (Math.Sign(value: direction) * SemitonesPerOctave)), max: (Pitches.Length - 1), min: 0);

        return Pitches[nudged];
    }
}
