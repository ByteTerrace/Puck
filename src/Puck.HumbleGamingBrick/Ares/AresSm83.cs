namespace Puck.HumbleGamingBrick.Ares;

/// <summary>
/// The Sharp SM83 CPU core, ported faithfully from ares (<c>component/processor/sm83</c>). The abstract memory and
/// timing hooks (<c>Read</c>/<c>Write</c>/<c>Step</c>/etc.) are supplied by the Game Boy CPU,
/// which interleaves a step between each sub-cycle of every access — the timing model that makes the
/// machine cycle-accurate. This file ports the register file, the ALU algorithms, and the memory primitives; the
/// instruction implementations and decode tables live in <c>AresSm83.Instructions.cs</c>.
/// </summary>
public abstract partial class AresSm83 {
    // Memory and timing hooks implemented by the Game Boy CPU.
    protected abstract bool Stoppable();
    protected abstract void Stop();
    protected abstract void Halt();
    protected abstract void Idle();
    protected abstract byte Read(ushort address);
    protected abstract void Write(ushort address, byte data);
    protected abstract void HaltBugTrigger();

    // The register file. The 8-bit registers are fields so instructions can take them by reference (the SM83's
    // n8& operands). The flag register F holds Z (bit 7), N (6), H (5), C (4) in its high nibble.
    protected byte A;
    protected byte F;
    protected byte B;
    protected byte C;
    protected byte D;
    protected byte E;
    protected byte H;
    protected byte L;
    protected ushort SP;
    protected ushort PC;

    // The 16-bit register pairs, composed from the byte fields.
    protected ushort AF {
        get => (ushort)((A << 8) | F);
        set {
            A = (byte)(value >> 8);
            F = (byte)(value & 0xF0);
        }
    }
    protected ushort BC {
        get => (ushort)((B << 8) | C);
        set {
            B = (byte)(value >> 8);
            C = (byte)value;
        }
    }
    protected ushort DE {
        get => (ushort)((D << 8) | E);
        set {
            D = (byte)(value >> 8);
            E = (byte)value;
        }
    }
    protected ushort HL {
        get => (ushort)((H << 8) | L);
        set {
            H = (byte)(value >> 8);
            L = (byte)value;
        }
    }

    // Flag accessors over the F register bits.
    protected bool ZF {
        get => (F & 0x80) != 0;
        set => F = (byte)(value ? (F | 0x80) : (F & ~0x80));
    }
    protected bool NF {
        get => (F & 0x40) != 0;
        set => F = (byte)(value ? (F | 0x40) : (F & ~0x40));
    }
    protected bool HF {
        get => (F & 0x20) != 0;
        set => F = (byte)(value ? (F | 0x20) : (F & ~0x20));
    }
    protected bool CF {
        get => (F & 0x10) != 0;
        set => F = (byte)(value ? (F | 0x10) : (F & ~0x10));
    }

    // Interrupt-enable delay (EI), halt, stop, IME, and the HALT-bug latch.
    protected bool RegisterEi;
    protected bool RegisterHalt;
    protected bool RegisterStop;
    protected bool RegisterIme;
    protected bool RegisterHaltBug;

    // === Memory primitives (sm83/memory.cpp). Each Read/Write is one machine cycle that the CPU spreads across
    // five sub-cycle bus calls with a Step between each. ===

    protected byte Operand() {
        if (RegisterHaltBug) {
            RegisterHaltBug = false;

            return Read(address: PC);
        }

        return Read(address: PC++);
    }

    protected ushort Operands() {
        var data = (ushort)Operand();

        return (ushort)(data | (Operand() << 8));
    }

    protected ushort Load(ushort address) {
        var data = (ushort)Read(address: address++);

        return (ushort)(data | (Read(address: address) << 8));
    }

    protected void Store(ushort address, ushort data) {
        Write(address: address++, data: (byte)data);
        Write(address: address, data: (byte)(data >> 8));
    }

    protected ushort Pop() {
        var data = (ushort)Read(address: SP++);

        return (ushort)(data | (Read(address: SP++) << 8));
    }

    protected void Push(ushort data) {
        Write(address: --SP, data: (byte)(data >> 8));
        Write(address: --SP, data: (byte)data);
    }

    // === ALU algorithms (sm83/algorithms.cpp). ===

    protected byte Add(byte target, byte source, bool carry = false) {
        var x = (target + source + (carry ? 1 : 0));
        var y = ((target & 0x0F) + (source & 0x0F) + (carry ? 1 : 0));

        CF = (x > 0xFF);
        HF = (y > 0x0F);
        NF = false;
        ZF = ((byte)x == 0);

        return (byte)x;
    }

    protected byte And(byte target, byte source) {
        target &= source;
        CF = false;
        HF = true;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected void Bit(int index, byte target) {
        HF = true;
        NF = false;
        ZF = (((target >> index) & 1) == 0);
    }

    protected void Cp(byte target, byte source) {
        var x = (target - source);
        var y = ((target & 0x0F) - (source & 0x0F));

        CF = (x < 0);
        HF = (y < 0);
        NF = true;
        ZF = ((byte)x == 0);
    }

    protected byte Dec(byte target) {
        target--;
        HF = ((target & 0x0F) == 0x0F);
        NF = true;
        ZF = (target == 0);

        return target;
    }

    protected byte Inc(byte target) {
        target++;
        HF = ((target & 0x0F) == 0x00);
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Or(byte target, byte source) {
        target |= source;
        CF = false;
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Rl(byte target) {
        var carry = ((target & 0x80) != 0);

        target = (byte)((target << 1) | (CF ? 1 : 0));
        CF = carry;
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Rlc(byte target) {
        target = (byte)((target << 1) | (target >> 7));
        CF = ((target & 0x01) != 0);
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Rr(byte target) {
        var carry = ((target & 0x01) != 0);

        target = (byte)(((CF ? 1 : 0) << 7) | (target >> 1));
        CF = carry;
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Rrc(byte target) {
        target = (byte)((target << 7) | (target >> 1));
        CF = ((target & 0x80) != 0);
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Sla(byte target) {
        var carry = ((target & 0x80) != 0);

        target <<= 1;
        CF = carry;
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Sra(byte target) {
        var carry = ((target & 0x01) != 0);

        target = (byte)((sbyte)target >> 1);
        CF = carry;
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Srl(byte target) {
        var carry = ((target & 0x01) != 0);

        target >>= 1;
        CF = carry;
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Sub(byte target, byte source, bool carry = false) {
        var x = (target - source - (carry ? 1 : 0));
        var y = ((target & 0x0F) - (source & 0x0F) - (carry ? 1 : 0));

        CF = (x < 0);
        HF = (y < 0);
        NF = true;
        ZF = ((byte)x == 0);

        return (byte)x;
    }

    protected byte Swap(byte target) {
        target = (byte)((target << 4) | (target >> 4));
        CF = false;
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }

    protected byte Xor(byte target, byte source) {
        target ^= source;
        CF = false;
        HF = false;
        NF = false;
        ZF = (target == 0);

        return target;
    }
}
