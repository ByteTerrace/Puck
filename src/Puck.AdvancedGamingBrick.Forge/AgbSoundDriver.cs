using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The four sequencer voices of the advanced machine's legacy programmable-sound unit, consuming the same compiled
/// streams the humble machine's driver does.
/// </summary>
/// <remarks>
/// <para>
/// A stream is a run of steps, each a duration byte followed by that voice's channel registers; a zero duration ends
/// it, rewinding to the loop start the voice carries or stopping the voice when it carries none. The advanced
/// machine's legacy channels expose the same registers as halfwords, so one compiled part drives both targets and a
/// document's audio does not change with its target.
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
    private const uint EffectDutyEnvelopeAddress = 0x04000062u;
    private const uint EffectFrequencyControlAddress = 0x04000064u;
    /// <summary>Pulse one's sweep register; the effect stream's first byte lands in its low half.</summary>
    private const uint EffectSweepAddress = 0x04000060u;
    private const uint NoiseFrequencyControlAddress = 0x0400007Cu;
    private const uint NoiseLengthEnvelopeAddress = 0x04000078u;
    private const uint Pulse2DutyEnvelopeAddress = 0x04000068u;
    private const uint Pulse2FrequencyControlAddress = 0x0400006Cu;
    /// <summary>One voice's state: a read pointer, a loop start and a wait counter, word aligned.</summary>
    private const int VoiceStateSize = 12;
    private const uint WaveFrequencyControlAddress = 0x04000074u;
    private const uint WaveLengthVolumeAddress = 0x04000072u;
    /// <summary>The first of the sixteen bytes holding the wave voice's thirty-two four-bit samples.</summary>
    private const uint WavePatternAddress = 0x04000090u;
    /// <summary>The wave voice's switch register; its five stream bytes span this and the two that follow.</summary>
    private const uint WaveSwitchAddress = 0x04000070u;

    /// <summary>The state block's size: one uniform block for each of the four voices.</summary>
    public const int StateByteCount = (VoiceStateSize * 4);

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

    // Pulse one's sweep has no partner byte; it occupies a halfword register on its own.
    private void EmitRegisterByte(int offset, uint address) {
        m_emitter.LoadByte(
            baseRegister: LowRegister.R0,
            byteOffset: offset,
            destination: LowRegister.R1
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R2,
            value: address
        );
        m_emitter.StoreHalf(
            baseRegister: LowRegister.R2,
            byteOffset: 0,
            source: LowRegister.R1
        );
    }
    // Packs two consecutive stream bytes into the halfword register that carries them.
    private void EmitRegisterPair(int lowOffset, uint address) {
        m_emitter.LoadByte(
            baseRegister: LowRegister.R0,
            byteOffset: lowOffset,
            destination: LowRegister.R1
        );
        m_emitter.LoadByte(
            baseRegister: LowRegister.R0,
            byteOffset: (lowOffset + 1),
            destination: LowRegister.R3
        );
        m_emitter.ShiftImmediate(
            amount: 8,
            destination: LowRegister.R3,
            op: ThumbShift.LogicalLeft,
            source: LowRegister.R3
        );
        m_emitter.Alu(
            destination: LowRegister.R1,
            op: ThumbAlu.Or,
            source: LowRegister.R3
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R2,
            value: address
        );
        m_emitter.StoreHalf(
            baseRegister: LowRegister.R2,
            byteOffset: 0,
            source: LowRegister.R1
        );
    }
    // One voice's per-frame section. Idle (a null read pointer) falls through, a positive wait burns the frame, and
    // otherwise the next step's duration byte decides: zero is the terminator, where a loop start rewinds the voice
    // and its absence stops and silences it.
    private void EmitVoiceTick(CartridgeVoice voice) {
        var done = m_emitter.NewLabel();
        var play = m_emitter.NewLabel();
        var step = m_emitter.NewLabel();
        var stop = m_emitter.NewLabel();
        var registerCount = ((voice is CartridgeVoice.Pulse2 or CartridgeVoice.Noise)
            ? 4
            : 5
        );

        m_emitter.LoadConstant(
            destination: LowRegister.R4,
            value: (m_stateAddress + StateOffset(voice: voice))
        );
        m_emitter.LoadWord(
            baseRegister: LowRegister.R4,
            byteOffset: 0,
            destination: LowRegister.R0
        );
        m_emitter.CompareImmediate(
            register: LowRegister.R0,
            value: 0
        );
        m_emitter.Branch(
            condition: ThumbCondition.Equal,
            label: done
        );

        m_emitter.LoadByte(
            baseRegister: LowRegister.R4,
            byteOffset: 8,
            destination: LowRegister.R1
        );
        m_emitter.CompareImmediate(
            register: LowRegister.R1,
            value: 0
        );
        m_emitter.Branch(
            condition: ThumbCondition.Equal,
            label: step
        );
        m_emitter.SubtractImmediate(
            register: LowRegister.R1,
            value: 1
        );
        m_emitter.StoreByte(
            baseRegister: LowRegister.R4,
            byteOffset: 8,
            source: LowRegister.R1
        );
        m_emitter.Branch(label: done);

        m_emitter.MarkLabel(label: step);
        m_emitter.LoadByte(
            baseRegister: LowRegister.R0,
            byteOffset: 0,
            destination: LowRegister.R2
        );
        m_emitter.CompareImmediate(
            register: LowRegister.R2,
            value: 0
        );
        m_emitter.Branch(
            condition: ThumbCondition.NotEqual,
            label: play
        );
        m_emitter.LoadWord(
            baseRegister: LowRegister.R4,
            byteOffset: 4,
            destination: LowRegister.R0
        );
        m_emitter.CompareImmediate(
            register: LowRegister.R0,
            value: 0
        );
        m_emitter.Branch(
            condition: ThumbCondition.Equal,
            label: stop
        );
        // The loop start rewinds and plays the first step this same frame; a part that is only a terminator idles.
        m_emitter.StoreWord(
            baseRegister: LowRegister.R4,
            byteOffset: 0,
            source: LowRegister.R0
        );
        m_emitter.LoadByte(
            baseRegister: LowRegister.R0,
            byteOffset: 0,
            destination: LowRegister.R2
        );
        m_emitter.CompareImmediate(
            register: LowRegister.R2,
            value: 0
        );
        m_emitter.Branch(
            condition: ThumbCondition.NotEqual,
            label: play
        );
        m_emitter.MarkLabel(label: stop);
        m_emitter.MoveImmediate(
            destination: LowRegister.R1,
            value: 0
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R4,
            byteOffset: 0,
            source: LowRegister.R1
        );
        StoreHalf(
            address: MuteAddress(voice: voice),
            value: 0x0000u
        );
        m_emitter.Branch(label: done);

        m_emitter.MarkLabel(label: play);
        m_emitter.SubtractImmediate(
            register: LowRegister.R2,
            value: 1
        );
        m_emitter.StoreByte(
            baseRegister: LowRegister.R4,
            byteOffset: 8,
            source: LowRegister.R2
        );
        m_emitter.AddImmediate(
            register: LowRegister.R0,
            value: 1
        );
        switch (voice) {
            case CartridgeVoice.Pulse2:
                EmitRegisterPair(
                    address: Pulse2DutyEnvelopeAddress,
                    lowOffset: 0
                );
                EmitRegisterPair(
                    address: Pulse2FrequencyControlAddress,
                    lowOffset: 2
                );
                break;
            case CartridgeVoice.Noise:
                EmitRegisterPair(
                    address: NoiseLengthEnvelopeAddress,
                    lowOffset: 0
                );
                EmitRegisterPair(
                    address: NoiseFrequencyControlAddress,
                    lowOffset: 2
                );
                break;
            case CartridgeVoice.Wave:
                EmitRegisterByte(
                    address: WaveSwitchAddress,
                    offset: 0
                );
                EmitRegisterPair(
                    address: WaveLengthVolumeAddress,
                    lowOffset: 1
                );
                EmitRegisterPair(
                    address: WaveFrequencyControlAddress,
                    lowOffset: 3
                );
                break;
            default:
                EmitRegisterByte(
                    address: EffectSweepAddress,
                    offset: 0
                );
                EmitRegisterPair(
                    address: EffectDutyEnvelopeAddress,
                    lowOffset: 1
                );
                EmitRegisterPair(
                    address: EffectFrequencyControlAddress,
                    lowOffset: 3
                );
                break;
        }

        m_emitter.AddImmediate(
            register: LowRegister.R0,
            value: ((byte)registerCount)
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R4,
            byteOffset: 0,
            source: LowRegister.R0
        );
        m_emitter.MarkLabel(label: done);
    }
    private static uint MuteAddress(CartridgeVoice voice) => voice switch {
        CartridgeVoice.Noise => NoiseLengthEnvelopeAddress,
        CartridgeVoice.Wave => WaveSwitchAddress,
        CartridgeVoice.Pulse2 => Pulse2DutyEnvelopeAddress,
        _ => EffectDutyEnvelopeAddress,
    };
    private static uint StateOffset(CartridgeVoice voice) => ((uint)(((int)voice) * VoiceStateSize));
    private void StoreHalf(uint address, uint value) {
        m_emitter.LoadConstant(
            destination: LowRegister.R0,
            value: value
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R2,
            value: address
        );
        m_emitter.StoreHalf(
            baseRegister: LowRegister.R2,
            byteOffset: 0,
            source: LowRegister.R0
        );
    }

    /// <summary>Emits the sound unit's power-up: master on, both sides at full volume, legacy channels at full ratio.</summary>
    public void EmitBoot() {
        StoreHalf(
            address: ControlEnableAddress,
            value: 0x0080u
        );
        StoreHalf(
            address: ControlVolumeAddress,
            value: 0xFF77u
        );
        StoreHalf(
            address: ControlMixAddress,
            value: 0x0002u
        );
        StoreHalf(
            address: Pulse2DutyEnvelopeAddress,
            value: 0x0000u
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R0,
            value: m_stateAddress
        );
        m_emitter.MoveImmediate(
            destination: LowRegister.R1,
            value: 0
        );
        for (var offset = 0; (offset < StateByteCount); offset += 4) {
            m_emitter.StoreWord(
                baseRegister: LowRegister.R0,
                byteOffset: offset,
                source: LowRegister.R1
            );
        }
    }
    /// <summary>Emits a start of a one-shot: the same trigger with no loop start, so it stops at its terminator.</summary>
    /// <param name="streamAddress">The compiled effect's address in the cartridge image.</param>
    /// <param name="voice">The voice that plays it.</param>
    public void EmitEffectStart(uint streamAddress, CartridgeVoice voice) {
        m_emitter.LoadConstant(
            destination: LowRegister.R0,
            value: (m_stateAddress + StateOffset(voice: voice))
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R1,
            value: streamAddress
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R0,
            byteOffset: 0,
            source: LowRegister.R1
        );
        m_emitter.MoveImmediate(
            destination: LowRegister.R1,
            value: 0
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R0,
            byteOffset: 4,
            source: LowRegister.R1
        );
        m_emitter.StoreByte(
            baseRegister: LowRegister.R0,
            byteOffset: 8,
            source: LowRegister.R1
        );
    }
    /// <summary>Emits one frame of every voice: wait out the current step, or publish the next one.</summary>
    public void EmitFrameTick() {
        EmitVoiceTick(voice: CartridgeVoice.Pulse1);
        EmitVoiceTick(voice: CartridgeVoice.Pulse2);
        EmitVoiceTick(voice: CartridgeVoice.Wave);
        EmitVoiceTick(voice: CartridgeVoice.Noise);
    }
    /// <summary>Emits a start of a looping part on one voice, replacing whatever that voice was playing.</summary>
    /// <param name="streamAddress">The compiled part's address in the cartridge image.</param>
    /// <param name="voice">The voice the part occupies.</param>
    public void EmitStart(uint streamAddress, CartridgeVoice voice) {
        m_emitter.LoadConstant(
            destination: LowRegister.R0,
            value: (m_stateAddress + StateOffset(voice: voice))
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R1,
            value: streamAddress
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R0,
            byteOffset: 0,
            source: LowRegister.R1
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R0,
            byteOffset: 4,
            source: LowRegister.R1
        );
        m_emitter.MoveImmediate(
            destination: LowRegister.R1,
            value: 0
        );
        m_emitter.StoreByte(
            baseRegister: LowRegister.R0,
            byteOffset: 8,
            source: LowRegister.R1
        );
    }
    /// <summary>Emits a stop of one voice and silences its channel. Safe with nothing playing.</summary>
    /// <param name="voice">The voice to silence.</param>
    public void EmitStop(CartridgeVoice voice) {
        m_emitter.LoadConstant(
            destination: LowRegister.R0,
            value: (m_stateAddress + StateOffset(voice: voice))
        );
        m_emitter.MoveImmediate(
            destination: LowRegister.R1,
            value: 0
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R0,
            byteOffset: 0,
            source: LowRegister.R1
        );
        m_emitter.StoreWord(
            baseRegister: LowRegister.R0,
            byteOffset: 4,
            source: LowRegister.R1
        );
        StoreHalf(
            address: MuteAddress(voice: voice),
            value: 0x0000u
        );
    }
    /// <summary>Emits a copy of a waveform into the wave voice's pattern registers.</summary>
    /// <param name="patternAddress">The sixteen bytes holding thirty-two four-bit samples.</param>
    /// <remarks>The voice's switch must be off while the pattern is written, so this clears it first.</remarks>
    public void EmitWavePatternLoad(uint patternAddress) {
        StoreHalf(
            address: WaveSwitchAddress,
            value: 0x0000u
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R0,
            value: patternAddress
        );
        m_emitter.LoadConstant(
            destination: LowRegister.R1,
            value: WavePatternAddress
        );
        m_emitter.MoveImmediate(
            destination: LowRegister.R2,
            value: 16
        );
        var copy = m_emitter.NewLabel();

        m_emitter.MarkLabel(label: copy);
        m_emitter.LoadByte(
            baseRegister: LowRegister.R0,
            byteOffset: 0,
            destination: LowRegister.R3
        );
        m_emitter.StoreByte(
            baseRegister: LowRegister.R1,
            byteOffset: 0,
            source: LowRegister.R3
        );
        m_emitter.AddImmediate(
            register: LowRegister.R0,
            value: 1
        );
        m_emitter.AddImmediate(
            register: LowRegister.R1,
            value: 1
        );
        m_emitter.SubtractImmediate(
            register: LowRegister.R2,
            value: 1
        );
        m_emitter.Branch(
            condition: ThumbCondition.NotEqual,
            label: copy
        );
    }
}
