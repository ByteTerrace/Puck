namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The music sequencer for the advanced machine's legacy programmable-sound channel two, consuming the same compiled
/// stream the humble machine's driver does.
/// </summary>
/// <remarks>
/// <para>
/// The stream is a run of steps, each a duration byte followed by the four channel registers; a zero duration ends it
/// and rewinds to the start. The advanced machine's legacy channel exposes those same four registers as two halfwords,
/// so one compiled track drives both targets and a document's audio does not change with its target.
/// </para>
/// <para>
/// Register map: 0x04000068 holds duty/length in its low byte and envelope in its high byte; 0x0400006C holds the
/// frequency low byte and the control/trigger byte. KEEP IN SYNC with the humble driver's port order.
/// </para>
/// </remarks>
public sealed class AgbSoundDriver {
    /// <summary>The master enable register; bit seven powers the sound unit.</summary>
    private const uint ControlEnableAddress = 0x04000084u;
    /// <summary>The mixing register; the low two bits set the legacy channels' volume ratio.</summary>
    private const uint ControlMixAddress = 0x04000082u;
    /// <summary>The volume and routing register.</summary>
    private const uint ControlVolumeAddress = 0x04000080u;
    private const uint DutyEnvelopeAddress = 0x04000068u;
    private const uint FrequencyControlAddress = 0x0400006Cu;
    /// <summary>Pulse one's sweep register; the effect stream's first byte lands in its low half.</summary>
    private const uint EffectSweepAddress = 0x04000060u;
    private const uint EffectDutyEnvelopeAddress = 0x04000062u;
    private const uint EffectFrequencyControlAddress = 0x04000064u;
    private const uint NoiseLengthEnvelopeAddress = 0x04000078u;
    private const uint NoiseFrequencyControlAddress = 0x0400007Cu;
    /// <summary>The wave voice's switch register; its five stream bytes span this and the two that follow.</summary>
    private const uint WaveSwitchAddress = 0x04000070u;
    private const uint WaveLengthVolumeAddress = 0x04000072u;
    private const uint WaveFrequencyControlAddress = 0x04000074u;
    /// <summary>The first of the sixteen bytes holding the wave voice's thirty-two four-bit samples.</summary>
    private const uint WavePatternAddress = 0x04000090u;
    private const int WaveStateOffset = 28;
    /// <summary>Each one-shot voice keeps a pointer and a wait counter after the music voice's twelve bytes.</summary>
    private const int PulseStateOffset = 12;
    private const int NoiseStateOffset = 20;

    /// <summary>The state block's size: a pointer and a wait counter for each of the three voices.</summary>
    public const int StateByteCount = 36;

    private readonly ThumbEmitter m_emitter;
    private readonly uint m_stateAddress;

