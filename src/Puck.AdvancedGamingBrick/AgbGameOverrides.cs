using System.Collections.Frozen;

namespace Puck.AdvancedGamingBrick;

/// <summary>A per-game override of the properties the ROM string-scan heuristic gets wrong: the save backup type and
/// whether the cartridge carries a real-time clock. <see langword="null"/> fields defer to the string scan.</summary>
/// <param name="Backup">The forced backup type, or <see langword="null"/> to keep the string-scan result.</param>
/// <param name="HasRtc">The forced RTC presence, or <see langword="null"/> to keep the string-scan result.</param>
/// <param name="HasRumble">Whether the cartridge wires a rumble motor to GPIO pin 3. Defaults to <see
/// langword="false"/> when absent.</param>
/// <param name="HasSolar">Whether the cartridge wires a light sensor to the GPIO overlay. Defaults to <see
/// langword="false"/> when absent.</param>
/// <param name="HasTilt">Whether the cartridge exposes the address-mapped tilt sensor at ROM offsets
/// <c>0x8000</c>-<c>0x8500</c>. Defaults to <see langword="false"/> when absent.</param>
public readonly record struct AgbGameOverride(CartridgeBackup? Backup, bool? HasRtc, bool HasRumble = false, bool HasSolar = false, bool HasTilt = false);

/// <summary>
/// A small hand-authored table keyed by the 4-character cartridge game code (ROM header offset 0xAC) that corrects
/// save-type / RTC detection <b>before</b> the <see cref="AgbCartridge"/> string-scan fallback. Every mainstream
/// emulator keeps one — the string scan has a known-broken minority (anti-piracy carts that embed decoy save strings,
/// the retro-classics series that baits with SRAM strings but is EEPROM-backed) that only an exact-code override
/// resolves. The facts are public documentation (game codes are printed in the cartridge header; the behaviours are
/// widely documented); no external emulator's data file was copied. See docs/ACKNOWLEDGMENTS.md for citations.
/// <para>
/// GPIO sensor presence (solar/tilt/gyro/rumble) keys off the same 4-character code. Game codes are public
/// cartridge-header data; only the mapping from code to modeled hardware is encoded here. Any curated fixed-address
/// idle-loop skip belongs here too. Extend the override record and this table rather than adding a second
/// content-sniffing path.
/// </para>
/// </summary>
public static class AgbGameOverrides {
    // Game-code strings are 4 ASCII chars (AXXY: title code + region). Packed big-endian into a uint key.
    private static readonly FrozenDictionary<uint, AgbGameOverride> s_table = Build();

