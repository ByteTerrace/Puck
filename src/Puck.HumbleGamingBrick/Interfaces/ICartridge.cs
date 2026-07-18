namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The cartridge as the bus sees it: a read-only program in the ROM region, an optional save-RAM window, and a set of
/// mapper control registers written through the ROM region. Reads and writes are pre-decoded by region — the bus has
/// already classified the address — so each mapper only implements its banking. A cartridge's mutable state (its
/// registers and save RAM, never the immutable ROM) is part of every snapshot.
/// </summary>
public interface ICartridge : ISnapshotable {
    /// <summary>Gets the decoded header.</summary>
    CartridgeHeader Header { get; }
    /// <summary>Gets the populated external (save) RAM size in bytes; zero for a RAM-less cartridge.</summary>
    int ExternalRamByteCount { get; }
    /// <summary>Gets whether the external RAM changed since the last <see cref="MarkExternalRamClean"/> /
    /// <see cref="ImportExternalRam"/> — the host's battery-save flush trigger. Set by every RAM store; snapshot
    /// restores do NOT set it (a restore replicates a machine whose own writes already tracked).</summary>
    bool ExternalRamDirty { get; }

    /// <summary>Copies the external RAM out — the host's battery-save read. Does not change
    /// <see cref="ExternalRamDirty"/>; the host marks clean only once the copy is safely persisted.</summary>
    /// <returns>A copy of the external RAM (empty for a RAM-less cartridge).</returns>
    byte[] ExportExternalRam();
    /// <summary>Copies a slice of external RAM out by ABSOLUTE offset — bank-independent and side-effect-free (the dirty
    /// flag is untouched and no bank selection is disturbed), so a host poll never fights the running game. The
    /// win-condition / inspection read: the top 16 bytes of the highest SRAM bank start at
    /// <see cref="ExternalRamByteCount"/><c> - 16</c>. A slice reaching outside the populated RAM (or a RAM-less
    /// cartridge) yields zeroes for the missing bytes rather than throwing.</summary>
    /// <param name="offset">The absolute byte offset into save RAM (independent of the current bank selection).</param>
    /// <param name="destination">The span to fill; its length is the slice size.</param>
    void ReadExternalRam(int offset, Span<byte> destination);
    /// <summary>Copies a persisted battery save into the external RAM (a host-side power-on load, before the machine
    /// runs) and marks the RAM clean. Copies at most <see cref="ExternalRamByteCount"/> bytes; a shorter source fills
    /// the front (the rest keeps its power-on zeroes).</summary>
    /// <param name="source">The persisted save image.</param>
    void ImportExternalRam(ReadOnlySpan<byte> source);
    /// <summary>Marks the external RAM clean — the host acknowledges a completed flush of the bytes a preceding
    /// <see cref="ExportExternalRam"/> returned.</summary>
    void MarkExternalRamClean();
    /// <summary>Gets the byte length of this cartridge's persistent-clock footer in a battery save (the bytes a save
    /// file appends after the external RAM), or zero for a cartridge without battery-backed timed hardware.</summary>
    int PersistentClockByteCount { get; }
    /// <summary>Exports the persistent-clock footer (<see cref="PersistentClockByteCount"/> bytes; empty when zero).
    /// The cartridge owns the FORMAT; the host owns wall time — <paramref name="unixTimestampSeconds"/> is stamped
    /// into the footer purely for FOREIGN-emulator interop (tools that fast-forward a real-time clock by elapsed
    /// wall time). This machine's own <see cref="ImportPersistentClock"/> ignores it: the deterministic clock
    /// RESUMES where it stopped, never advancing from wall time.</summary>
    /// <param name="unixTimestampSeconds">The host's flush wall time as UNIX seconds (interop metadata only).</param>
    byte[] ExportPersistentClock(long unixTimestampSeconds);
    /// <summary>Imports a persisted clock footer (a host-side power-on load, before the machine runs): restores the
    /// clock registers so the clock RESUMES where the last flush left it. Any embedded wall timestamp is ignored —
    /// no wall time ever enters the machine, so time pauses while powered off and never goes backward. A source
    /// shorter than <see cref="PersistentClockByteCount"/> is ignored whole.</summary>
    /// <param name="source">The persisted clock footer.</param>
    void ImportPersistentClock(ReadOnlySpan<byte> source);

