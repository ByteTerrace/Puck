namespace Puck.HumbleGamingDeck;

/// <summary>
/// The 2A03's audio processing unit, clocked once per CPU cycle: two pulse channels, a triangle, a noise channel, the
/// delta-modulation channel, and the frame counter that clocks their envelopes, sweeps, and length counters and raises
/// the frame interrupt. Its emulated output is five integer levels — pulse and triangle and noise 0 to 15, DMC 0 to 127 —
/// read through <see cref="PulseOneLevel"/> and its siblings; mixing them into sound is presentation.
/// <para>
/// The frame counter follows the NTSC sequence in CPU cycles. The DMC's sample fetches go through the DMA unit, which
/// polls <see cref="DmcNeedsSample"/> and answers with <see cref="CompleteDmcFetch"/>.
/// </para>
/// </summary>
public sealed partial class HgdApu : ISnapshotable {
    private Pulse m_pulseOne;
    private Pulse m_pulseTwo;
    private Triangle m_triangle;
    private Noise m_noise;
    private Dmc m_dmc;
    private bool m_evenCycle = true;
    private bool m_fiveStep;
    private bool m_frameIrqInhibit;
    private bool m_frameIrq;
    private int m_frameCycle;
    private int m_frameResetDelay;
    private bool m_pulseOneEnabled;
    private bool m_pulseTwoEnabled;
    private bool m_triangleEnabled;
    private bool m_noiseEnabled;

    /// <summary>Initializes a new instance of the <see cref="HgdApu"/> class at power-on: every channel silent, the
    /// noise shift register at 1, and the frame counter in four-step mode with its interrupt enabled.</summary>
    public HgdApu() {
        m_noise.Shift = 1;
        m_noise.Period = NoisePeriods[0];
        m_dmc.Period = DmcPeriods[0];
        m_dmc.BitsRemaining = 8;
    }

    /// <summary>Gets the first pulse channel's level, 0 through 15.</summary>
    public byte PulseOneLevel => m_pulseOne.Output(onesComplement: true);
    /// <summary>Gets the second pulse channel's level, 0 through 15.</summary>
    public byte PulseTwoLevel => m_pulseTwo.Output(onesComplement: false);
    /// <summary>Gets the triangle channel's level, 0 through 15.</summary>
    public byte TriangleLevel => m_triangle.Output;
    /// <summary>Gets the noise channel's level, 0 through 15.</summary>
    public byte NoiseLevel => m_noise.Output;
    /// <summary>Gets the delta-modulation channel's level, 0 through 127.</summary>
    public byte DmcLevel => m_dmc.Level;
    /// <summary>Gets the APU's interrupt request: the frame interrupt or the DMC's end-of-sample interrupt.</summary>
    public bool Irq => (m_frameIrq || m_dmc.Irq);
    /// <summary>Gets whether the DMC's sample buffer is empty while bytes remain, which is its DMA request.</summary>
    public bool DmcNeedsSample => m_dmc.NeedsSample;
    /// <summary>Gets the CPU address the DMC's next sample byte comes from.</summary>
    public ushort DmcAddress => m_dmc.Address;

