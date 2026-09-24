namespace Puck.AdvancedGamingBrick.Post;

// --state-roundtrip [rom]: the whole-machine savestate round-trip diagnostic.
internal sealed partial class Diagnostics {
    private static bool ReportStateRoundTrip(string label, StateRoundTripResult result) {
        Console.WriteLine(value: $"  [{(result.Pass
            ? "PASS"
            : "FAIL")}] {label}  (image {result.ImageBytes} bytes)");
        Console.WriteLine(value: $"           frame-boundary: {result.FrameBoundary}");
        Console.WriteLine(value: $"           mid-frame:      {result.MidFrame}");
        Console.WriteLine(value: $"           double-restore: {result.DoubleRestore}");

        return result.Pass;
    }

    /// <summary>
    /// Runs the whole-machine savestate round-trip diagnostic: every generated micro-ROM and, when
    /// <paramref name="romPath"/> is a real ROM on disk, that cartridge too. Each ROM is booted, snapshotted at a
    /// frame boundary and mid-frame, restored, and re-run — asserting the framebuffer + register recordings are
    /// bit-identical. Returns 0 when every check passed, 1 otherwise.
    /// </summary>
    public int StateRoundTrip(string? romPath) {
        Console.WriteLine(value: "== whole-machine savestate round-trip ==");

        var failures = 0;

        foreach (var kind in MicroRoms.Kinds) {
            if (!ReportStateRoundTrip(
                label: $"micro:{kind}",
                result: StateRoundTripProbe.Run(
                    bios: BiosImage,
                    rom: MicroRoms.GenerateBytes(kind: kind)
                )
            )) {
                ++failures;
            }
        }

        if (!string.IsNullOrEmpty(value: romPath)) {
            if (File.Exists(path: romPath)) {
                if (!ReportStateRoundTrip(
                    label: $"rom:{Path.GetFileName(path: romPath)}",
                    result: StateRoundTripProbe.Run(
                        bios: BiosImage,
                        rom: File.ReadAllBytes(path: romPath)
                    )
                )) {
                    ++failures;
                }
            } else {
                Console.WriteLine(value: $"  [SKIP] rom:{romPath} — not found");
            }
        }

        Console.WriteLine(value: $"== savestate round-trip: {((failures == 0)
            ? "PASS"
            : $"FAIL ({failures} ROM(s))")} ==");

        return ((failures == 0)
            ? 0
            : 1
        );
    }
}
