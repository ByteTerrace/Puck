namespace Puck.Abstractions.Machines;

/// <summary>
/// An OPTIONAL capability of an <see cref="IScreenMachine"/> — a debug window onto the machine's whole bus address
/// space. A machine advertises it by also implementing this interface; a host tests for it
/// (<c>machine is IMachineMemoryPeek</c>) before using it and reports its absence loudly rather than assuming it. The
/// address space is machine-defined and covers the CPU's entire view (an SM83 brick's <c>0x0000</c>-<c>0xFFFF</c>: ROM,
/// VRAM, external/work/high RAM, and the I/O page), not merely work RAM; a byte outside the machine's readable space
/// reads as 0.
/// <para>
/// <see cref="PeekByte"/> is side-effect-free — never a write into machine state, and never a read that advances a
/// time-dependent component — so it makes emulated state assertable without perturbing it. <see cref="PokeByte"/> is
/// the debug MUTATION counterpart: it forces a byte into the machine outside the replay-determinism contract, so a host
/// that pokes must drop any captured replay/rewind history for that machine (the poked byte is an unrecorded input the
/// deterministic timeline cannot reconstruct).
/// </para>
/// </summary>
public interface IMachineMemoryPeek {
    /// <summary>Reads one byte from anywhere in the machine's bus address space, without side effects. Returns 0 for an
    /// address outside the machine's readable space or an unassigned machine.</summary>
    /// <param name="address">A machine-defined bus address.</param>
    /// <returns>The byte at that address, or 0.</returns>
    byte PeekByte(int address);

    /// <summary>Forces one byte into the machine's bus address space — a DEBUG mutation outside the replay-determinism
    /// contract. The write lands only on writable regions (RAM); an address outside the machine's writable space, or an
    /// unassigned machine, is a no-op. Because the poked byte is an unrecorded input, the caller must treat any captured
    /// replay/rewind history for this machine as invalidated.</summary>
    /// <param name="address">A machine-defined bus address.</param>
    /// <param name="value">The byte to store.</param>
    void PokeByte(int address, byte value);
}
