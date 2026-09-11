using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The advanced machine's digital sound path: a timer clocks a transfer out of a mix buffer into the sound queue, and
/// a per-frame mixer sums the sounding voices into the half of that buffer the hardware is not reading.
/// </summary>
/// <remarks>
/// <para>
/// Sign convention: samples are signed eight-bit and the queue takes them signed, so the mixer accumulates signed and
/// clamps to -128..127 rather than wrapping. A wrap would turn a loud moment into a full-scale inversion.
/// </para>
/// <para>
/// Layout: two <see cref="SamplesPerFrame"/>-byte halves, alternating each frame. The transfer runs continuously over
/// the pair, so the mixer must always fill the half the transfer is not in; the frame counter's low bit picks it.
/// </para>
/// <para>
/// The sample rate and buffer size are tied: one frame is 280896 machine cycles and the timer reloads every
/// <see cref="CyclesPerSample"/>, so a half must cover a whole frame's worth or the queue starves.
/// </para>
/// </remarks>
public sealed class AgbDirectSound {
    /// <summary>Machine cycles between samples; the timer's reload is its two's complement.</summary>
    public const int CyclesPerSample = 1024;
    /// <summary>Samples in one half of the mix buffer, covering a whole frame at <see cref="CyclesPerSample"/>.</summary>
    public const int SamplesPerFrame = 288;
    /// <summary>Bytes of work memory the engine needs: the mix buffer plus per-voice state.</summary>
    public const int StateByteCount = (SamplesPerFrame * 2) + (CartridgeLimits.SampleVoiceCount * 8) + 4;

    private const uint ControlMixAddress = 0x04000082u;
    /// <summary>The first transfer's control halfword. 0x040000C4 is its word count, which the queue mode ignores.</summary>
    private const uint DmaControlAddress = 0x040000C6u;
    private const uint DmaDestinationAddress = 0x040000C0u;
    private const uint DmaSourceAddress = 0x040000BCu;
    private const uint QueueAddress = 0x040000A0u;
    private const uint TimerControlAddress = 0x04000102u;
    private const uint TimerReloadAddress = 0x04000100u;

    private readonly ThumbEmitter m_emitter;
    private readonly uint m_stateAddress;

