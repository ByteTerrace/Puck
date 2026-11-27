using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;

namespace Puck.HumbleGamingDeck.Forge;

/// <summary>
/// Compiles a cartridge's sounds into the register streams the Deck's in-image driver pumps, one stream per voice. The
/// four voices map onto the APU's tone channels: pulse1 and pulse2 onto the two pulse channels, wave onto the triangle,
/// noise onto the noise channel (https://www.nesdev.org/wiki/APU).
/// </summary>
/// <remarks>
/// <para>A stream is a run of five-byte steps, <c>frames, r0, r2, r3, trigger</c>, ended by a zero frame count. A step
/// writes the channel's first register, and when <c>trigger</c> is set also its period low and high registers, the last
/// of which restarts the channel; it then holds for <c>frames</c> frames.</para>
/// <para>The document's envelope byte is the Brick's: initial volume in the high nibble, direction in bit 3, a step
/// period in the low three bits. The Deck's channels play at a constant volume here, and the envelope is unrolled at
/// build time into steps that each rewrite the volume, so the driver stays a register pump and the same document decays
/// alike on every machine. A Brick envelope steps every <c>period/64</c> second, which is the nearest whole frame count
/// at sixty frames a second.</para>
/// </remarks>
public static class DeckSoundCompiler {
    /// <summary>The CPU clock the APU's timers divide, in hertz (NTSC: the 21.477272 MHz master clock over twelve).</summary>
    public const long CpuClockHertz = 1_789_773L;

    private const int DefaultNoiseEnvelope = 0x51;
    private const int DefaultPulseEnvelope = 0x63;
    // r3's length-counter load: any nonzero index, since every channel here halts its length counter.
    private const int LengthLoad = 0x08;

    /// <summary>Returns the APU channel a voice name plays on: 0 and 1 the pulses, 2 the triangle, 3 the noise.</summary>
    /// <param name="voice">The authored voice name, or null for pulse1.</param>
    /// <returns>The channel index, which is also its register block's offset from $4000 divided by four.</returns>
    public static int Channel(string? voice) => voice switch {
        AudioEffectDocument.VoicePulse2 => 1,
        AudioEffectDocument.VoiceWave => 2,
        AudioEffectDocument.VoiceNoise => 3,
        _ => 0,
    };
    /// <summary>Returns the first-register value that silences a channel without retriggering it.</summary>
    /// <param name="channel">The channel index from <see cref="Channel"/>.</param>
    /// <returns>Constant volume zero for a pulse or the noise; a zero linear-counter reload for the triangle.</returns>
    public static byte Silence(int channel) => ((channel == 2) ? (byte)0x80 : (byte)0x30);
    /// <summary>Compiles one voice's part of a music track: the document's play order, where a hold sustains the previous
    /// note's pitch without retriggering it, ending with the terminator the driver rewinds on.</summary>
    /// <param name="document">The normalized part document.</param>
    /// <param name="voice">The voice the part plays on.</param>
    /// <returns>The stream bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The document is not normalized, or a row names an unknown note.</exception>
    public static byte[] CompileMusicVoice(AudioDocument document, string voice) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var (frames, rows) = AudioDocumentCompiler.PlayOrder(document: document);
        var stream = new List<byte>();
        var channel = Channel(voice: voice);
        AudioRowDocument? sounding = null;

        foreach (var row in rows) {
            if (AudioDocumentCompiler.IsOff(row: row) || (AudioDocumentCompiler.IsHold(row: row) && (sounding is null))) {
                sounding = null;
                Rest(channel: channel, frames: frames, stream: stream);
            } else if (AudioDocumentCompiler.IsHold(row: row)) {
                Note(channel: channel, frames: frames, row: sounding!, stream: stream, trigger: false);
            } else {
                sounding = row;
                Note(channel: channel, frames: frames, row: row, stream: stream, trigger: true);
            }
        }
        stream.Add(item: 0);

        return [.. stream];
    }
    /// <summary>Compiles a one-shot effect: each row a note, a hold and a cut both silence, ending with the terminator
    /// that stops the voice.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="frames">The frames each row holds, 1 through 255.</param>
    /// <returns>The stream bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A row names an unknown note.</exception>
    public static byte[] CompileEffect(AudioEffectDocument effect, int frames) {
        ArgumentNullException.ThrowIfNull(argument: effect);

        var step = Math.Clamp(value: frames, min: 1, max: 255);
        var stream = new List<byte>();
        var channel = Channel(voice: effect.Voice);

        foreach (var row in effect.Rows) {
            if (AudioDocumentCompiler.IsOff(row: row) || AudioDocumentCompiler.IsHold(row: row)) {
                Rest(channel: channel, frames: step, stream: stream);
            } else {
                Note(channel: channel, frames: step, row: row, stream: stream, trigger: true);
            }
        }
        stream.Add(item: 0);

        return [.. stream];
    }

    private static void Rest(int channel, int frames, List<byte> stream) =>
        stream.AddRange(collection: [((byte)frames), Silence(channel: channel), 0, 0, 0]);
    // One sounding row: its pitch on the first step, then a step each time the unrolled envelope changes the volume.
    private static void Note(int channel, int frames, AudioRowDocument row, List<byte> stream, bool trigger) {
        var millihertz = ApuNotePeriod.MillihertzFor(noteName: row.Note);
        int r2;
        int r3;

        if (channel == 3) {
            // A higher note picks a shorter noise period: the table's 37 notes spread across the sixteen periods.
            var rank = ApuNotePeriod.Millihertz.Values.Count(predicate: value => (value < millihertz));

            r2 = (15 - ((rank * 15) / (ApuNotePeriod.Millihertz.Count - 1)));
            r3 = LengthLoad;
        } else {
            var period = ApuNotePeriod.DividedPeriod(millihertz: millihertz, clockHertz: CpuClockHertz, steps: ((channel == 2) ? 32 : 16));

            r2 = (period & 0xFF);
            r3 = (LengthLoad | ((period >> 8) & 0x07));
        }

        if (channel == 2) {
            // The triangle has no volume: the Brick's wave volume field (bits 0-1, zero muting) gates it on or off.
            var on = (((row.Envelope ?? 1) & 0x03) != 0);

            stream.AddRange(collection: [((byte)frames), (on ? (byte)0xFF : (byte)0x80), ((byte)r2), ((byte)r3), (trigger ? (byte)1 : (byte)0)]);

            return;
        }

        var envelope = (row.Envelope ?? ((channel == 3) ? DefaultNoiseEnvelope : DefaultPulseEnvelope));
        var volume = ((envelope >> 4) & 0x0F);
        var rising = ((envelope & 0x08) != 0);
        var interval = (((envelope & 0x07) == 0) ? 0 : Math.Max(val1: 1, val2: ((((envelope & 0x07) * 60) + 32) / 64)));
        var duty = ((channel == 3) ? 0 : ((Math.Clamp(value: (row.Duty ?? 2), min: 0, max: 3)) << 6));
        var left = frames;
        var first = true;

        while (left > 0) {
            var settled = ((interval == 0) || (rising ? (volume == 15) : (volume == 0)));
            var span = (settled ? left : Math.Min(val1: interval, val2: left));

            stream.AddRange(collection: [((byte)span), ((byte)(duty | 0x30 | volume)), ((byte)r2), ((byte)r3), ((first && trigger) ? (byte)1 : (byte)0)]);
            first = false;
            left -= span;
            if (!settled) {
                volume += (rising ? 1 : -1);
            }
        }
    }
}
