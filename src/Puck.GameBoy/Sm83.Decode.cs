namespace Puck.GameBoy;

// The instruction decode, structured by the SM83 opcode bit-fields rather than a flat 256-way switch:
// x = bits 7-6 (quadrant), y = bits 5-3, z = bits 2-0, with p = y >> 1 and q = y &amp; 1. This mirrors the
// hardware's own decoding and keeps each method small. Instruction timing is produced by the count and order
// of cycle-accessor calls — an operand resolving to (HL), or an instruction's internal cycles — not a table.
public sealed partial class Sm83 {
    private void Execute(byte opcode) {
        var x = (opcode >> 6);
        var y = ((opcode >> 3) & 7);
        var z = (opcode & 7);

        switch (x) {
            case 0:
                ExecuteQuadrant0(
                    y: y,
                    z: z
                );

                break;
            case 1:
                // LD r,r' across the whole quadrant, except the (HL),(HL) encoding which is HALT.
                if ((y == 6) && (z == 6)) {
                    ExecuteHalt();
                }
                else {
                    WriteOperand(
                        index: y,
                        value: ReadOperand(index: z)
                    );
                }

                break;
            case 2:
                AluOperation(
                    operation: y,
                    value: ReadOperand(index: z)
                );

                break;
            default:
                ExecuteQuadrant3(
                    y: y,
                    z: z
                );

                break;
        }
    }

    private void ExecuteQuadrant0(int y, int z) {
        switch (z) {
            case 0:
                ExecuteQuadrant0Control(y: y);

                break;
            case 1: {
                // LD rr,nn (q=0) or ADD HL,rr (q=1).
                var pair = (y >> 1);

                if ((y & 1) == 0) {
                    WriteRegisterPair(
                        pair: pair,
                        value: ReadImmediate16()
                    );
                }
                else {
                    m_bus.InternalCycle();
                    AddToHl(value: ReadRegisterPair(pair: pair));
                }

                break;
            }
            case 2:
                ExecuteLoadAccumulatorIndirect(p: (y >> 1), q: (y & 1));

                break;
            case 3: {
                // INC rr (q=0) or DEC rr (q=1).
                var pair = (y >> 1);

                m_bus.InternalCycle();
                WriteRegisterPair(
                    pair: pair,
                    value: (ushort)(ReadRegisterPair(pair: pair) + (((y & 1) == 0) ? 1 : -1))
                );

                break;
            }
            case 4:
                WriteOperand(
                    index: y,
                    value: Increment8(value: ReadOperand(index: y))
                );

                break;
            case 5:
                WriteOperand(
                    index: y,
                    value: Decrement8(value: ReadOperand(index: y))
                );

                break;
            case 6:
                WriteOperand(
                    index: y,
                    value: ReadImmediate8()
                );

                break;
            default:
                ExecuteAccumulatorMisc(y: y);

                break;
        }
    }

    private void ExecuteQuadrant0Control(int y) {
        switch (y) {
            case 0:
                break;
            case 1: {
                // LD (nn),SP
                var address = ReadImmediate16();

                m_bus.WriteCycle(
                    address: address,
                    value: (byte)m_stackPointer
                );
                m_bus.WriteCycle(
                    address: (ushort)(address + 1),
                    value: (byte)(m_stackPointer >> 8)
                );

                break;
            }
            case 2:
                ExecuteStop();

                break;
            case 3:
                JumpRelative(condition: true);

                break;
            default:
                // y=4..7: JR NZ/Z/NC/C
                JumpRelative(condition: ConditionMet(condition: (y - 4)));

                break;
        }
    }

    private void ExecuteLoadAccumulatorIndirect(int p, int q) {
        var address = p switch {
            0 => BC,
            1 => DE,
            _ => HL,
        };

        if (q == 0) {
            m_bus.WriteCycle(
                address: address,
                value: m_a
            );
        }
        else {
            m_a = m_bus.ReadCycle(address: address);
        }

        // p=2/3 are the post-increment/decrement HL forms; the pointer modify happens after the access.
        if (p == 2) {
            HL += 1;
        }
        else if (p == 3) {
            HL -= 1;
        }
    }

    private void ExecuteAccumulatorMisc(int y) {
        switch (y) {
            case 0:
                m_a = RotateLeftCircular(value: m_a);
                ClearRotateFlagsForAccumulator();

                break;
            case 1:
                m_a = RotateRightCircular(value: m_a);
                ClearRotateFlagsForAccumulator();

                break;
            case 2:
                m_a = RotateLeftThroughCarry(value: m_a);
                ClearRotateFlagsForAccumulator();

                break;
            case 3:
                m_a = RotateRightThroughCarry(value: m_a);
                ClearRotateFlagsForAccumulator();

                break;
            case 4:
                DecimalAdjustAccumulator();

                break;
            case 5:
                m_a = (byte)~m_a;
                m_flagSubtract = true;
                m_flagHalfCarry = true;

                break;
            case 6:
                m_flagCarry = true;
                m_flagSubtract = false;
                m_flagHalfCarry = false;

                break;
            default:
                m_flagCarry = !m_flagCarry;
                m_flagSubtract = false;
                m_flagHalfCarry = false;

                break;
        }
    }