    /// <summary>Reads a byte from the ROM region, honoring the current bank selection.</summary>
    /// <param name="address">An address in <c>[0x0000, 0x7FFF]</c>.</param>
    /// <returns>The ROM byte.</returns>
    byte ReadRom(ushort address);
    /// <summary>Reads a byte from the external RAM window, or open-bus <c>0xFF</c> when RAM is absent or disabled.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The RAM byte or <c>0xFF</c>.</returns>
    byte ReadRam(ushort address);
    /// <summary>Writes to a mapper control register through the ROM region (the write does not alter ROM).</summary>
    /// <param name="address">An address in <c>[0x0000, 0x7FFF]</c>.</param>
    /// <param name="value">The value written.</param>
    void WriteControl(ushort address, byte value);
    /// <summary>Writes a byte to the external RAM window; ignored when RAM is absent or disabled.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <param name="value">The value to store.</param>
    void WriteRam(ushort address, byte value);

    /// <summary>Gets the immutable ROM image bank offsets resolve against — the SAME array <see cref="ReadRom"/>
    /// indexes, not a copy. The bus's derived ROM-window cache indexes directly into it once
    /// <see cref="ComputeRomWindows"/> has resolved the current bank offsets, bypassing <see cref="ReadRom"/> and the
    /// slot indirection on the hot fetch path.</summary>
    byte[] RomImage { get; }
    /// <summary>Computes the ROM byte offsets the fixed (<c>0x0000</c>) and switchable (<c>0x4000</c>) windows
    /// currently resolve to, honoring the same bank-wrap semantics as <see cref="ReadRom"/>. The bus calls this after
    /// any <see cref="WriteControl"/> that can move a bank, and after every point the machine already re-derives
    /// capability gates (a live model swap, a snapshot restore) — the resulting window is derived state, never
    /// serialized; a restore rebuilds it from the just-loaded mapper registers.</summary>
    /// <param name="bank0Offset">The absolute offset into <see cref="RomImage"/> the fixed <c>0x0000</c>-<c>0x3FFF</c>
    /// window currently reads from.</param>
    /// <param name="bankNOffset">The absolute offset into <see cref="RomImage"/> the switchable
    /// <c>0x4000</c>-<c>0x7FFF</c> window currently reads from.</param>
    void ComputeRomWindows(out int bank0Offset, out int bankNOffset);
    /// <summary>Gets the live external (save) RAM array bank offsets resolve against — the SAME array
    /// <see cref="ReadRam"/>/<see cref="WriteRam"/> mutate, not a copy.</summary>
    byte[] RamImage { get; }
    /// <summary>Attempts to compute the currently-banked external RAM window as a pure array offset, for a mapper whose
    /// <c>0xA000</c>-<c>0xBFFF</c> window is unconditional array access with no side effects and no mode routing.
    /// Mappers with a side-effectful or mode-selected window (an RTC register, an EEPROM/accelerometer protocol, an IR
    /// line, the camera cartridge's register block) override this to always return <see langword="false"/>, keeping their
    /// window on the interface path.</summary>
    /// <param name="offset">The absolute offset into <see cref="RamImage"/> address <c>0xA000</c> currently resolves
    /// to.</param>
    /// <param name="length">The number of bytes from <paramref name="offset"/> that are safely indexable — bytes
    /// beyond it (RAM smaller than the 8&#160;KiB window, or the window disabled) must fall back to
    /// <see cref="ReadRam"/>/<see cref="WriteRam"/>.</param>
    /// <returns><see langword="true"/> when the window is a pure array offset this call resolved; <see langword="false"/>
    /// when the window must stay on the interface path.</returns>
    bool TryComputeRamWindow(out int offset, out int length);
    /// <summary>Marks the external RAM dirty without an address-decoded write — the counterpart a cached-window write
    /// calls, since it bypasses <see cref="WriteRam"/>'s own dirty-flag set.</summary>
    void MarkExternalRamDirty();
    /// <summary>Gets the cartridge's current rumble motor level, 0..1 — host-facing feedback state a queued host
    /// samples each step and forwards to controller haptics. Zero on every mapper except a rumble-variant MBC5
    /// (<see cref="CartridgeHeader.HasRumble"/>), whose latched motor bit this hardware only supports on/off (never a
    /// PWM level), so the value is always exactly 0 or 1. Purely an output: it never influences emulated behavior, so
    /// it carries no read-side effect and is not part of the ROM/RAM/control surface above.</summary>
    /// <remarks>Declared here as a default-implemented member for documentation, but every mapper's actual dispatch
    /// runs through <see cref="CartridgeBase"/>'s <c>virtual</c> override of the same signature — a class whose
    /// interface is implemented by a base type must expose an overridable member for a derived override to reach the
    /// interface's dispatch table; a derived class's own same-named member alone is invisible through an
    /// <see cref="ICartridge"/>-typed reference.</remarks>
    float MotorLevel => 0f;
}
