namespace Puck.Forge.Framework;

/// <summary>
/// The real APU sound driver: a compact SM83 register pump over the <see cref="SoundTables"/> catalog. Three
/// independent sequencer voices tick once per frame from the main loop — a pulse-1 SFX voice, a noise SFX voice, and
/// a pulse-2 music voice — each holding a read pointer + wait counter in the framework's sound work-RAM block
/// (<see cref="FrameworkMemoryMap.SoundMusicPointer"/>…). A step is a duration byte followed by the voice's raw APU
/// register bytes; the tick writes them straight to the ports and waits the duration out, an effect stream's zero
/// terminator silences its channel (envelope zero, DAC off), and the music pattern's terminator rewinds to the
/// pattern start — the short loop. <see cref="ISoundDriver.EmitEffect"/> resolves an effect id to its ROM stream at
/// BUILD time and emits a three-store trigger (pointer + zeroed wait), so triggering is race-free with the tick that
/// runs later in the same frame. The streams themselves are MANIFEST tables: the game declares them with
/// <see cref="SoundTables.DefineIn"/> beside its other data, links, then hands the linked manifest to
/// <see cref="Bind"/> — so games consume the catalog through the table layer. Everything the driver plays is
/// deterministic work-RAM state driven by the frame counter and inputs
/// — replay-identical like the rest of the framework.
/// </summary>
internal sealed class ApuSoundDriver : ISoundDriver {
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
            // Stop the loop and silence pulse 2 (envelope zero = DAC off): idempotent, safe with no music playing.
            emitter.XorA();
            emitter.StoreAToAddress(address: FrameworkMemoryMap.SoundMusicPointerHigh);
            emitter.StoreAToHighPage(port: Hw.PortPulse2Envelope);

            return;
        }

        if (effectId == SoundTables.MusicLoop) {
            var pattern = (m_musicLoop ?? throw new InvalidOperationException(message: "The sound driver was never bound to a linked manifest (call Bind after GameManifest.Link)."));

            EmitVoiceStart(
                emitter: emitter,
                pointerAddress: FrameworkMemoryMap.SoundMusicPointer,
                startAddress: FrameworkMemoryMap.SoundMusicStart,
                streamAddress: pattern.Address,
                waitAddress: FrameworkMemoryMap.SoundMusicWait
            );

            return;
        }

        if (!m_effects.TryGetValue(key: effectId, value: out var effect)) {
            throw new ArgumentException(message: $"Effect id {effectId} is not in the sound catalog (was the driver bound?).", paramName: nameof(effectId));
        }

        var isPulse = (effect.Voice == SoundVoice.Pulse);

        EmitVoiceStart(
            emitter: emitter,
            pointerAddress: (isPulse ? FrameworkMemoryMap.SoundPulsePointer : FrameworkMemoryMap.SoundNoisePointer),
            startAddress: null,
            streamAddress: effect.Table.Address,
            waitAddress: (isPulse ? FrameworkMemoryMap.SoundPulseWait : FrameworkMemoryMap.SoundNoiseWait)
        );
    }

    /// <inheritdoc/>
    public void EmitLibrary(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.MarkLabel(label: (m_tickLabel ??= emitter.NewLabel()));

        // The pulse-1 SFX voice (five registers from NR10), the noise SFX voice (four from NR41), then the music
        // voice (four from NR21, looping on its terminator).
        EmitVoiceTick(
            emitter: emitter,
            loopStartAddress: null,
            muteEnvelopePort: Hw.PortPulse1Envelope,
            pointerAddress: FrameworkMemoryMap.SoundPulsePointer,
            portBase: Hw.PortPulse1Sweep,
            registerCount: 5,
            waitAddress: FrameworkMemoryMap.SoundPulseWait
        );
        EmitVoiceTick(
            emitter: emitter,
            loopStartAddress: null,
            muteEnvelopePort: Hw.PortNoiseEnvelope,
            pointerAddress: FrameworkMemoryMap.SoundNoisePointer,
            portBase: Hw.PortNoiseLength,
            registerCount: 4,
            waitAddress: FrameworkMemoryMap.SoundNoiseWait
        );
        EmitVoiceTick(
            emitter: emitter,
            loopStartAddress: FrameworkMemoryMap.SoundMusicStart,
            muteEnvelopePort: Hw.PortPulse2Envelope,
            pointerAddress: FrameworkMemoryMap.SoundMusicPointer,
            portBase: Hw.PortPulse2DutyLength,
            registerCount: 4,
            waitAddress: FrameworkMemoryMap.SoundMusicWait
        );
        emitter.Return();
    }

    // The build-time-resolved trigger: point the voice at the stream and zero its wait, so the tick later this same
    // frame plays the first step immediately. Retriggering a playing voice restarts it — the intended feel.
    private static void EmitVoiceStart(Sm83Emitter emitter, ushort pointerAddress, ushort? startAddress, ushort streamAddress, ushort waitAddress) {
        emitter.LoadAImmediate(value: (byte)(streamAddress & 0xFF));
        emitter.StoreAToAddress(address: pointerAddress);

        if (startAddress is { } start) {
            emitter.StoreAToAddress(address: start);
        }

        emitter.LoadAImmediate(value: (byte)(streamAddress >> 8));
        emitter.StoreAToAddress(address: (ushort)(pointerAddress + 1));

        if (startAddress is { } startHigh) {
            emitter.StoreAToAddress(address: (ushort)(startHigh + 1));
        }

        emitter.XorA();
        emitter.StoreAToAddress(address: waitAddress);
    }

    // One voice's per-frame sequencer section. Idle (pointer high byte zero) falls straight through; a positive wait
    // burns one frame; otherwise the next step's duration byte decides — zero is the terminator (an SFX voice stops
    // and mutes its channel; the music voice rewinds to the pattern start and plays on), anything else writes the
    // step's register bytes to the ports and waits the duration out. Clobbers A/B/C/HL — main-loop-safe.
    private static void EmitVoiceTick(Sm83Emitter emitter, ushort? loopStartAddress, byte muteEnvelopePort, ushort pointerAddress, byte portBase, int registerCount, ushort waitAddress) {
        var copy = emitter.NewLabel();
        var play = emitter.NewLabel();
        var sectionDone = emitter.NewLabel();
        var step = emitter.NewLabel();
        var pointerHighAddress = (ushort)(pointerAddress + 1);

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

        if (loopStartAddress is { } loopStart) {
            // The music terminator: rewind to the pattern start and play its first event this same frame.
            emitter.LoadAFromAddress(address: loopStart);
            emitter.StoreAToAddress(address: pointerAddress);
            emitter.LoadAFromAddress(address: (ushort)(loopStart + 1));
            emitter.StoreAToAddress(address: pointerHighAddress);
            emitter.JumpAbsolute(label: step);
        } else {
            // The effect terminator: stop the voice (A is zero here) and turn the channel's DAC off.
            emitter.StoreAToAddress(address: pointerHighAddress);
            emitter.StoreAToHighPage(port: muteEnvelopePort);
            emitter.JumpAbsolute(label: sectionDone);
        }

        // Play the step: wait := duration - 1 (this frame counts), then pump the register bytes to the ports.
        emitter.MarkLabel(label: play);
        emitter.Decrement(register: Reg8.A);
        emitter.StoreAToAddress(address: waitAddress);
        emitter.LoadImmediate(destination: Reg8.C, value: portBase);
        emitter.LoadImmediate(destination: Reg8.B, value: (byte)registerCount);
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