    private void ExecuteQuadrant3(int y, int z) {
        switch (z) {
            case 0:
                ExecuteQuadrant3Column0(y: y);

                break;
            case 1:
                ExecuteQuadrant3Column1(p: (y >> 1), q: (y & 1));

                break;
            case 2:
                ExecuteQuadrant3Column2(y: y);

                break;
            case 3:
                ExecuteQuadrant3Column3(y: y);

                break;
            case 4:
                // CALL cc,nn (y<4); the remaining encodings are illegal.
                if (y < 4) {
                    Call(condition: ConditionMet(condition: y));
                }
                else {
                    m_stopped = true;
                }

                break;
            case 5:
                ExecuteQuadrant3Column5(p: (y >> 1), q: (y & 1));

                break;
            case 6:
                AluOperation(
                    operation: y,
                    value: ReadImmediate8()
                );

                break;
            default:
                // RST: vector = y * 8.
                m_bus.InternalCycle();
                Push16(value: m_programCounter);
                m_programCounter = (ushort)(y << 3);

                break;
        }
    }

    private void ExecuteQuadrant3Column0(int y) {
        switch (y) {
            case 0:
            case 1:
            case 2:
            case 3:
                ReturnConditional(condition: ConditionMet(condition: y));

                break;
            case 4:
                // LDH (n),A
                m_bus.WriteCycle(
                    address: (ushort)(0xFF00 + ReadImmediate8()),
                    value: m_a
                );

                break;
            case 5: {
                // ADD SP,e
                var offset = (sbyte)ReadImmediate8();

                m_bus.InternalCycle();
                m_bus.InternalCycle();
                m_stackPointer = AddSignedToStackPointer(offset: offset);

                break;
            }
            case 6:
                // LDH A,(n)
                m_a = m_bus.ReadCycle(address: (ushort)(0xFF00 + ReadImmediate8()));

                break;
            default: {
                // LD HL,SP+e
                var offset = (sbyte)ReadImmediate8();

                m_bus.InternalCycle();
                HL = AddSignedToStackPointer(offset: offset);

                break;
            }
        }
    }

    private void ExecuteQuadrant3Column1(int p, int q) {
        if (q == 0) {
            // POP rr (the AF-bearing table; the F low nibble is dropped by the F setter).
            WriteStackPair(
                pair: p,
                value: Pop16()
            );

            return;
        }

        switch (p) {
            case 0: {
                // RET
                var address = Pop16();

                m_bus.InternalCycle();
                m_programCounter = address;

                break;
            }
            case 1: {
                // RETI re-enables interrupts immediately, with no one-instruction delay.
                var address = Pop16();

                m_bus.InternalCycle();
                m_programCounter = address;
                m_interruptMasterEnable = true;
                m_interruptEnableDelay = 0;

                break;
            }
            case 2:
                // JP HL has no internal cycle.
                m_programCounter = HL;

                break;
            default:
                // LD SP,HL
                m_bus.InternalCycle();
                m_stackPointer = HL;

                break;
        }
    }

    private void ExecuteQuadrant3Column2(int y) {
        switch (y) {
            case 0:
            case 1:
            case 2:
            case 3:
                JumpAbsolute(condition: ConditionMet(condition: y));

                break;
            case 4:
                // LD (C),A
                m_bus.WriteCycle(
                    address: (ushort)(0xFF00 + m_c),
                    value: m_a
                );

                break;
            case 5: {
                // LD (nn),A
                var address = ReadImmediate16();

                m_bus.WriteCycle(
                    address: address,
                    value: m_a
                );

                break;
            }
            case 6:
                // LD A,(C)
                m_a = m_bus.ReadCycle(address: (ushort)(0xFF00 + m_c));

                break;
            default: {
                // LD A,(nn)
                var address = ReadImmediate16();

                m_a = m_bus.ReadCycle(address: address);

                break;
            }
        }
    }

    private void ExecuteQuadrant3Column3(int y) {
        switch (y) {
            case 0:
                JumpAbsolute(condition: true);

                break;
            case 1:
                ExecuteCb(opcode: ReadImmediate8());

                break;
            case 6:
                // DI
                m_interruptMasterEnable = false;
                m_interruptEnableDelay = 0;

                break;
            case 7:
                // EI enables interrupts after the following instruction; the Step loop counts the delay down. A
                // back-to-back EI must not restart an enable already in flight (nor matter once IME is set), or a
                // run of EIs would perpetually re-arm the delay and never actually enable.
                if ((m_interruptEnableDelay == 0) && !m_interruptMasterEnable) {
                    m_interruptEnableDelay = 2;
                }

                break;
            default:
                // y=2..5: illegal opcodes (0xD3, 0xDB, 0xE3, 0xEB) hang the real CPU.
                m_stopped = true;

                break;
        }
    }

