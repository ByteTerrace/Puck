namespace Puck.HumbleGamingDeck;

// The APU's tone generators. Every level they publish is an integer, the emulated contract; mixing them into sound is the
// host adapter's presentation work. https://www.nesdev.org/wiki/APU
public sealed partial class HgdApu {
    private static ReadOnlySpan<byte> LengthTable => [
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    ];
    private static ReadOnlySpan<byte> DutyTable => [
        0b01000000, 0b01100000, 0b01111000, 0b10011111,
    ];
    private static ReadOnlySpan<byte> TriangleSequence => [
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];
    private static ReadOnlySpan<ushort> NoisePeriods => [
        4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068,
    ];
    private static ReadOnlySpan<ushort> DmcPeriods => [
        428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54,
    ];

    // A volume envelope: constant volume, or a decay from 15 the quarter-frame clock steps, optionally looping.
    private struct Envelope {
        public byte Register;
        public bool Start;
        public byte Divider;
        public byte Decay;

        public readonly bool Loop => ((Register & 0x20) != 0);
        public readonly byte Volume => (((Register & 0x10) != 0) ? ((byte)(Register & 15)) : Decay);

        public void Clock() {
            if (Start) {
                Start = false;
                Decay = 15;
                Divider = ((byte)(Register & 15));

                return;
            }
            if (Divider != 0) {
                --Divider;

                return;
            }

            Divider = ((byte)(Register & 15));
            if (Decay != 0) {
                --Decay;
            } else if (Loop) {
                Decay = 15;
            }
        }
        public void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
            transfer.Byte(value: ref Register);
            transfer.Boolean(value: ref Start);
            transfer.Byte(value: ref Divider);
            transfer.Byte(value: ref Decay);
        }
    }
    private struct Pulse {
        public Envelope Envelope;
        public byte Duty;
        public byte Step;
        public ushort Period;
        public ushort Timer;
        public byte Length;
        public byte SweepRegister;
        public byte SweepDivider;
        public bool SweepReload;

        // The sweep's target period; pulse 1 negates with the ones' complement, pulse 2 with the two's complement.
        public readonly int Target(bool onesComplement) {
            var change = (Period >> (SweepRegister & 7));

            if ((SweepRegister & 0x08) == 0) {
                return (Period + change);
            }

            return ((Period - change) - (onesComplement ? 1 : 0));
        }
        public readonly bool Muted(bool onesComplement) =>
            ((Period < 8) || (Target(onesComplement: onesComplement) > 0x7FF));
        public readonly byte Output(bool onesComplement) =>
            (((Length == 0) || Muted(onesComplement: onesComplement) || (((DutyTable[Duty] >> (7 - Step)) & 1) == 0))
                ? ((byte)0)
                : Envelope.Volume);
        public void ClockTimer() {
            if (Timer == 0) {
                Timer = Period;
                Step = ((byte)((Step + 1) & 7));
            } else {
                --Timer;
            }
        }
        public void ClockSweep(bool onesComplement) {
            var target = Target(onesComplement: onesComplement);

            if ((SweepDivider == 0) && ((SweepRegister & 0x80) != 0) && ((SweepRegister & 7) != 0) && !Muted(onesComplement: onesComplement)) {
                Period = ((ushort)target);
            }
            if ((SweepDivider == 0) || SweepReload) {
                SweepDivider = ((byte)((SweepRegister >> 4) & 7));
                SweepReload = false;
            } else {
                --SweepDivider;
            }
        }
        public void ClockLength() {
            if ((Length != 0) && !Envelope.Loop) {
                --Length;
            }
        }
        public void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
            Envelope.TransferState(transfer: transfer);
            transfer.Byte(value: ref Duty);
            transfer.Byte(value: ref Step);
            transfer.UInt16(value: ref Period);
            transfer.UInt16(value: ref Timer);
            transfer.Byte(value: ref Length);
            transfer.Byte(value: ref SweepRegister);
            transfer.Byte(value: ref SweepDivider);
            transfer.Boolean(value: ref SweepReload);
        }
    }
    private struct Triangle {
        public byte Control;
        public byte LinearCounter;
        public bool LinearReload;
        public byte Step;
        public ushort Period;
        public ushort Timer;
        public byte Length;

        public readonly byte Output => TriangleSequence[Step];

        // The sequencer advances only while both counters are nonzero, and it holds its level when either stops, so the
        // triangle never clicks to zero when silenced.
        public void ClockTimer() {
            if (Timer == 0) {
                Timer = Period;
                if ((Length != 0) && (LinearCounter != 0)) {
                    Step = ((byte)((Step + 1) & 31));
                }
            } else {
                --Timer;
            }
        }
        public void ClockLinear() {
            if (LinearReload) {
                LinearCounter = ((byte)(Control & 0x7F));
            } else if (LinearCounter != 0) {
                --LinearCounter;
            }
            if ((Control & 0x80) == 0) {
                LinearReload = false;
            }
        }
        public void ClockLength() {
            if ((Length != 0) && ((Control & 0x80) == 0)) {
                --Length;
            }
        }
        public void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
            transfer.Byte(value: ref Control);
            transfer.Byte(value: ref LinearCounter);
            transfer.Boolean(value: ref LinearReload);
            transfer.Byte(value: ref Step);
            transfer.UInt16(value: ref Period);
            transfer.UInt16(value: ref Timer);
            transfer.Byte(value: ref Length);
        }
    }
    private struct Noise {
        public Envelope Envelope;
        public bool ShortMode;
        public ushort Period;
        public ushort Timer;
        public ushort Shift;
        public byte Length;

        public readonly byte Output =>
            (((Length == 0) || ((Shift & 1) != 0)) ? ((byte)0) : Envelope.Volume);

        public void ClockTimer() {
            if (Timer != 0) {
                --Timer;

                return;
            }

            Timer = ((ushort)(Period - 1));

            var feedback = (Shift ^ (Shift >> (ShortMode ? 6 : 1))) & 1;

            Shift = ((ushort)((Shift >> 1) | (feedback << 14)));
        }
        public void ClockLength() {
            if ((Length != 0) && !Envelope.Loop) {
                --Length;
            }
        }
        public void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
            Envelope.TransferState(transfer: transfer);
            transfer.Boolean(value: ref ShortMode);
            transfer.UInt16(value: ref Period);
            transfer.UInt16(value: ref Timer);
            transfer.UInt16(value: ref Shift);
            transfer.Byte(value: ref Length);
        }
    }
    // The delta-modulation channel. Its sample bytes arrive through DMA: it raises a request when its buffer is empty and
    // bytes remain, and the DMA unit answers with CompleteSampleFetch.
    private struct Dmc {
        public byte Control;
        public byte Level;
        public ushort StartAddress;
        public ushort StartLength;
        public ushort Address;
        public ushort BytesRemaining;
        public ushort Period;
        public ushort Timer;
        public byte Buffer;
        public bool BufferFull;
        public byte Shift;
        public byte BitsRemaining;
        public bool Silence;
        public bool Irq;

        public readonly bool NeedsSample => (!BufferFull && (BytesRemaining != 0));

        public void Restart() {
            Address = StartAddress;
            BytesRemaining = StartLength;
        }
        public void CompleteSampleFetch(byte value) {
            Buffer = value;
            BufferFull = true;
            Address = ((ushort)((Address == 0xFFFF) ? 0x8000 : (Address + 1)));
            --BytesRemaining;
            if (BytesRemaining != 0) {
                return;
            }
            if ((Control & 0x40) != 0) {
                Restart();
            } else if ((Control & 0x80) != 0) {
                Irq = true;
            }
        }
        public void ClockTimer() {
            if (Timer != 0) {
                --Timer;

                return;
            }

            Timer = ((ushort)(Period - 1));
            if (!Silence) {
                if ((Shift & 1) != 0) {
                    if (Level <= 125) {
                        Level += 2;
                    }
                } else if (Level >= 2) {
                    Level -= 2;
                }
            }
            Shift >>= 1;
            if (BitsRemaining != 0) {
                --BitsRemaining;
            }
            if (BitsRemaining != 0) {
                return;
            }

            BitsRemaining = 8;
            if (BufferFull) {
                Silence = false;
                Shift = Buffer;
                BufferFull = false;
            } else {
                Silence = true;
            }
        }
        public void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
            transfer.Byte(value: ref Control);
            transfer.Byte(value: ref Level);
            transfer.UInt16(value: ref StartAddress);
            transfer.UInt16(value: ref StartLength);
            transfer.UInt16(value: ref Address);
            transfer.UInt16(value: ref BytesRemaining);
            transfer.UInt16(value: ref Period);
            transfer.UInt16(value: ref Timer);
            transfer.Byte(value: ref Buffer);
            transfer.Boolean(value: ref BufferFull);
            transfer.Byte(value: ref Shift);
            transfer.Byte(value: ref BitsRemaining);
            transfer.Boolean(value: ref Silence);
            transfer.Boolean(value: ref Irq);
        }
    }
}