    /// <summary>Creates the driver over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="stateAddress">The word-aligned base of the driver's state block.</param>
    public AgbSoundDriver(ThumbEmitter emitter, uint stateAddress) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_stateAddress = stateAddress;
    }

    /// <summary>Emits the sound unit's power-up: master on, both sides at full volume, legacy channels at full ratio.</summary>
    public void EmitBoot() {
        StoreHalf(address: ControlEnableAddress, value: 0x0080u);
        StoreHalf(address: ControlVolumeAddress, value: 0xFF77u);
        StoreHalf(address: ControlMixAddress, value: 0x0002u);
        StoreHalf(address: DutyEnvelopeAddress, value: 0x0000u);
        m_emitter.LoadConstant(destination: LowRegister.R0, value: m_stateAddress);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
        for (var offset = 0; offset < StateByteCount; offset += 4) {
            m_emitter.StoreWord(source: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: offset);
        }
    }

    /// <summary>Emits a start of the stream at <paramref name="streamAddress"/>, replacing whatever was playing.</summary>
    /// <param name="streamAddress">The compiled track's address in the cartridge image.</param>
    public void EmitStart(uint streamAddress) {
        m_emitter.LoadConstant(destination: LowRegister.R0, value: m_stateAddress);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: streamAddress);
        m_emitter.StoreWord(source: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: 0);
        m_emitter.StoreWord(source: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: 4);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
        m_emitter.StoreByte(source: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: 8);
    }

    /// <summary>Emits a stop of the music voice and silences its channel. Safe with nothing playing.</summary>
    public void EmitStop() {
        m_emitter.LoadConstant(destination: LowRegister.R0, value: m_stateAddress);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
        m_emitter.StoreWord(source: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: 0);
        StoreHalf(address: DutyEnvelopeAddress, value: 0x0000u);
    }

    /// <summary>Emits a start of a one-shot on the voice reserved for it, leaving the music voice untouched.</summary>
    /// <param name="streamAddress">The compiled effect's address in the cartridge image.</param>
    /// <param name="voice">Which one-shot voice plays it: 0 pulse one, 1 noise, 2 wave.</param>
    public void EmitEffectStart(uint streamAddress, int voice) {
        var offset = voice switch { 1 => NoiseStateOffset, 2 => WaveStateOffset, _ => PulseStateOffset };
        m_emitter.LoadConstant(destination: LowRegister.R0, value: m_stateAddress + (uint)offset);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: streamAddress);
        m_emitter.StoreWord(source: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: 0);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
        m_emitter.StoreByte(source: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: 4);
    }

    /// <summary>Emits a copy of a waveform into the wave voice's pattern registers.</summary>
    /// <param name="patternAddress">The sixteen bytes holding thirty-two four-bit samples.</param>
    /// <remarks>The voice's switch must be off while the pattern is written, so this clears it first.</remarks>
    public void EmitWavePatternLoad(uint patternAddress) {
        StoreHalf(address: WaveSwitchAddress, value: 0x0000u);
        m_emitter.LoadConstant(destination: LowRegister.R0, value: patternAddress);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: WavePatternAddress);
        m_emitter.MoveImmediate(destination: LowRegister.R2, value: 16);
        var copy = m_emitter.NewLabel();
        m_emitter.MarkLabel(label: copy);
        m_emitter.LoadByte(destination: LowRegister.R3, baseRegister: LowRegister.R0, byteOffset: 0);
        m_emitter.StoreByte(source: LowRegister.R3, baseRegister: LowRegister.R1, byteOffset: 0);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 1);
        m_emitter.AddImmediate(register: LowRegister.R1, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R2, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: copy);
    }

    /// <summary>Emits one frame of every voice: wait out the current step, or publish the next one.</summary>
    public void EmitFrameTick() {
        EmitMusicTick();
        EmitEffectTick(stateOffset: PulseStateOffset, registerCount: 5, muteAddress: EffectDutyEnvelopeAddress);
        EmitEffectTick(stateOffset: NoiseStateOffset, registerCount: 4, muteAddress: NoiseLengthEnvelopeAddress);
        EmitEffectTick(stateOffset: WaveStateOffset, registerCount: 5, muteAddress: WaveSwitchAddress);
    }

    // A one-shot stops on its terminator rather than rewinding, and silences its channel's envelope on the way out.
    private void EmitEffectTick(int stateOffset, int registerCount, uint muteAddress) {
        var done = m_emitter.NewLabel();
        var play = m_emitter.NewLabel();
        var step = m_emitter.NewLabel();
        var isNoise = registerCount == 4;
        m_emitter.LoadConstant(destination: LowRegister.R4, value: m_stateAddress + (uint)stateOffset);
        m_emitter.LoadWord(destination: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 0);
        m_emitter.CompareImmediate(register: LowRegister.R0, value: 0);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: done);

        m_emitter.LoadByte(destination: LowRegister.R1, baseRegister: LowRegister.R4, byteOffset: 4);
        m_emitter.CompareImmediate(register: LowRegister.R1, value: 0);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: step);
        m_emitter.SubtractImmediate(register: LowRegister.R1, value: 1);
        m_emitter.StoreByte(source: LowRegister.R1, baseRegister: LowRegister.R4, byteOffset: 4);
        m_emitter.Branch(label: done);

        m_emitter.MarkLabel(label: step);
        m_emitter.LoadByte(destination: LowRegister.R2, baseRegister: LowRegister.R0, byteOffset: 0);
        m_emitter.CompareImmediate(register: LowRegister.R2, value: 0);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: play);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
        m_emitter.StoreWord(source: LowRegister.R1, baseRegister: LowRegister.R4, byteOffset: 0);
        StoreHalf(address: muteAddress, value: 0x0000u);
        m_emitter.Branch(label: done);

        m_emitter.MarkLabel(label: play);
        m_emitter.SubtractImmediate(register: LowRegister.R2, value: 1);
        m_emitter.StoreByte(source: LowRegister.R2, baseRegister: LowRegister.R4, byteOffset: 4);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 1);
        if (registerCount == 4) {
            EmitRegisterPair(lowOffset: 0, address: NoiseLengthEnvelopeAddress);
            EmitRegisterPair(lowOffset: 2, address: NoiseFrequencyControlAddress);
        } else if (stateOffset == WaveStateOffset) {
            EmitRegisterByte(offset: 0, address: WaveSwitchAddress);
            EmitRegisterPair(lowOffset: 1, address: WaveLengthVolumeAddress);
            EmitRegisterPair(lowOffset: 3, address: WaveFrequencyControlAddress);
        } else {
            EmitRegisterByte(offset: 0, address: EffectSweepAddress);
            EmitRegisterPair(lowOffset: 1, address: EffectDutyEnvelopeAddress);
            EmitRegisterPair(lowOffset: 3, address: EffectFrequencyControlAddress);
        }

        m_emitter.AddImmediate(register: LowRegister.R0, value: (byte)registerCount);
        m_emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 0);
        m_emitter.MarkLabel(label: done);
    }

    // Pulse one's sweep has no partner byte; it occupies a halfword register on its own.
    private void EmitRegisterByte(int offset, uint address) {
        m_emitter.LoadByte(destination: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: offset);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreHalf(source: LowRegister.R1, baseRegister: LowRegister.R2, byteOffset: 0);
    }

    private void EmitMusicTick() {
        var done = m_emitter.NewLabel();
        var play = m_emitter.NewLabel();
        var step = m_emitter.NewLabel();
        m_emitter.LoadConstant(destination: LowRegister.R4, value: m_stateAddress);
        m_emitter.LoadWord(destination: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 0);
        m_emitter.CompareImmediate(register: LowRegister.R0, value: 0);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: done);

        m_emitter.LoadByte(destination: LowRegister.R1, baseRegister: LowRegister.R4, byteOffset: 8);
        m_emitter.CompareImmediate(register: LowRegister.R1, value: 0);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: step);
        m_emitter.SubtractImmediate(register: LowRegister.R1, value: 1);
        m_emitter.StoreByte(source: LowRegister.R1, baseRegister: LowRegister.R4, byteOffset: 8);
        m_emitter.Branch(label: done);

        m_emitter.MarkLabel(label: step);
        m_emitter.LoadByte(destination: LowRegister.R2, baseRegister: LowRegister.R0, byteOffset: 0);
        m_emitter.CompareImmediate(register: LowRegister.R2, value: 0);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: play);
        // The terminator rewinds and plays the first step this same frame; a track that is only a terminator idles.
        m_emitter.LoadWord(destination: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 4);
        m_emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 0);
        m_emitter.LoadByte(destination: LowRegister.R2, baseRegister: LowRegister.R0, byteOffset: 0);
        m_emitter.CompareImmediate(register: LowRegister.R2, value: 0);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: done);

        m_emitter.MarkLabel(label: play);
        m_emitter.SubtractImmediate(register: LowRegister.R2, value: 1);
        m_emitter.StoreByte(source: LowRegister.R2, baseRegister: LowRegister.R4, byteOffset: 8);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 1);
        EmitRegisterPair(lowOffset: 0, address: DutyEnvelopeAddress);
        EmitRegisterPair(lowOffset: 2, address: FrequencyControlAddress);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 4);
        m_emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 0);
        m_emitter.MarkLabel(label: done);
    }

    // Packs two consecutive stream bytes into the halfword register that carries them.
    private void EmitRegisterPair(int lowOffset, uint address) {
        m_emitter.LoadByte(destination: LowRegister.R1, baseRegister: LowRegister.R0, byteOffset: lowOffset);
        m_emitter.LoadByte(destination: LowRegister.R3, baseRegister: LowRegister.R0, byteOffset: lowOffset + 1);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R3, source: LowRegister.R3, amount: 8);
        m_emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R1, source: LowRegister.R3);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreHalf(source: LowRegister.R1, baseRegister: LowRegister.R2, byteOffset: 0);
    }

    private void StoreHalf(uint address, uint value) {
        m_emitter.LoadConstant(destination: LowRegister.R0, value: value);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreHalf(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
    }
}
