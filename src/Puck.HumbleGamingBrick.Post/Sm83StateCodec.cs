using Puck.Snapshots;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Reads and writes <see cref="Sm83"/>'s full internal state through its EXISTING <see cref="ISnapshotable"/> seam — no
/// new core API. Mirrors <c>Sm83.SaveState</c>'s exact field order (A,F,B,C,D,E,H,L,SP,PC,halted,haltBug,lockedUp,IME,
/// interruptEnableCountdown = 20 bytes), so register-only <c>ICpu</c> members and the fields <c>ICpu</c> does not
/// expose (IME, halted, the EI-delay countdown) can both be set and read back by round-tripping a hand-built buffer
/// through <c>LoadState</c>/<c>SaveState</c>. Shared by the SST harness (seeding a vector's initial state) and the BESS
/// importer (restoring IME/execution-state from a savestate).
/// </summary>
internal static class Sm83StateCodec {
    /// <summary>The serialized byte count (<c>Sm83.SaveState</c>'s exact output length).</summary>
    public const int ByteCount = 20;

    private const int HaltedOffset = 12;
    private const int ImeOffset = 15;
    private const int InterruptEnableCountdownOffset = 16;

    /// <summary>Writes a full internal state into the CPU.</summary>
    /// <param name="cpu">The CPU to load.</param>
    /// <param name="scratch">A reusable writer (its contents are discarded).</param>
    /// <param name="a">The accumulator.</param>
    /// <param name="f">The flags register.</param>
    /// <param name="b">The B register.</param>
    /// <param name="c">The C register.</param>
    /// <param name="d">The D register.</param>
    /// <param name="e">The E register.</param>
    /// <param name="h">The H register.</param>
    /// <param name="l">The L register.</param>
    /// <param name="sp">The stack pointer.</param>
    /// <param name="pc">The program counter.</param>
    /// <param name="halted">Whether the CPU is parked in HALT.</param>
    /// <param name="haltBug">Whether the next fetch is affected by the HALT bug.</param>
    /// <param name="lockedUp">Whether the CPU is wedged on an illegal opcode.</param>
    /// <param name="ime">The interrupt-master-enable flag.</param>
    /// <param name="interruptEnableCountdown">The EI-delay countdown (0 = none pending).</param>
    public static void Load(Sm83 cpu, StateWriter scratch, byte a, byte f, byte b, byte c, byte d, byte e, byte h, byte l, ushort sp, ushort pc, bool halted, bool haltBug, bool lockedUp, bool ime, int interruptEnableCountdown) {
        scratch.Reset();
        scratch.WriteByte(value: a);
        scratch.WriteByte(value: f);
        scratch.WriteByte(value: b);
        scratch.WriteByte(value: c);
        scratch.WriteByte(value: d);
        scratch.WriteByte(value: e);
        scratch.WriteByte(value: h);
        scratch.WriteByte(value: l);
        scratch.WriteUInt16(value: sp);
        scratch.WriteUInt16(value: pc);
        scratch.WriteBoolean(value: halted);
        scratch.WriteBoolean(value: haltBug);
        scratch.WriteBoolean(value: lockedUp);
        scratch.WriteBoolean(value: ime);
        scratch.WriteInt32(value: interruptEnableCountdown);

        cpu.LoadState(reader: scratch.OpenReader());
    }
    /// <summary>Reads back the fields <c>ICpu</c> does not expose.</summary>
    /// <param name="cpu">The CPU to read.</param>
    /// <param name="scratch">A reusable writer (its contents are discarded).</param>
    /// <param name="buffer">A reusable <see cref="ByteCount"/>-byte scratch buffer.</param>
    /// <param name="halted">Receives whether the CPU is parked in HALT.</param>
    /// <param name="ime">Receives the interrupt-master-enable flag.</param>
    /// <param name="eiPending">Receives whether an EI-armed enable is still pending.</param>
    public static void ReadTail(Sm83 cpu, StateWriter scratch, byte[] buffer, out bool halted, out bool ime, out bool eiPending) {
        scratch.Reset();
        cpu.SaveState(writer: scratch);
        scratch.OpenReader().ReadBytes(destination: buffer);

        halted = (buffer[HaltedOffset] != 0);
        ime = (buffer[ImeOffset] != 0);
        eiPending = (BitConverter.ToInt32(value: buffer, startIndex: InterruptEnableCountdownOffset) != 0);
    }
}
