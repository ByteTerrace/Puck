namespace Puck.HumbleGamingBrick.Forge.Framework;

/// <summary>
/// The real APU sound driver: a compact SM83 register pump over the <see cref="SoundTables"/> catalog.
/// </summary>
/// <remarks>
/// Four sequencer voices tick once per frame from the main loop — pulse 1, pulse 2, wave and noise — each holding a
/// read pointer, a loop start and a wait counter in the framework's sound work-RAM block
/// (<see cref="FrameworkMemoryMap.SoundVoiceState"/>). A step is a duration byte followed by the voice's raw APU
/// register bytes; the tick writes them straight to the ports and waits the duration out. What a stream's zero
/// terminator does is the loop start's to decide: a voice carrying one rewinds to it and plays on, and a voice
/// without one stops and silences its channel. Nothing here reserves a voice for music or for effects. <see cref="ISoundDriver.EmitEffect"/> resolves an effect id to
/// its ROM stream at build time and emits a three-store trigger (pointer + zeroed wait), so triggering is
/// race-free with the tick that runs later in the same frame. The streams themselves are manifest tables: the game
/// declares them with <see cref="SoundTables.DefineIn"/> beside its other data, links, then hands the linked
/// manifest to <see cref="Bind"/> — so games consume the catalog through the table layer. Everything the driver
/// plays is deterministic work-RAM state driven by the frame counter and inputs — replay-identical like the rest
/// of the framework.
/// </remarks>
public sealed class ApuSoundDriver : ISoundDriver {
    private readonly Dictionary<byte, (SoundVoice Voice, RomTable Table)> m_effects = [];

    private RomTable? m_musicLoop;
    private int? m_tickLabel;

    /// <summary>Resolves the catalog's streams from the linked manifest (they were declared by
    /// <see cref="SoundTables.DefineIn"/>). Call once, after <see cref="GameManifest.Link"/> and before any
    /// <see cref="EmitEffect"/>.</summary>
    /// <param name="linked">The game's linked manifest.</param>
    public void Bind(LinkedManifest linked) {
        ArgumentNullException.ThrowIfNull(linked);

        foreach (var effect in SoundTables.BuildEffectCatalog()) {
            m_effects[effect.Id] = (effect.Voice, linked.Table(name: SoundTables.EffectTableName(name: effect.Name)));
        }

        m_musicLoop = linked.Table(name: SoundTables.MusicLoopTableName);
    }
    /// <inheritdoc/>
    public void EmitBoot(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        // Master power FIRST (register writes are ignored while the APU is off), then full volume both sides, every
        // channel routed left + right, and all four DACs off so boot is silence rather than a pop. The voice state in
        // work RAM is already zeroed by the kernel's boot clear.
        emitter.LoadAImmediate(value: 0x80);
        emitter.StoreAToHighPage(port: Hw.PortSoundOnOff);
        emitter.LoadAImmediate(value: 0xFF);
        emitter.StoreAToHighPage(port: Hw.PortOutputRouting);
        emitter.LoadAImmediate(value: 0x77);
        emitter.StoreAToHighPage(port: Hw.PortMasterVolume);
        emitter.XorA();
        emitter.StoreAToHighPage(port: Hw.PortPulse1Envelope);
        emitter.StoreAToHighPage(port: Hw.PortPulse2Envelope);
        emitter.StoreAToHighPage(port: Hw.PortWaveDacEnable);
        emitter.StoreAToHighPage(port: Hw.PortNoiseEnvelope);
    }
    /// <inheritdoc/>
    public void EmitFrameTick(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.Call(label: (m_tickLabel ??= emitter.NewLabel()));
    }
    /// <inheritdoc/>
    public void EmitEffect(Sm83Emitter emitter, byte effectId) {
        ArgumentNullException.ThrowIfNull(emitter);

        if (effectId == SoundTables.MusicStop) {
            EmitVoiceStop(emitter: emitter, voice: SoundVoice.Pulse2);

            return;
        }

        if (effectId == SoundTables.MusicLoop) {
            var pattern = (m_musicLoop ?? throw new InvalidOperationException(message: "The sound driver was never bound to a linked manifest (call Bind after GameManifest.Link)."));

            EmitMusicStart(emitter: emitter, stream: pattern, voice: SoundVoice.Pulse2);

            return;
        }

        if (!m_effects.TryGetValue(key: effectId, value: out var effect)) {
            throw new ArgumentException(message: $"Effect id {effectId} is not in the sound catalog (was the driver bound?).", paramName: nameof(effectId));
        }

        EmitEffectStart(emitter: emitter, stream: effect.Table, voice: effect.Voice);
    }
    /// <summary>Emits a start of the named music stream, replacing whatever the music voice was playing.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="stream">The compiled music-loop table; the voice rewinds to its start on the terminator.</param>
    /// <param name="voice">The voice the part occupies.</param>
    /// <remarks>Independent of <see cref="Bind"/>: a document compiler supplies its own tables.</remarks>
    public static void EmitMusicStart(Sm83Emitter emitter, RomTable stream, SoundVoice voice) {
        ArgumentNullException.ThrowIfNull(emitter);

        EmitVoiceStart(
            emitter: emitter,
            pointerAddress: VoicePointer(voice: voice),
            startAddress: VoiceStart(voice: voice),
            streamAddress: stream.Address,
            waitAddress: VoiceWait(voice: voice)
        );
    }

