namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Verifies the cartridge save-persistence API end to end, with no machine or external ROM: synthesise a ROM tagged
/// <c>SRAM_V113</c> (so detection allocates the 64&#160;KiB battery-SRAM window), write known bytes, export them as a
/// host would to a <c>.sav</c> file, load them into a FRESH cartridge, and confirm the bytes survive the round trip —
/// plus the dirty-flag transitions, the legacy 32&#160;KiB prefix load, and the oversized-image rejection.
/// </summary>
internal static class SaveRoundTripProbe {
    // "SRAM_V113" — the library tag that makes detection allocate the battery-SRAM backup.
    private static readonly byte[] SramTag = [0x53, 0x52, 0x41, 0x4D, 0x5F, 0x56, 0x31, 0x31, 0x33];

    /// <summary>Runs the round-trip checks.</summary>
    /// <returns>The pass/fail result and a one-line detail (listing any failed checks).</returns>
    public static (bool Pass, string Detail) Run() {
        var rom = new byte[0x1000];

        SramTag.CopyTo(array: rom, index: 0xC0);

        var failures = new List<string>();

        void Check(bool condition, string what) {
            if (!condition) {
                failures.Add(item: what);
            }
        }

        var source = new AgbCartridge(rom: rom);

        Check(condition: (source.Backup == CartridgeBackup.Sram), what: "detects SRAM backup");
        Check(condition: source.HasSave, what: "HasSave");
        Check(condition: !source.SaveDirty, what: "fresh cartridge starts clean");

        source.WriteSave(address: 0x0000u, value: 0x12);
        source.WriteSave(address: 0x0001u, value: 0x34);
        source.WriteSave(address: 0x0100u, value: 0xAB);

        Check(condition: source.SaveDirty, what: "SaveDirty set after a write");

        var exported = source.SaveData.ToArray();

        Check(condition: (exported.Length == (64 * 1024)), what: "exported save spans the 64 KiB SRAM window");
        Check(condition: ((exported[0x0000] == 0x12) && (exported[0x0001] == 0x34) && (exported[0x0100] == 0xAB)), what: "exported bytes match writes");

        var reloaded = new AgbCartridge(rom: rom);

        Check(condition: reloaded.LoadSave(data: exported), what: "LoadSave accepts a matching-size save");
        Check(condition: !reloaded.SaveDirty, what: "freshly loaded cartridge is clean");
        Check(condition: ((reloaded.ReadSave(address: 0x0000u) == 0x12) && (reloaded.ReadSave(address: 0x0001u) == 0x34) && (reloaded.ReadSave(address: 0x0100u) == 0xAB)), what: "persisted bytes read back");

        // A legacy 32 KiB SRAM .sav loads as a prefix of the 64 KiB window; empty and oversized images are rejected.
        var legacy = new byte[(32 * 1024)];

        legacy[0x0000] = 0x9A;
        Check(condition: (reloaded.LoadSave(data: legacy) && (reloaded.ReadSave(address: 0x0000u) == 0x9A)), what: "accepts a legacy 32 KiB save as a prefix");
        Check(condition: !reloaded.LoadSave(data: ReadOnlySpan<byte>.Empty), what: "rejects an empty save");
        Check(condition: !reloaded.LoadSave(data: new byte[((64 * 1024) + 1)]), what: "rejects an oversized save");

        return ((failures.Count == 0)
            ? (true, "SRAM export/import round-trip, dirty-flag transitions, legacy-prefix load, oversized rejection")
            : (false, $"failed: {string.Join(separator: ", ", values: failures)}"));
    }
}