    private void ExecuteQuadrant3Column5(int p, int q) {
        if (q == 0) {
            // PUSH rr (one internal cycle, then two writes).
            m_bus.InternalCycle();
            Push16(value: ReadStackPair(pair: p));

            return;
        }

        if (p == 0) {
            // CALL nn
            Call(condition: true);
        }
        else {
            // 0xDD, 0xED, 0xFD: illegal.
            m_stopped = true;
        }
    }

    private void ExecuteCb(byte opcode) {
        var operation = ((opcode >> 3) & 0x1F);
        var index = (opcode & 7);
        var value = ReadOperand(index: index);

        if (operation < 8) {
            var result = operation switch {
                0 => RotateLeftCircular(value: value),
                1 => RotateRightCircular(value: value),
                2 => RotateLeftThroughCarry(value: value),
                3 => RotateRightThroughCarry(value: value),
                4 => ShiftLeftArithmetic(value: value),
                5 => ShiftRightArithmetic(value: value),
                6 => Swap(value: value),
                _ => ShiftRightLogical(value: value),
            };

            m_flagZero = (result == 0);
            m_flagSubtract = false;
            m_flagHalfCarry = false;
            WriteOperand(
                index: index,
                value: result
            );
        }
        else if (operation < 16) {
            // BIT b,r: no write-back, carry preserved; (HL) costs only the read above.
            m_flagZero = ((value & (1 << (operation - 8))) == 0);
            m_flagSubtract = false;
            m_flagHalfCarry = true;
        }
        else if (operation < 24) {
            // RES b,r
            WriteOperand(
                index: index,
                value: (byte)(value & ~(1 << (operation - 16)))
            );
        }
        else {
            // SET b,r
            WriteOperand(
                index: index,
                value: (byte)(value | (1 << (operation - 24)))
            );
        }
    }

    private void AluOperation(int operation, byte value) {
        switch (operation) {
            case 0:
                AddToAccumulator(value: value, withCarry: false);

                break;
            case 1:
                AddToAccumulator(value: value, withCarry: true);

                break;
            case 2:
                SubtractFromAccumulator(value: value, withCarry: false);

                break;
            case 3:
                SubtractFromAccumulator(value: value, withCarry: true);

                break;
            case 4:
                AndWithAccumulator(value: value);

                break;
            case 5:
                XorWithAccumulator(value: value);

                break;
            case 6:
                OrWithAccumulator(value: value);

                break;
            default:
                CompareWithAccumulator(value: value);

                break;
        }
    }

    private bool ConditionMet(int condition) =>
        condition switch {
            0 => !m_flagZero,
            1 => m_flagZero,
            2 => !m_flagCarry,
            _ => m_flagCarry,
        };

    private ushort ReadRegisterPair(int pair) =>
        pair switch {
            0 => BC,
            1 => DE,
            2 => HL,
            _ => m_stackPointer,
        };
    private void WriteRegisterPair(int pair, ushort value) {
        switch (pair) {
            case 0:
                BC = value;

                break;
            case 1:
                DE = value;

                break;
            case 2:
                HL = value;

                break;
            default:
                m_stackPointer = value;

                break;
        }
    }
    private ushort ReadStackPair(int pair) =>
        pair switch {
            0 => BC,
            1 => DE,
            2 => HL,
            _ => AF,
        };
    private void WriteStackPair(int pair, ushort value) {
        switch (pair) {
            case 0:
                BC = value;

                break;
            case 1:
                DE = value;

                break;
            case 2:
                HL = value;

                break;
            default:
                AF = value;

                break;
        }
    }

    private void ClearRotateFlagsForAccumulator() {
        m_flagZero = false;
        m_flagSubtract = false;
        m_flagHalfCarry = false;
    }

    private void JumpRelative(bool condition) {
        var offset = (sbyte)ReadImmediate8();

        if (condition) {
            m_bus.InternalCycle();
            m_programCounter = (ushort)(m_programCounter + offset);
        }
    }
    private void JumpAbsolute(bool condition) {
        var address = ReadImmediate16();

        if (condition) {
            m_bus.InternalCycle();
            m_programCounter = address;
        }
    }
    private void Call(bool condition) {
        var address = ReadImmediate16();

        if (condition) {
            m_bus.InternalCycle();
            Push16(value: m_programCounter);
            m_programCounter = address;
        }
    }
    private void ReturnConditional(bool condition) {
        // The condition is evaluated on an internal cycle even when the return is not taken.
        m_bus.InternalCycle();

        if (condition) {
            var address = Pop16();

            m_bus.InternalCycle();
            m_programCounter = address;
        }
    }

    private void ExecuteHalt() {
        if (m_interruptMasterEnable || !m_bus.Interrupts.HasPending) {
            m_halted = true;
        }
        else {
            // HALT with IME=0 and an interrupt already pending triggers the HALT bug instead of halting.
            m_haltBug = true;
        }
    }
    private void ExecuteStop() {
        // STOP is nominally a two-byte opcode; skip the padding byte. On the CGB a STOP with an armed KEY1
        // request performs the speed switch instead of stopping. The multi-thousand-cycle STOP stall is
        // approximated (not modeled) for now.
        m_programCounter += 1;

        if (!m_bus.ApplyPreparedSpeedSwitch()) {
            m_stopped = true;
        }
    }
}