    /// <summary>Emits a start of a one-shot on the voice reserved for it, leaving the music voice untouched.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="stream">The compiled effect table; the voice stops on its terminator.</param>
    /// <param name="voice">The one-shot voice the effect plays on.</param>
    /// <remarks>Independent of <see cref="Bind"/>: a document compiler supplies its own tables.</remarks>
    public static void EmitEffectStart(Sm83Emitter emitter, RomTable stream, SoundVoice voice) {
        ArgumentNullException.ThrowIfNull(emitter);

        EmitVoiceStart(
            emitter: emitter,
            clearAddress: VoiceStart(voice: voice),
            pointerAddress: VoicePointer(voice: voice),
            startAddress: null,
            streamAddress: stream.Address,
            waitAddress: VoiceWait(voice: voice)
        );
    }

    /// <summary>Emits a copy of a waveform into the wave voice's pattern registers.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="pattern">The sixteen bytes holding thirty-two four-bit samples.</param>
    /// <remarks>The DAC must be off while the pattern is written, so this switches it off and leaves it off.</remarks>
    public static void EmitWavePatternLoad(Sm83Emitter emitter, RomTable pattern) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.XorA();
        emitter.StoreAToHighPage(port: Hw.PortWaveDacEnable);
        emitter.LoadImmediate(pair: Reg16.Hl, value: pattern.Address);
        for (var index = 0; index < 16; ++index) {
            emitter.LoadAFromHlIncrement();
            emitter.StoreAToHighPage(port: ((byte)(Hw.PortWavePattern + index)));
        }
    }

    /// <summary>Returns the base of a voice's sequencer state block.</summary>
    /// <param name="voice">The hardware voice.</param>
    /// <returns>The block's first address.</returns>
    public static ushort VoiceState(SoundVoice voice) => voice switch {
        SoundVoice.Noise => FrameworkMemoryMap.SoundNoiseState,
        SoundVoice.Wave => FrameworkMemoryMap.SoundWaveState,
        SoundVoice.Pulse2 => FrameworkMemoryMap.SoundPulse2State,
        _ => FrameworkMemoryMap.SoundPulse1State,
    };
    /// <summary>Returns the high-page port a voice's channel is silenced through.</summary>
    /// <param name="voice">The hardware voice.</param>
    /// <returns>The port.</returns>
    public static byte VoiceMutePort(SoundVoice voice) => voice switch {
        SoundVoice.Noise => Hw.PortNoiseEnvelope,
        SoundVoice.Wave => Hw.PortWaveDacEnable,
        SoundVoice.Pulse2 => Hw.PortPulse2Envelope,
        _ => Hw.PortPulse1Envelope,
    };

    private static ushort VoicePointer(SoundVoice voice) => ((ushort)(VoiceState(voice: voice) + FrameworkMemoryMap.SoundVoicePointerOffset));
    private static ushort VoiceStart(SoundVoice voice) => ((ushort)(VoiceState(voice: voice) + FrameworkMemoryMap.SoundVoiceStartOffset));
    private static ushort VoiceWait(SoundVoice voice) => ((ushort)(VoiceState(voice: voice) + FrameworkMemoryMap.SoundVoiceWaitOffset));

    /// <summary>Emits a stop of one voice and silences its channel. Safe with nothing playing.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="voice">The voice to silence.</param>
    public static void EmitVoiceStop(Sm83Emitter emitter, SoundVoice voice) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.XorA();
        emitter.StoreAToAddress(address: ((ushort)(VoicePointer(voice: voice) + 1)));
        emitter.StoreAToAddress(address: ((ushort)(VoiceStart(voice: voice) + 1)));
        emitter.StoreAToHighPage(port: VoiceMutePort(voice: voice));
    }

    /// <inheritdoc/>
    public void EmitLibrary(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.MarkLabel(label: (m_tickLabel ??= emitter.NewLabel()));

        // One section per voice, in hardware order. Nothing here reserves a voice for music or for one-shots: a
        // stream's terminator rewinds or stops depending on whether the voice carries a loop start.
        EmitVoiceTick(emitter: emitter, portBase: Hw.PortPulse1Sweep, registerCount: 5, voice: SoundVoice.Pulse1);
        EmitVoiceTick(emitter: emitter, portBase: Hw.PortNoiseLength, registerCount: 4, voice: SoundVoice.Noise);
        EmitVoiceTick(emitter: emitter, portBase: Hw.PortWaveDacEnable, registerCount: 5, voice: SoundVoice.Wave);
        EmitVoiceTick(emitter: emitter, portBase: Hw.PortPulse2DutyLength, registerCount: 4, voice: SoundVoice.Pulse2);
        emitter.Return();
    }

    // The build-time-resolved trigger: point the voice at the stream and zero its wait, so the tick later this same
    // frame plays the first step immediately. Retriggering a playing voice restarts it — the intended feel.
    private static void EmitVoiceStart(Sm83Emitter emitter, ushort pointerAddress, ushort? startAddress, ushort streamAddress, ushort waitAddress, ushort? clearAddress = null) {
        emitter.LoadAImmediate(value: ((byte)(streamAddress & 0xFF)));
        emitter.StoreAToAddress(address: pointerAddress);

        if (startAddress is { } start) {
            emitter.StoreAToAddress(address: start);
        }

        emitter.LoadAImmediate(value: ((byte)(streamAddress >> 8)));
        emitter.StoreAToAddress(address: ((ushort)(pointerAddress + 1)));

        if (startAddress is { } startHigh) {
            emitter.StoreAToAddress(address: ((ushort)(startHigh + 1)));
        }

        emitter.XorA();
        emitter.StoreAToAddress(address: waitAddress);

        if (clearAddress is { } clear) {
            // No loop start: the terminator stops this voice instead of rewinding it.
            emitter.StoreAToAddress(address: clear);
            emitter.StoreAToAddress(address: ((ushort)(clear + 1)));
        }
    }
    // One voice's per-frame sequencer section. Idle (pointer high byte zero) falls straight through; a positive wait
    // burns one frame; otherwise the next step's duration byte decides — zero is the terminator (an SFX voice stops
    // and mutes its channel; the music voice rewinds to the pattern start and plays on), anything else writes the
    // step's register bytes to the ports and waits the duration out. Clobbers A/B/C/HL — main-loop-safe.
    private static void EmitVoiceTick(Sm83Emitter emitter, byte portBase, int registerCount, SoundVoice voice) {
        var copy = emitter.NewLabel();
        var play = emitter.NewLabel();
        var sectionDone = emitter.NewLabel();
        var step = emitter.NewLabel();
        var stop = emitter.NewLabel();
        var muteEnvelopePort = VoiceMutePort(voice: voice);
        var pointerAddress = VoicePointer(voice: voice);
        var loopStartAddress = VoiceStart(voice: voice);
        var waitAddress = VoiceWait(voice: voice);
        var pointerHighAddress = ((ushort)(pointerAddress + 1));

        // Idle?
        emitter.LoadAFromAddress(address: pointerHighAddress);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        emitter.JumpAbsolute(condition: Condition.Zero, label: sectionDone);

        // Waiting out the current step?
        emitter.LoadAFromAddress(address: waitAddress);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        emitter.JumpRelative(condition: Condition.Zero, label: step);
        emitter.Decrement(register: Reg8.A);
        emitter.StoreAToAddress(address: waitAddress);
        emitter.JumpAbsolute(label: sectionDone);

        // HL := the stream pointer; A := the next step's duration byte.
        emitter.MarkLabel(label: step);
        emitter.LoadAFromAddress(address: pointerAddress);
        emitter.Load(destination: Reg8.L, source: Reg8.A);
        emitter.LoadAFromAddress(address: pointerHighAddress);
        emitter.Load(destination: Reg8.H, source: Reg8.A);
        emitter.LoadAFromHlIncrement();
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        emitter.JumpRelative(condition: Condition.NotZero, label: play);

        // The terminator. A voice carrying a loop start rewinds to it and plays its first event this same frame;
        // one without stops and turns its channel's converter off.
        emitter.LoadAFromAddress(address: ((ushort)(loopStartAddress + 1)));
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        emitter.JumpRelative(condition: Condition.Zero, label: stop);
        emitter.StoreAToAddress(address: pointerHighAddress);
        emitter.LoadAFromAddress(address: loopStartAddress);
        emitter.StoreAToAddress(address: pointerAddress);
        emitter.JumpAbsolute(label: step);
        emitter.MarkLabel(label: stop);
        emitter.XorA();
        emitter.StoreAToAddress(address: pointerHighAddress);
        emitter.StoreAToHighPage(port: muteEnvelopePort);
        emitter.JumpAbsolute(label: sectionDone);

        // Play the step: wait := duration - 1 (this frame counts), then pump the register bytes to the ports.
        emitter.MarkLabel(label: play);
        emitter.Decrement(register: Reg8.A);
        emitter.StoreAToAddress(address: waitAddress);
        emitter.LoadImmediate(destination: Reg8.C, value: portBase);
        emitter.LoadImmediate(destination: Reg8.B, value: ((byte)registerCount));
        emitter.MarkLabel(label: copy);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToHighPageC();
        emitter.Increment(register: Reg8.C);
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(condition: Condition.NotZero, label: copy);

        // Store the advanced pointer back.
        emitter.Load(destination: Reg8.A, source: Reg8.L);
        emitter.StoreAToAddress(address: pointerAddress);
        emitter.Load(destination: Reg8.A, source: Reg8.H);
        emitter.StoreAToAddress(address: pointerHighAddress);
        emitter.MarkLabel(label: sectionDone);
    }
}
