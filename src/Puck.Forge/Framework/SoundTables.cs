namespace Puck.Forge.Framework;

/// <summary>The hardware voice a catalog effect plays on: <see cref="Pulse"/> = pulse channel 1 (NR10–NR14, five
/// register bytes per step), <see cref="Noise"/> = the noise channel (NR41–NR44, four register bytes per step).
/// Music always plays on pulse channel 2, so effects never fight the loop for a channel.</summary>
internal enum SoundVoice {
    /// <summary>Pulse channel 1 (melodic effects, sweeps, jingles).</summary>
    Pulse = 0,
    /// <summary>The noise channel (ticks, thuds, riffles).</summary>
    Noise = 1,
}

/// <summary>One catalog sound effect: its trigger id, diagnostic block name, hardware voice, and step stream.</summary>
/// <param name="Id">The effect id a game passes to <see cref="ISoundDriver.EmitEffect"/>.</param>
/// <param name="Name">The diagnostic name (also the ROM data block's name suffix).</param>
/// <param name="Voice">The hardware voice the stream's register bytes target.</param>
/// <param name="Stream">The step stream (see <see cref="SoundTables"/> for the grammar).</param>
internal readonly record struct SoundEffect(byte Id, string Name, SoundVoice Voice, byte[] Stream);

/// <summary>
/// The framework's curated sound catalog — every table <see cref="ApuSoundDriver"/> links into a cartridge, declared
/// the same way a framework game declares its own data tables. The streams share one grammar the driver's
/// per-frame sequencer consumes: each STEP is a duration byte (frames the step occupies, ≥ 1) followed by the
/// voice's raw APU register bytes (pulse: NR10–NR14, noise: NR41–NR44, music: NR21–NR24), and a zero duration byte
/// terminates the stream — an effect voice falls silent (envelope zeroed, DAC off), the music sequencer loops back
/// to the pattern's start. Register values are resolved at BUILD time (note periods from integer millihertz math),
/// so a stream is plain bytes and the in-ROM driver stays a dumb register pump. The effect ids are the REUSABLE
/// surface: <see cref="EffectDeal"/>/<see cref="EffectFlip"/>/<see cref="EffectShuffle"/>/<see cref="EffectWin"/>
/// and the rest, so games consume the catalog through the table layer.
/// </summary>
internal static class SoundTables {
    /// <summary>A soft two-hiss noise tick — a card sliding off the deck (also any small "object placed" moment).</summary>
    public const byte EffectDeal = 0;
    /// <summary>A quick two-note upward pulse blip — a card flipping face-up (also a "confirm/start" chirp).</summary>
    public const byte EffectFlip = 1;
    /// <summary>A six-burst noise riffle — a deck shuffling.</summary>
    public const byte EffectShuffle = 2;
    /// <summary>The ascending four-note win jingle (C5–E5–G5–C6).</summary>
    public const byte EffectWin = 3;
    /// <summary>A tiny single pulse tick — cursor/menu movement.</summary>
    public const byte EffectCursor = 4;
    /// <summary>A low noise thump — a piece locking, a card stack landing.</summary>
    public const byte EffectThud = 5;
    /// <summary>A rising hardware-sweep zip — a line clear, a power-up.</summary>
    public const byte EffectSweep = 6;
    /// <summary>A descending three-note run — game over.</summary>
    public const byte EffectOver = 7;

    /// <summary>The music-control id that starts the framework's short loop on pulse 2 (restarts from the top when
    /// already playing).</summary>
    public const byte MusicLoop = 0x80;
    /// <summary>The music-control id that stops whatever loop is playing and silences pulse 2.</summary>
    public const byte MusicStop = 0xFF;

    /// <summary>The manifest table name of the short loop's pattern stream.</summary>
    public const string MusicLoopTableName = "sound-music-loop";

    /// <summary>The manifest table name of a catalog effect's stream.</summary>
    /// <param name="name">The effect's catalog name (<see cref="SoundEffect.Name"/>).</param>
    /// <returns>The table name.</returns>
    public static string EffectTableName(string name) => $"sound-effect-{name}";

