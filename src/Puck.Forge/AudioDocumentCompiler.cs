using Puck.Authoring;
using Puck.Forge.Framework;

namespace Puck.Forge;

/// <summary>
/// Compiles a normalized <see cref="AudioDocument"/> to the exact ROM sound-table stream formats
/// <see cref="ApuSoundDriver"/> already plays: the pulse-2 music loop (NR21 duty/length, NR22 envelope, NR23/NR24
/// period + trigger per step) and pulse-1/noise effect streams (NR10-NR14 / NR41-NR44). Every step resolves through
/// <see cref="ApuNotePeriod"/>'s integer millihertz math, so the same document compiles to byte-identical streams on
/// every run — no wall-clock, no RNG, no floats anywhere in the compile path. The output streams are ordinary bytes a
/// <see cref="Framework.GameManifest"/> declares with <c>DefineTable</c>, exactly like <see cref="SoundTables"/>'s own
/// hand-authored catalog.
/// </summary>
public static class AudioDocumentCompiler {
    // Duty bits (NR11/NR21 bits 7-6) — the same four-value hardware encoding SoundTables uses.
    private static readonly byte[] DutyBits = [0x00, 0x40, 0x80, 0xC0];

    // The envelope byte SoundTables' own patterns favor when a row doesn't specify one: a modest decay so a plain
    // authored loop is audible without per-row tuning.
    private const int DefaultPulseEnvelope = 0x63;
    private const int DefaultNoiseEnvelope = 0x51;
    private const int DefaultNoisePolynomial = 0x52;

    /// <summary>Compiles the document's pattern <see cref="AudioDocument.Order"/> into the pulse-2 music loop stream:
    /// each row becomes one step (a note triggers NR23/NR24 with the resolved period; a hold repeats the previous
    /// step's registers with no new trigger — realized as a rest with the prior duty/envelope so the DAC stays as it
    /// was; "OFF" silences the voice), and the stream ends with the terminator byte the driver rewinds its loop on.
    /// </summary>
    /// <param name="document">The normalized document (see <see cref="AudioDocumentStore.Load"/>).</param>
    /// <returns>The music-loop stream bytes, <see cref="SoundTables.MusicLoopTableName"/>'s exact grammar.</returns>
    public static byte[] CompileMusicLoop(AudioDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var stream = new List<byte>();
        var patterns = (document.Patterns ?? throw new ArgumentException(message: "The document has no patterns (was it normalized by AudioDocumentStore.Load?).", paramName: nameof(document)));
        var order = (document.Order ?? throw new ArgumentException(message: "The document has no play order (was it normalized?).", paramName: nameof(document)));
        var frames = (byte)Math.Clamp(value: (document.Tempo ?? AudioDocument.DefaultTempo), max: 255, min: 1);
        var lastDuty = DutyBits[2];
        var lastEnvelope = (byte)DefaultPulseEnvelope;

        foreach (var patternIndex in order) {
            foreach (var row in patterns[patternIndex]) {
                AppendMusicRow(frames: frames, lastDuty: ref lastDuty, lastEnvelope: ref lastEnvelope, row: row, stream: stream);
            }
        }

        stream.Add(item: 0x00); // The loop terminator: the driver rewinds to the pattern start.

        return [.. stream];
    }

    /// <summary>Compiles a named effect's rows into a pulse-1 or noise stream (its <see cref="AudioEffectDocument.Voice"/>
    /// selects the register layout), terminated with the effect terminator (mutes the channel).</summary>
    /// <param name="effect">The normalized effect (see <see cref="AudioDocumentStore.Load"/>).</param>
    /// <param name="frames">Frames per row (the document's <see cref="AudioDocument.Tempo"/>).</param>
    /// <returns>The effect stream bytes.</returns>
    public static byte[] CompileEffect(AudioEffectDocument effect, int frames) {
        ArgumentNullException.ThrowIfNull(effect);

        var stream = new List<byte>();
        var isNoise = string.Equals(a: effect.Voice, b: AudioEffectDocument.VoiceNoise, comparisonType: StringComparison.OrdinalIgnoreCase);
        var stepFrames = (byte)Math.Clamp(value: frames, max: 255, min: 1);

        foreach (var row in effect.Rows) {
            if (isNoise) {
                AppendNoiseRow(frames: stepFrames, row: row, stream: stream);
            } else {
                AppendPulseRow(frames: stepFrames, row: row, stream: stream);
            }
        }

        stream.Add(item: 0x00); // The effect terminator: the driver stops the voice and mutes its channel.

        return [.. stream];
    }

