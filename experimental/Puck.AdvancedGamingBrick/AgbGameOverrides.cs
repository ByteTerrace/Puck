using System.Collections.Frozen;

namespace Puck.AdvancedGamingBrick;

/// <summary>A per-game override of the properties the ROM string-scan heuristic gets wrong: the save backup type and
/// whether the cartridge carries a real-time clock. <see langword="null"/> fields defer to the string scan.</summary>
/// <param name="Backup">The forced backup type, or <see langword="null"/> to keep the string-scan result.</param>
/// <param name="HasRtc">The forced RTC presence, or <see langword="null"/> to keep the string-scan result.</param>
public readonly record struct AgbGameOverride(CartridgeBackup? Backup, bool? HasRtc);

/// <summary>
/// A small hand-authored table keyed by the 4-character cartridge game code (ROM header offset 0xAC) that corrects
/// save-type / RTC detection <b>before</b> the <see cref="AgbCartridge"/> string-scan fallback. Every mainstream
/// emulator keeps one — the string scan has a known-broken minority (anti-piracy carts that embed decoy save strings,
/// the retro-classics series that baits with SRAM strings but is EEPROM-backed) that only an exact-code override
/// resolves. The facts are public documentation (game codes are printed in the cartridge header; the behaviours are
/// widely documented); no external emulator's data file was copied. See docs/ACKNOWLEDGMENTS.md for citations.
/// <para>
/// SEAM: this table is also the intended home for later game-code keying — GPIO sensor presence (solar/tilt/gyro/
/// rumble), and any curated fixed-address idle-loop skip — all of which key off the same 4-character code. Extend the
/// override record and this table rather than adding a second content-sniffing path.
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

        // The RTC titles: they embed SIIRTC_V AND (Ruby/Sapphire/Emerald) FLASH1M_V, so the string scan already gets
        // these right — but keying RTC/backup off the game code makes it authoritative rather than a lucky string hit,
        // and documents the known-RTC set. Pokémon Ruby / Sapphire / Emerald (USA · Europe · Japan), Flash 128K + RTC.
        foreach (var code in new[] { "AXVE", "AXVP", "AXVJ", "AXPE", "AXPP", "AXPJ", "BPEE", "BPEP", "BPEJ" }) {
            Add(table, code, new AgbGameOverride(Backup: CartridgeBackup.Flash128, HasRtc: true));
        }

        // Boktai: The Sun Is in Your Hand — the RTC + solar-sensor title (USA · Europe · Japan). The sensor itself
        // rides the GPIO seam (deferred); only the RTC keying is forced here (the backup defers to the string scan,
        // to avoid asserting a save size we cannot document with confidence).
        foreach (var code in new[] { "U3IE", "U3IP", "U3IJ" }) {
            Add(table, code, new AgbGameOverride(Backup: null, HasRtc: true));
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

        // Classic NES Series / Famicom Mini: Nintendo assigned this whole family the 'F' first game-code letter, and
        // the carts bait save detection with an SRAM probe while actually being EEPROM-backed (a mis-detect throws
        // "Game Pak Error"). Force EEPROM for the family. This is documented behaviour, not a per-title guess.
        if (rom[0xAC] == (byte)'F') {
            return new AgbGameOverride(Backup: CartridgeBackup.Eeprom, HasRtc: false);
        }

        return null;
    }
}