    /// <summary>Declares the whole catalog into a game's manifest (one table per effect stream + the loop pattern) —
    /// the one line that gives any framework game the shared sound set. Pair with
    /// <see cref="ApuSoundDriver.Bind"/> after the manifest links.</summary>
    /// <param name="manifest">The game's manifest.</param>
    /// <param name="musicLoop">The pulse-2 music loop stream to declare instead of the hand-authored
    /// <see cref="BuildMusicLoop"/> (null = the stock loop). A game whose music comes from a <c>puck.audio.v1</c>
    /// document (compiled with <c>AudioDocumentCompiler.CompileMusicLoop</c>) passes its bytes here so it still
    /// declares under <see cref="MusicLoopTableName"/> and rides the same driver/manifest plumbing.</param>
    public static void DefineIn(GameManifest manifest, byte[]? musicLoop = null) {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var effect in BuildEffectCatalog()) {
            manifest.DefineTable(name: EffectTableName(name: effect.Name), bytes: effect.Stream);
        }

        manifest.DefineTable(name: MusicLoopTableName, bytes: (musicLoop ?? BuildMusicLoop()));
    }

    // Note frequencies in millihertz (equal temperament, A4 = 440 Hz) — integers so the period math is exact and the
    // built streams are byte-identical across runs.
    private const int NoteG3 = 195_998;
    private const int NoteA3 = 220_000;
    private const int NoteA4 = 440_000;
    private const int NoteA5 = 880_000;
    private const int NoteB3 = 246_942;
    private const int NoteB4 = 493_883;
    private const int NoteC4 = 261_626;
    private const int NoteC5 = 523_251;
    private const int NoteC6 = 1_046_502;
    private const int NoteD4 = 293_665;
    private const int NoteD5 = 587_330;
    private const int NoteE4 = 329_628;
    private const int NoteE5 = 659_255;
    private const int NoteG4 = 391_995;
    private const int NoteG5 = 783_991;

    // Duty bits (NR11/NR21 bits 7-6).
    private const byte DutyQuarter = 0x40;
    private const byte DutyHalf = 0x80;
    private const byte DutyThreeQuarters = 0xC0;

    /// <summary>Builds the curated effect catalog (ids, voices, and streams).</summary>
    /// <returns>The eight effects, in id order.</returns>
    public static SoundEffect[] BuildEffectCatalog() => [
        new SoundEffect(Id: EffectDeal, Name: "deal", Voice: SoundVoice.Noise, Stream: BuildStream(
            NoiseStep(frames: 2, envelope: 0x51, polynomial: 0x40),
            NoiseStep(frames: 3, envelope: 0x31, polynomial: 0x51)
        )),
        new SoundEffect(Id: EffectFlip, Name: "flip", Voice: SoundVoice.Pulse, Stream: BuildStream(
            PulseStep(frames: 2, sweep: 0x00, duty: DutyHalf, envelope: 0x91, millihertz: NoteE5),
            PulseStep(frames: 4, sweep: 0x00, duty: DutyHalf, envelope: 0x71, millihertz: NoteG5)
        )),
        new SoundEffect(Id: EffectShuffle, Name: "shuffle", Voice: SoundVoice.Noise, Stream: BuildStream(
            NoiseStep(frames: 2, envelope: 0x51, polynomial: 0x46),
            NoiseStep(frames: 2, envelope: 0x41, polynomial: 0x52),
            NoiseStep(frames: 2, envelope: 0x51, polynomial: 0x46),
            NoiseStep(frames: 2, envelope: 0x41, polynomial: 0x52),
            NoiseStep(frames: 2, envelope: 0x31, polynomial: 0x5A),
            NoiseStep(frames: 4, envelope: 0x21, polynomial: 0x62)
        )),
        new SoundEffect(Id: EffectWin, Name: "win", Voice: SoundVoice.Pulse, Stream: BuildStream(
            PulseStep(frames: 5, sweep: 0x00, duty: DutyHalf, envelope: 0xA1, millihertz: NoteC5),
            PulseStep(frames: 5, sweep: 0x00, duty: DutyHalf, envelope: 0xA1, millihertz: NoteE5),
            PulseStep(frames: 5, sweep: 0x00, duty: DutyHalf, envelope: 0xA1, millihertz: NoteG5),
            PulseStep(frames: 14, sweep: 0x00, duty: DutyHalf, envelope: 0xA3, millihertz: NoteC6)
        )),
        new SoundEffect(Id: EffectCursor, Name: "cursor", Voice: SoundVoice.Pulse, Stream: BuildStream(
            PulseStep(frames: 2, sweep: 0x00, duty: DutyQuarter, envelope: 0x71, millihertz: NoteA5)
        )),
        new SoundEffect(Id: EffectThud, Name: "thud", Voice: SoundVoice.Noise, Stream: BuildStream(
            NoiseStep(frames: 3, envelope: 0x71, polynomial: 0x74),
            NoiseStep(frames: 3, envelope: 0x31, polynomial: 0x76)
        )),
        new SoundEffect(Id: EffectSweep, Name: "sweep", Voice: SoundVoice.Pulse, Stream: BuildStream(
            PulseStep(frames: 14, sweep: 0x13, duty: DutyHalf, envelope: 0xA2, millihertz: NoteC5)
        )),
        new SoundEffect(Id: EffectOver, Name: "over", Voice: SoundVoice.Pulse, Stream: BuildStream(
            PulseStep(frames: 6, sweep: 0x00, duty: DutyThreeQuarters, envelope: 0x91, millihertz: NoteG4),
            PulseStep(frames: 6, sweep: 0x00, duty: DutyThreeQuarters, envelope: 0x91, millihertz: NoteE4),
            PulseStep(frames: 16, sweep: 0x00, duty: DutyThreeQuarters, envelope: 0x93, millihertz: NoteC4)
        )),
    ];

    /// <summary>Builds the framework's short loop (pulse 2): a four-bar C–G/B–Am–G arpeggio walk, eight frames per
    /// eighth note (~4.3 s per pass at 60 fps), ending on a one-note rest so the loop breathes before restarting.</summary>
    /// <returns>The music pattern stream.</returns>
    public static byte[] BuildMusicLoop() {
        int[] bars = [
            NoteC4, NoteE4, NoteG4, NoteC5, NoteG4, NoteE4, NoteC4, NoteE4,
            NoteB3, NoteD4, NoteG4, NoteB4, NoteG4, NoteD4, NoteB3, NoteD4,
            NoteA3, NoteC4, NoteE4, NoteA4, NoteE4, NoteC4, NoteA3, NoteC4,
            NoteG3, NoteB3, NoteD4, NoteG4, NoteD4, NoteB3, NoteG3,
        ];
        var stream = new List<byte>();

        foreach (var note in bars) {
            stream.AddRange(collection: MusicNote(frames: 8, duty: DutyQuarter, envelope: 0x63, millihertz: note));
        }

        stream.AddRange(collection: MusicRest(frames: 8));
        stream.Add(item: 0x00); // Loop terminator: the sequencer rewinds to the pattern's start.

        return [.. stream];
    }

    // Concatenates steps and appends the effect terminator (duration 0 → the voice stops and mutes its channel).
    private static byte[] BuildStream(params byte[][] steps) {
        var stream = new List<byte>();

        foreach (var step in steps) {
            stream.AddRange(collection: step);
        }

        stream.Add(item: 0x00);

        return [.. stream];
    }

    // One pulse-1 step: duration + the five NR10-NR14 register bytes (NR14 carries the trigger bit).
    private static byte[] PulseStep(byte frames, byte sweep, byte duty, byte envelope, int millihertz) {
        var period = Period(millihertz: millihertz);

        return [frames, sweep, duty, envelope, (byte)(period & 0xFF), (byte)(0x80 | (period >> 8))];
    }

    // One noise step: duration + the four NR41-NR44 register bytes (NR44 carries the trigger bit).
    private static byte[] NoiseStep(byte frames, byte envelope, byte polynomial) =>
        [frames, 0x00, envelope, polynomial, 0x80];

    // One music event: duration + the four NR21-NR24 register bytes (NR24 carries the trigger bit).
    private static byte[] MusicNote(byte frames, byte duty, byte envelope, int millihertz) {
        var period = Period(millihertz: millihertz);

        return [frames, duty, envelope, (byte)(period & 0xFF), (byte)(0x80 | (period >> 8))];
    }

    // A music rest: envelope 0 turns pulse 2's DAC off (flat silence), and no trigger bit is set.
    private static byte[] MusicRest(byte frames) =>
        [frames, DutyQuarter, 0x00, 0x00, 0x00];

    // The 11-bit period register value for a frequency — delegates to ApuNotePeriod, the shared integer-math
    // implementation, so AudioDocumentCompiler shares the exact same integer math.
    private static int Period(int millihertz) => ApuNotePeriod.Period(millihertz: millihertz);
}
