using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the battery-save seam behaves. On an MBC3+RAM+BATTERY cartridge, a RAM store sets the host's flush
/// trigger (<see cref="ICartridge.ExternalRamDirty"/>); exporting does NOT clear it (a failed disk write must retry)
/// while an acknowledged flush and an import do; an exported save imported into a FRESH machine reproduces the
/// external RAM byte-for-byte; and the persistent-clock footer round-trips the RTC registers through the standard
/// 48-byte layout — set through the real MBC3 register protocol, read back latched on the fresh machine — with the
/// embedded wall timestamp preserved for foreign-emulator interop but provably IGNORED by the import (the
/// deterministic clock resumes, never advancing from wall time). This is the contract the demo's
/// <c>&lt;romPath&gt;.sav</c> persistence (power-on load, debounced flush, save-across-reboot) stands on.
/// </summary>
internal sealed class BatterySaveStage : IPostStage {
    private const long InteropTimestamp = 0x0123_4567_89AB_CDEF; // recognizable, provably-ignored footer metadata
    private const int PatternLength = 64;

    /// <inheritdoc/>
    public string Name =>
        "battery-save";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = SyntheticRom.Create(cartridgeType: 0x13, ramSize: 0x03); // MBC3+RAM+BATTERY, 32 KiB RAM

