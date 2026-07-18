namespace Puck.AdvancedGamingBrick.Post;

// --state-roundtrip [rom]: the whole-machine savestate round-trip diagnostic.
internal static partial class Diagnostics {
    /// <summary>
    /// Runs the whole-machine savestate round-trip diagnostic: every generated micro-ROM and, when
    /// <paramref name="romPath"/> is a real ROM on disk, that cartridge too. Each ROM is booted, snapshotted at a
    /// frame boundary and mid-frame, restored, and re-run — asserting the framebuffer + register recordings are
    /// bit-identical. Returns 0 when every check passed, 1 otherwise.
    /// </summary>
    public static int StateRoundTrip(string? romPath) {
        Console.WriteLine(value: "== whole-machine savestate round-trip ==");

        var failures = 0;

        foreach (var kind in MicroRoms.Kinds) {
            var (pass, _) = StateRoundTripProbe.Run(rom: MicroRoms.GenerateBytes(kind: kind), label: $"micro:{kind}", bios: BiosImage);

            if (!pass) {
                ++failures;
            }
        }

        if (!string.IsNullOrEmpty(value: romPath)) {
            if (File.Exists(path: romPath)) {
                var (pass, _) = StateRoundTripProbe.Run(rom: File.ReadAllBytes(path: romPath), label: $"rom:{Path.GetFileName(path: romPath)}", bios: BiosImage);

                if (!pass) {
                    ++failures;
                }
            } else {
                Console.WriteLine(value: $"  [SKIP] rom:{romPath} — not found");
            }
        }

        Console.WriteLine(value: $"== savestate round-trip: {((failures == 0) ? "PASS" : $"FAIL ({failures} ROM(s))")} ==");

        return ((failures == 0) ? 0 : 1);
    }
}