    /// <summary>Delivers the sample byte a DMC DMA fetched.</summary>
    /// <param name="value">The byte read from <see cref="DmcAddress"/>.</param>
    public void CompleteDmcFetch(byte value) =>
        m_dmc.CompleteSampleFetch(value: value);
    /// <summary>Reads $4015: bits 0-3 report nonzero length counters, bit 4 an unfinished DMC sample, bit 6 the frame
    /// interrupt and bit 7 the DMC interrupt; bit 5 is open bus. The read clears the frame interrupt.</summary>
    /// <param name="openBus">The CPU data bus before the read.</param>
    /// <returns>The status byte.</returns>
    public byte ReadStatus(byte openBus) {
        var status = PeekStatus(openBus: openBus);

        m_frameIrq = false;

        return status;
    }
    /// <summary>Returns what a $4015 read would, changing nothing.</summary>
    /// <param name="openBus">The CPU data bus before the read.</param>
    /// <returns>The status byte.</returns>
    public byte PeekStatus(byte openBus) =>
        ((byte)(
            ((m_pulseOne.Length != 0) ? 0x01 : 0) |
            ((m_pulseTwo.Length != 0) ? 0x02 : 0) |
            ((m_triangle.Length != 0) ? 0x04 : 0) |
            ((m_noise.Length != 0) ? 0x08 : 0) |
            ((m_dmc.BytesRemaining != 0) ? 0x10 : 0) |
            (openBus & 0x20) |
            (m_frameIrq ? 0x40 : 0) |
            (m_dmc.Irq ? 0x80 : 0)
        ));
    /// <summary>Writes an APU register: $4000-$4013, $4015, or $4017.</summary>
    /// <param name="address">The CPU address.</param>
    /// <param name="value">The data byte.</param>
    public void WriteRegister(ushort address, byte value) {
        switch (address) {
            case 0x4000:
                WritePulseControl(pulse: ref m_pulseOne, value: value);

                break;
            case 0x4001:
                WritePulseSweep(pulse: ref m_pulseOne, value: value);

                break;
            case 0x4002:
                m_pulseOne.Period = ((ushort)((m_pulseOne.Period & 0x700) | value));

                break;
            case 0x4003:
                WritePulseHigh(enabled: m_pulseOneEnabled, pulse: ref m_pulseOne, value: value);

                break;
            case 0x4004:
                WritePulseControl(pulse: ref m_pulseTwo, value: value);

                break;
            case 0x4005:
                WritePulseSweep(pulse: ref m_pulseTwo, value: value);

                break;
            case 0x4006:
                m_pulseTwo.Period = ((ushort)((m_pulseTwo.Period & 0x700) | value));

                break;
            case 0x4007:
                WritePulseHigh(enabled: m_pulseTwoEnabled, pulse: ref m_pulseTwo, value: value);

                break;
            case 0x4008:
                m_triangle.Control = value;

                break;
            case 0x400A:
                m_triangle.Period = ((ushort)((m_triangle.Period & 0x700) | value));

                break;
            case 0x400B:
                m_triangle.Period = ((ushort)((m_triangle.Period & 0xFF) | ((value & 7) << 8)));
                if (m_triangleEnabled) {
                    m_triangle.Length = LengthTable[(value >> 3)];
                }
                m_triangle.LinearReload = true;

                break;
            case 0x400C:
                m_noise.Envelope.Register = value;

                break;
            case 0x400E:
                m_noise.ShortMode = ((value & 0x80) != 0);
                m_noise.Period = NoisePeriods[value & 15];

                break;
            case 0x400F:
                if (m_noiseEnabled) {
                    m_noise.Length = LengthTable[(value >> 3)];
                }
                m_noise.Envelope.Start = true;

                break;
            case 0x4010:
                m_dmc.Control = value;
                m_dmc.Period = DmcPeriods[value & 15];
                if ((value & 0x80) == 0) {
                    m_dmc.Irq = false;
                }

                break;
            case 0x4011:
                m_dmc.Level = ((byte)(value & 0x7F));

                break;
            case 0x4012:
                m_dmc.StartAddress = ((ushort)(0xC000 | (value << 6)));

                break;
            case 0x4013:
                m_dmc.StartLength = ((ushort)((value << 4) | 1));

                break;
            case 0x4015:
                WriteEnable(value: value);

                break;
            case 0x4017:
                WriteFrameCounter(value: value);

                break;
        }
    }
    /// <summary>Advances one CPU cycle: the triangle, noise and DMC timers every cycle, the pulse timers every other
    /// cycle, and the frame counter.</summary>
    public void StepCpuCycle() {
        m_triangle.ClockTimer();
        m_noise.ClockTimer();
        m_dmc.ClockTimer();
        if (!m_evenCycle) {
            m_pulseOne.ClockTimer();
            m_pulseTwo.ClockTimer();
        }
        m_evenCycle = !m_evenCycle;
        StepFrameCounter();
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain a complete APU state.</exception>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));

    private static void WritePulseControl(ref Pulse pulse, byte value) {
        pulse.Duty = ((byte)(value >> 6));
        pulse.Envelope.Register = value;
    }
    private static void WritePulseSweep(ref Pulse pulse, byte value) {
        pulse.SweepRegister = value;
        pulse.SweepReload = true;
    }
    private static void WritePulseHigh(ref Pulse pulse, byte value, bool enabled) {
        pulse.Period = ((ushort)((pulse.Period & 0xFF) | ((value & 7) << 8)));
        if (enabled) {
            pulse.Length = LengthTable[(value >> 3)];
        }
        pulse.Step = 0;
        pulse.Envelope.Start = true;
    }
    private void WriteEnable(byte value) {
        m_pulseOneEnabled = ((value & 1) != 0);
        m_pulseTwoEnabled = ((value & 2) != 0);
        m_triangleEnabled = ((value & 4) != 0);
        m_noiseEnabled = ((value & 8) != 0);
        if (!m_pulseOneEnabled) {
            m_pulseOne.Length = 0;
        }
        if (!m_pulseTwoEnabled) {
            m_pulseTwo.Length = 0;
        }
        if (!m_triangleEnabled) {
            m_triangle.Length = 0;
        }
        if (!m_noiseEnabled) {
            m_noise.Length = 0;
        }
        if ((value & 0x10) == 0) {
            m_dmc.BytesRemaining = 0;
        } else if (m_dmc.BytesRemaining == 0) {
            m_dmc.Restart();
        }
        m_dmc.Irq = false;
    }
    // A $4017 write resets the sequence three CPU cycles later when it lands on an even cycle and four when it lands on
    // an odd one; five-step mode also clocks the quarter- and half-frame units at once.
    // https://www.nesdev.org/wiki/APU_Frame_Counter
    private void WriteFrameCounter(byte value) {
        m_fiveStep = ((value & 0x80) != 0);
        m_frameIrqInhibit = ((value & 0x40) != 0);
        if (m_frameIrqInhibit) {
            m_frameIrq = false;
        }
        m_frameResetDelay = (m_evenCycle ? 3 : 4);
        if (m_fiveStep) {
            ClockQuarterFrame();
            ClockHalfFrame();
        }
    }
    private void StepFrameCounter() {
        if (m_frameResetDelay != 0) {
            --m_frameResetDelay;
            if (m_frameResetDelay == 0) {
                m_frameCycle = 0;

                return;
            }
        }
        ++m_frameCycle;
        if (m_fiveStep) {
            switch (m_frameCycle) {
                case 7457:
                case 22371:
                    ClockQuarterFrame();

                    break;
                case 14913:
                case 37281:
                    ClockQuarterFrame();
                    ClockHalfFrame();

                    break;
                case 37282:
                    m_frameCycle = 0;

                    break;
            }

            return;
        }
        switch (m_frameCycle) {
            case 7457:
            case 22371:
                ClockQuarterFrame();

                break;
            case 14913:
                ClockQuarterFrame();
                ClockHalfFrame();

                break;
            case 29828:
                RaiseFrameIrq();

                break;
            case 29829:
                RaiseFrameIrq();
                ClockQuarterFrame();
                ClockHalfFrame();

                break;
            case 29830:
                RaiseFrameIrq();
                m_frameCycle = 0;

                break;
        }
    }
    private void RaiseFrameIrq() {
        if (!m_frameIrqInhibit) {
            m_frameIrq = true;
        }
    }
    private void ClockQuarterFrame() {
        m_pulseOne.Envelope.Clock();
        m_pulseTwo.Envelope.Clock();
        m_noise.Envelope.Clock();
        m_triangle.ClockLinear();
    }
    private void ClockHalfFrame() {
        m_pulseOne.ClockLength();
        m_pulseTwo.ClockLength();
        m_triangle.ClockLength();
        m_noise.ClockLength();
        m_pulseOne.ClockSweep(onesComplement: true);
        m_pulseTwo.ClockSweep(onesComplement: false);
    }
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        m_pulseOne.TransferState(transfer: transfer);
        m_pulseTwo.TransferState(transfer: transfer);
        m_triangle.TransferState(transfer: transfer);
        m_noise.TransferState(transfer: transfer);
        m_dmc.TransferState(transfer: transfer);
        transfer.Boolean(value: ref m_evenCycle);
        transfer.Boolean(value: ref m_fiveStep);
        transfer.Boolean(value: ref m_frameIrqInhibit);
        transfer.Boolean(value: ref m_frameIrq);
        transfer.Int32(value: ref m_frameCycle);
        transfer.Int32(value: ref m_frameResetDelay);
        transfer.Boolean(value: ref m_pulseOneEnabled);
        transfer.Boolean(value: ref m_pulseTwoEnabled);
        transfer.Boolean(value: ref m_triangleEnabled);
        transfer.Boolean(value: ref m_noiseEnabled);
    }
}