        using var machine = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);

        var cartridge = machine.GetRequiredService<ICartridge>();

        if (!cartridge.Header.HasBattery || (cartridge.ExternalRamByteCount != 0x8000)) {
            return PostStageOutcome.Fail(detail: $"the synthetic MBC3 header decoded wrong: battery {cartridge.Header.HasBattery}, RAM {cartridge.ExternalRamByteCount} bytes (expected battery-backed 32768)");
        }

        if (cartridge.ExternalRamDirty) {
            return PostStageOutcome.Fail(detail: "a freshly assembled cartridge reported dirty external RAM before any store");
        }

        // Enable the RAM window and select bank 0 (the MBC3 protocol), then store a recognizable pattern.
        cartridge.WriteControl(address: 0x0000, value: 0x0A);
        cartridge.WriteControl(address: 0x4000, value: 0x00);

        for (var index = 0; (index < PatternLength); index++) {
            cartridge.WriteRam(address: (ushort)(0xA000 + index), value: (byte)(0xA5 ^ index));
        }

        if (!cartridge.ExternalRamDirty) {
            return PostStageOutcome.Fail(detail: "a RAM store did not set ExternalRamDirty — the host flush trigger is dead");
        }

        var save = cartridge.ExportExternalRam();

        if (!cartridge.ExternalRamDirty) {
            return PostStageOutcome.Fail(detail: "ExportExternalRam cleared the dirty flag — a failed disk write could never retry");
        }

        cartridge.MarkExternalRamClean();

        if (cartridge.ExternalRamDirty) {
            return PostStageOutcome.Fail(detail: "MarkExternalRamClean left the dirty flag set");
        }

        // The power-on load: a fresh machine imports the exported save and must expose the identical bytes.
        using var reboot = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);

        var rebootCartridge = reboot.GetRequiredService<ICartridge>();

        rebootCartridge.ImportExternalRam(source: save);

        if (rebootCartridge.ExternalRamDirty) {
            return PostStageOutcome.Fail(detail: "ImportExternalRam left the dirty flag set — a just-loaded save would immediately re-flush");
        }

        rebootCartridge.WriteControl(address: 0x0000, value: 0x0A);
        rebootCartridge.WriteControl(address: 0x4000, value: 0x00);

        for (var index = 0; (index < PatternLength); index++) {
            var expected = (byte)(0xA5 ^ index);
            var actual = rebootCartridge.ReadRam(address: (ushort)(0xA000 + index));

            if (actual != expected) {
                return PostStageOutcome.Fail(detail: $"the imported save diverged at offset {index}: expected 0x{expected:X2}, read 0x{actual:X2}");
            }
        }

        if (!save.AsSpan().SequenceEqual(other: rebootCartridge.ExportExternalRam())) {
            return PostStageOutcome.Fail(detail: "the imported external RAM is not byte-identical to the exported save");
        }

        // The persistent clock: set the RTC through the real register protocol (select register 0x08..0x0B via
        // 0x4000, write through the RAM window), export the footer, import it into the fresh machine, then latch
        // (0x6000: 0 then 1) and read the registers back.
        (byte Register, byte Value)[] clock = [(0x08, 30), (0x09, 5), (0x0A, 3), (0x0B, 0x2A)];

        foreach (var (register, value) in clock) {
            cartridge.WriteControl(address: 0x4000, value: register);
            cartridge.WriteRam(address: 0xA000, value: value);
        }

        var footer = cartridge.ExportPersistentClock(unixTimestampSeconds: InteropTimestamp);

        if (footer.Length != cartridge.PersistentClockByteCount) {
            return PostStageOutcome.Fail(detail: $"the exported clock footer is {footer.Length} bytes; the cartridge declares {cartridge.PersistentClockByteCount}");
        }

        if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(source: footer.AsSpan(start: 40)) != InteropTimestamp) {
            return PostStageOutcome.Fail(detail: "the exported clock footer did not stamp the host's interop timestamp at offset 40");
        }

        rebootCartridge.ImportPersistentClock(source: footer);
        rebootCartridge.WriteControl(address: 0x6000, value: 0x00);
        rebootCartridge.WriteControl(address: 0x6000, value: 0x01);

        foreach (var (register, expected) in clock) {
            rebootCartridge.WriteControl(address: 0x4000, value: register);

            var actual = rebootCartridge.ReadRam(address: 0xA000);

            if (actual != expected) {
                return PostStageOutcome.Fail(detail: $"the imported clock diverged at RTC register 0x{register:X2}: expected {expected}, read {actual} — either the footer layout or the timestamp-ignoring import is wrong");
            }
        }

        // The HuC3 clock footer: set minutes=1234 (0x4D2) and days=0xABC through the real nibble-command protocol
        // (mode 0xB; 0x4n/0x5n set the access index, 0x3n stores a nibble post-increment), export, import into a
        // fresh machine, and verify by protocol read-back plus a re-export field compare.
        if (RunHuC3ClockLeg() is { } huc3Failure) {
            return PostStageOutcome.Fail(detail: huc3Failure);
        }

        return PostStageOutcome.Pass(detail: $"MBC3+RAM+BATTERY: store sets dirty, export preserves it, acknowledge/import clear it, a {save.Length}-byte save round-trips into a fresh machine byte-identical, and the {footer.Length}-byte clock footer restores the RTC registers (interop timestamp stamped and ignored); HuC3: the 16-byte minutes/days footer round-trips through the nibble protocol");
    }

    private static string? RunHuC3ClockLeg() {
        var rom = SyntheticRom.Create(cartridgeType: 0xFE, ramSize: 0x03); // HuC3 (RAM + battery per the type decode)

        using var machine = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);

        var cartridge = machine.GetRequiredService<ICartridge>();

        if (cartridge.PersistentClockByteCount != 16) {
            return $"the HuC3 cartridge declares a {cartridge.PersistentClockByteCount}-byte clock footer (expected 16)";
        }

        // minutes = 0x4D2 (1234), days = 0xABC — stored LSB-nibble first at access indices 0..2 and 3..6.
        cartridge.WriteControl(address: 0x0000, value: 0x0B);

        foreach (var command in (byte[])[0x40, 0x50, 0x32, 0x3D, 0x34, 0x3C, 0x3B, 0x3A, 0x30]) {
            cartridge.WriteRam(address: 0xA000, value: command);
        }

        var footer = cartridge.ExportPersistentClock(unixTimestampSeconds: InteropTimestamp);

        if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(source: footer.AsSpan(start: 8)) != InteropTimestamp) {
            return "the HuC3 clock footer did not stamp the host's interop timestamp at offset 8";
        }

        using var reboot = PostMachine.Build(model: ConsoleModel.Dmg, rom: rom);

        var rebootCartridge = reboot.GetRequiredService<ICartridge>();

        rebootCartridge.ImportPersistentClock(source: footer);

        // Protocol read-back of nibble 0 (minutes' low nibble, 2): fetch post-increment in command mode, then read.
        rebootCartridge.WriteControl(address: 0x0000, value: 0x0B);
        rebootCartridge.WriteRam(address: 0xA000, value: 0x40);
        rebootCartridge.WriteRam(address: 0xA000, value: 0x50);
        rebootCartridge.WriteRam(address: 0xA000, value: 0x10);
        rebootCartridge.WriteControl(address: 0x0000, value: 0x0C);

        if (rebootCartridge.ReadRam(address: 0xA000) != 0x02) {
            return "the imported HuC3 clock's minutes low nibble did not read back 2 through the protocol";
        }

        if (!footer.AsSpan(start: 0, length: 8).SequenceEqual(other: rebootCartridge.ExportPersistentClock(unixTimestampSeconds: InteropTimestamp).AsSpan(start: 0, length: 8))) {
            return "the imported HuC3 clock's re-exported minutes/days diverged from the original footer";
        }

        return null;
    }
}