    private static FrozenDictionary<uint, AgbGameOverride> Build() {
        var table = new Dictionary<uint, AgbGameOverride>();

        // Top Gun: Combat Zones — the one cart that embeds THREE conflicting save strings as an anti-piracy trap: it
        // writes a signature into whatever backup the emulator exposes, then detects it and locks the main menu. It
        // has no real save. Forcing "no backup" removes the decoy the anti-piracy routine probes for.
        Add(table, "A2YE", new AgbGameOverride(Backup: CartridgeBackup.None, HasRtc: false));

        // The RTC titles: they embed SIIRTC_V AND FLASH1M_V, so the string scan already gets these right — but keying
        // RTC/backup off the game code makes it authoritative rather than a lucky string hit, and documents the
        // known-RTC set. Pokémon Ruby / Sapphire / Emerald (USA · Europe · Japan), Flash 128K + RTC.
        foreach (var code in new[] { "AXVE", "AXVP", "AXVJ", "AXPE", "AXPP", "AXPJ", "BPEE", "BPEP", "BPEJ" }) {
            Add(table, code, new AgbGameOverride(Backup: CartridgeBackup.Flash128, HasRtc: true));
        }

        // Boktai: The Sun Is in Your Hand (USA/Europe/Japan) and Boktai 2: Solar Boy Django (Japan/USA/Europe) — the
        // RTC + solar-sensor titles. The backup defers to the string scan (to avoid asserting a save size we cannot
        // document with confidence); RTC and the GPIO light sensor are both forced for all six codes.
        foreach (var code in new[] { "U3IE", "U3IP", "U3IJ", "U32J", "U32E", "U32P" }) {
            Add(table, code, new AgbGameOverride(Backup: null, HasRtc: true, HasSolar: true));
        }

        // Shin Bokura no Taiyou: Gyakushuu no Sabata (Boktai 3, Japan-only) — same RTC + solar-sensor pairing.
        Add(table, "U33J", new AgbGameOverride(Backup: null, HasRtc: true, HasSolar: true));

        // Drill Dozer (Japan/USA/Europe) — SRAM + a rumble motor on GPIO pin 3, no RTC.
        foreach (var code in new[] { "V49J", "V49E", "V49P" }) {
            Add(table, code, new AgbGameOverride(Backup: CartridgeBackup.Sram, HasRtc: false, HasRumble: true));
        }

        // Goodboy Galaxy (Europe) — SRAM + rumble.
        Add(table, "2GBP", new AgbGameOverride(Backup: CartridgeBackup.Sram, HasRtc: false, HasRumble: true));

        // WarioWare: Twisted! (Japan/USA/Europe) — SRAM + rumble AND a gyro sensor sharing the same GPIO overlay.
        foreach (var code in new[] { "RZWJ", "RZWE", "RZWP" }) {
            Add(table, code, new AgbGameOverride(Backup: CartridgeBackup.Sram, HasRtc: false, HasRumble: true));
        }

        // Koro Koro Puzzle - Happy Panechu! (Japan-only) and Yoshi's Universal Gravitation / Yoshi Topsy-Turvy
        // (Japan/USA/Europe) — the address-mapped tilt sensor (ROM offsets 0x8000-0x8500), EEPROM-backed.
        Add(table, "KHPJ", new AgbGameOverride(Backup: CartridgeBackup.Eeprom, HasRtc: false, HasTilt: true));

        foreach (var code in new[] { "KYGJ", "KYGE", "KYGP" }) {
            Add(table, code, new AgbGameOverride(Backup: CartridgeBackup.Eeprom, HasRtc: false, HasTilt: true));
        }

        return table.ToFrozenDictionary();
    }
    private static void Add(Dictionary<uint, AgbGameOverride> table, string code, AgbGameOverride entry) =>
        table[Key(code: code)] = entry;
    private static uint Key(string code) =>
        ((uint)(byte)code[0] << 24) | ((uint)(byte)code[1] << 16) | ((uint)(byte)code[2] << 8) | (byte)code[3];

    /// <summary>Looks up the override for a ROM by its header game code. Returns the exact-code entry when present,
    /// then the Classic NES / Famicom Mini family rule (game code beginning with <c>'F'</c>), else <see
    /// langword="null"/> — the caller then falls back to the string scan.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <returns>The applicable override, or <see langword="null"/> when none applies.</returns>
    public static AgbGameOverride? Lookup(ReadOnlySpan<byte> rom) {
        // The game code lives at header offset 0xAC..0xAF; too-small images (hand-assembled micro-ROMs) have none.
        if (rom.Length < 0xB0) {
            return null;
        }

        var code = ((uint)rom[0xAC] << 24) | ((uint)rom[0xAD] << 16) | ((uint)rom[0xAE] << 8) | rom[0xAF];

        if (s_table.TryGetValue(key: code, value: out var entry)) {
            return entry;
        }

        // Classic NES Series / Famicom Mini: this whole cartridge family was assigned the 'F' first game-code letter,
        // and the carts bait save detection with an SRAM probe while actually being EEPROM-backed (a mis-detect throws
        // "Game Pak Error"). Force EEPROM for the family. This is documented behaviour, not a per-title guess.
        if (rom[0xAC] == (byte)'F') {
            return new AgbGameOverride(Backup: CartridgeBackup.Eeprom, HasRtc: false);
        }

        return null;
    }
}