    // One music row: a note triggers NR21-NR24 fresh; a hold repeats the last step's duty/envelope with no trigger
    // bit (the previous envelope keeps decaying — exactly what the driver's per-frame register pump already does
    // when the wait counter is still counting down, but an authored hold spans a WHOLE new step so it re-writes the
    // same registers without the trigger, which restarts the envelope while intentionally not re-picking the pitch);
    // "OFF" zeroes the envelope (DAC off) with no trigger.
    private static void AppendMusicRow(byte frames, ref byte lastDuty, ref byte lastEnvelope, AudioRowDocument row, List<byte> stream) {
        if (string.Equals(a: row.Note, b: AudioRowDocument.Off, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            stream.Add(item: frames);
            stream.Add(item: lastDuty);
            stream.Add(item: 0x00);
            stream.Add(item: 0x00);
            stream.Add(item: 0x00);

            return;
        }

        if (string.Equals(a: row.Note, b: AudioRowDocument.Hold, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            stream.Add(item: frames);
            stream.Add(item: lastDuty);
            stream.Add(item: lastEnvelope);
            stream.Add(item: 0x00);
            stream.Add(item: 0x00);

            return;
        }

        var duty = DutyBits[Math.Clamp(value: (row.Duty ?? 2), max: 3, min: 0)];
        var envelope = (byte)(row.Envelope ?? DefaultPulseEnvelope);
        var period = ApuNotePeriod.Period(millihertz: ApuNotePeriod.MillihertzFor(noteName: row.Note));

        lastDuty = duty;
        lastEnvelope = envelope;

        stream.Add(item: frames);
        stream.Add(item: duty);
        stream.Add(item: envelope);
        stream.Add(item: (byte)(period & 0xFF));
        stream.Add(item: (byte)(0x80 | (period >> 8)));
    }

    // One pulse-1 effect row (NR10 sweep, NR11 duty/length, NR12 envelope, NR13/NR14 period+trigger). Sweep is
    // always 0 — the document has no sweep field (a later schema bump can add one without breaking this compiler).
    private static void AppendPulseRow(byte frames, AudioRowDocument row, List<byte> stream) {
        if (string.Equals(a: row.Note, b: AudioRowDocument.Off, comparisonType: StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a: row.Note, b: AudioRowDocument.Hold, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            stream.Add(item: frames);
            stream.Add(item: 0x00);
            stream.Add(item: DutyBits[Math.Clamp(value: (row.Duty ?? 2), max: 3, min: 0)]);
            stream.Add(item: 0x00);
            stream.Add(item: 0x00);
            stream.Add(item: 0x00);

            return;
        }

        var duty = DutyBits[Math.Clamp(value: (row.Duty ?? 2), max: 3, min: 0)];
        var envelope = (byte)(row.Envelope ?? DefaultPulseEnvelope);
        var period = ApuNotePeriod.Period(millihertz: ApuNotePeriod.MillihertzFor(noteName: row.Note));

        stream.Add(item: frames);
        stream.Add(item: 0x00);
        stream.Add(item: duty);
        stream.Add(item: envelope);
        stream.Add(item: (byte)(period & 0xFF));
        stream.Add(item: (byte)(0x80 | (period >> 8)));
    }

    // One noise effect row (NR41 length, NR42 envelope, NR43 polynomial, NR44 control+trigger). The document has no
    // dedicated polynomial field, so the row's envelope byte (when present) doubles as the polynomial selector — a
    // deliberately narrow surface until a later schema bump adds one; a hold/off row mutes the channel.
    private static void AppendNoiseRow(byte frames, AudioRowDocument row, List<byte> stream) {
        if (string.Equals(a: row.Note, b: AudioRowDocument.Off, comparisonType: StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a: row.Note, b: AudioRowDocument.Hold, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            stream.Add(item: frames);
            stream.Add(item: 0x00);
            stream.Add(item: 0x00);
            stream.Add(item: 0x00);

            return;
        }

        var envelope = (byte)(row.Envelope ?? DefaultNoiseEnvelope);
        var polynomial = (byte)DefaultNoisePolynomial;

        stream.Add(item: frames);
        stream.Add(item: envelope);
        stream.Add(item: polynomial);
        stream.Add(item: 0x80);
    }
}