    /// <summary>Creates the engine over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="stateAddress">The word-aligned base of the engine's work memory.</param>
    public AgbDirectSound(ThumbEmitter emitter, uint stateAddress) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_stateAddress = stateAddress;
    }

    private uint BufferAddress => m_stateAddress;
    private uint CursorAddress => m_stateAddress + (SamplesPerFrame * 2);
    private uint VoiceAddress => CursorAddress + 4;

    /// <summary>Emits the engine's power-up: silence the buffer, arm the transfer, and start the timer.</summary>
    public void EmitBoot() {
        var clear = m_emitter.NewLabel();
        m_emitter.LoadConstant(destination: LowRegister.R0, value: m_stateAddress);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: (uint)StateByteCount);
        m_emitter.MoveImmediate(destination: LowRegister.R2, value: 0);
        m_emitter.MarkLabel(label: clear);
        m_emitter.StoreByte(source: LowRegister.R2, baseRegister: LowRegister.R0, byteOffset: 0);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R1, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: clear);

        // Route the digital path at full volume, reset its queue, and clock it from the first timer.
        StoreHalf(address: ControlMixAddress, value: 0x0B04u);
        StoreWord(address: DmaSourceAddress, value: BufferAddress);
        StoreWord(address: DmaDestinationAddress, value: QueueAddress);
        // Enable, queue-request start, word width, repeating, destination fixed on the queue register.
        StoreHalf(address: DmaControlAddress, value: 0xB640u);
        StoreHalf(address: TimerReloadAddress, value: (uint)(65536 - CyclesPerSample));
        StoreHalf(address: TimerControlAddress, value: 0x0080u);
    }

    /// <summary>Emits a start of a recorded one-shot in the first idle voice, dropping it when every voice is busy.</summary>
    /// <param name="sampleAddress">The sample's address in the cartridge image.</param>
    /// <param name="sampleLength">The sample's length in bytes.</param>
    public void EmitStart(uint sampleAddress, int sampleLength) {
        var done = m_emitter.NewLabel();
        for (var voice = 0; voice < CartridgeLimits.SampleVoiceCount; ++voice) {
            var busy = m_emitter.NewLabel();
            m_emitter.LoadConstant(destination: LowRegister.R2, value: VoiceAddress + (uint)(voice * 8));
            m_emitter.LoadWord(destination: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 4);
            m_emitter.CompareImmediate(register: LowRegister.R0, value: 0);
            m_emitter.Branch(condition: ThumbCondition.NotEqual, label: busy);
            m_emitter.LoadConstant(destination: LowRegister.R0, value: sampleAddress);
            m_emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
            m_emitter.LoadConstant(destination: LowRegister.R0, value: (uint)sampleLength);
            m_emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 4);
            m_emitter.Branch(label: done);
            m_emitter.MarkLabel(label: busy);
        }

        m_emitter.MarkLabel(label: done);
    }

    /// <summary>Emits one frame of mixing into the half the transfer is not reading.</summary>
    public void EmitFrameTick() {
        var fill = m_emitter.NewLabel();
        var silent = m_emitter.NewLabel();

        // The cursor alternates halves; r4 walks the one being filled.
        m_emitter.LoadConstant(destination: LowRegister.R2, value: CursorAddress);
        m_emitter.LoadWord(destination: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 1);
        m_emitter.Alu(op: ThumbAlu.ExclusiveOr, destination: LowRegister.R0, source: LowRegister.R1);
        m_emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: SamplesPerFrame);
        m_emitter.Alu(op: ThumbAlu.Multiply, destination: LowRegister.R0, source: LowRegister.R1);
        m_emitter.LoadConstant(destination: LowRegister.R4, value: BufferAddress);
        m_emitter.AddRegister(destination: LowRegister.R4, source: LowRegister.R4, operand: LowRegister.R0);

        // Silence the half first, then accumulate each sounding voice over it.
        m_emitter.MoveRegister(destination: LowRegister.R0, source: LowRegister.R4);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: SamplesPerFrame);
        m_emitter.MoveImmediate(destination: LowRegister.R2, value: 0);
        m_emitter.MarkLabel(label: silent);
        m_emitter.StoreByte(source: LowRegister.R2, baseRegister: LowRegister.R0, byteOffset: 0);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R1, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: silent);

        for (var voice = 0; voice < CartridgeLimits.SampleVoiceCount; ++voice) {
            EmitVoiceMix(voice: voice);
        }

        m_emitter.MarkLabel(label: fill);
    }

    // One voice: walk its remaining samples into the half, clamping each sum, and retire it when it runs out.
    private void EmitVoiceMix(int voice) {
        var done = m_emitter.NewLabel();
        var loop = m_emitter.NewLabel();
        var high = m_emitter.NewLabel();
        var low = m_emitter.NewLabel();
        var stored = m_emitter.NewLabel();

        m_emitter.LoadConstant(destination: LowRegister.R5, value: VoiceAddress + (uint)(voice * 8));
        m_emitter.LoadWord(destination: LowRegister.R6, baseRegister: LowRegister.R5, byteOffset: 4);
        m_emitter.CompareImmediate(register: LowRegister.R6, value: 0);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: done);
        m_emitter.LoadWord(destination: LowRegister.R7, baseRegister: LowRegister.R5, byteOffset: 0);
        m_emitter.MoveRegister(destination: LowRegister.R3, source: LowRegister.R4);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: SamplesPerFrame);

        m_emitter.MarkLabel(label: loop);
        m_emitter.CompareImmediate(register: LowRegister.R6, value: 0);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: done);
        EmitLoadSigned(destination: LowRegister.R0, baseRegister: LowRegister.R7);
        EmitLoadSigned(destination: LowRegister.R1, baseRegister: LowRegister.R3);
        m_emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1);

        // Clamp: a sum past a signed byte would otherwise wrap and inverted the waveform.
        m_emitter.LoadConstant(destination: LowRegister.R1, value: 127);
        m_emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R0, source: LowRegister.R1);
        m_emitter.Branch(condition: ThumbCondition.SignedGreaterOrEqual, label: high);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: unchecked((uint)-128));
        m_emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R0, source: LowRegister.R1);
        m_emitter.Branch(condition: ThumbCondition.SignedLess, label: low);
        m_emitter.Branch(label: stored);
        m_emitter.MarkLabel(label: high);
        m_emitter.LoadConstant(destination: LowRegister.R0, value: 127);
        m_emitter.Branch(label: stored);
        m_emitter.MarkLabel(label: low);
        m_emitter.LoadConstant(destination: LowRegister.R0, value: unchecked((uint)-128));
        m_emitter.MarkLabel(label: stored);

        m_emitter.StoreByte(source: LowRegister.R0, baseRegister: LowRegister.R3, byteOffset: 0);
        m_emitter.AddImmediate(register: LowRegister.R3, value: 1);
        m_emitter.AddImmediate(register: LowRegister.R7, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R6, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R2, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: loop);

        m_emitter.MarkLabel(label: done);
        m_emitter.StoreWord(source: LowRegister.R7, baseRegister: LowRegister.R5, byteOffset: 0);
        m_emitter.StoreWord(source: LowRegister.R6, baseRegister: LowRegister.R5, byteOffset: 4);
    }

    // The emitter has no immediate-offset signed load, so widen the byte in place instead of spending a register.
    private void EmitLoadSigned(LowRegister destination, LowRegister baseRegister) {
        m_emitter.LoadByte(destination: destination, baseRegister: baseRegister, byteOffset: 0);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: destination, source: destination, amount: 24);
        m_emitter.ShiftImmediate(op: ThumbShift.ArithmeticRight, destination: destination, source: destination, amount: 24);
    }

    private void StoreHalf(uint address, uint value) {
        m_emitter.LoadConstant(destination: LowRegister.R0, value: value);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreHalf(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
    }

    private void StoreWord(uint address, uint value) {
        m_emitter.LoadConstant(destination: LowRegister.R0, value: value);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
    }
}
