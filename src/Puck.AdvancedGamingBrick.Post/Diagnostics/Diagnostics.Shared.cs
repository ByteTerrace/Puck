namespace Puck.AdvancedGamingBrick.Post;

// Helpers shared across the per-mode Diagnostics partial-class files: CLI arg parsing, the ROM-load-and-direct-boot
// shortcut most single-ROM inspectors start from, and the cycle-parity co-sim pre-flight gate.
internal static partial class Diagnostics {
    /// <summary>
    /// Pre-flight BIOS gate for the cycle-parity / co-simulation diagnostics. It classifies <see cref="BiosImage"/>
    /// by content hash and, when the image is not the verified retail BIOS, prints a prominent warning — the
    /// documented "phantom cycle drift" trap caused by diffing against the replacement BIOS. Returns
    /// <see langword="true"/> (abort the diagnostic) unless <c>--allow-replacement-bios</c>
    /// is passed, which downgrades the refusal to a warning and proceeds.
    /// </summary>
    private static bool ParityBiosGuard(string mode, string[] args) {
        var identity = AgbBiosProfile.Identify(image: BiosImage.Span);

        if (identity.IsCycleParityTrustworthy) {
            Console.WriteLine(value: $"  [bios] {mode}: {identity.Description} (sha1 {identity.Sha1}) — OK for cycle parity");

            return false;
        }

        var allow = (Array.IndexOf(array: args, value: "--allow-replacement-bios") >= 0);

        Console.WriteLine(value: "  ============================================================================");
        Console.WriteLine(value: $"  !! WARNING: {mode} is running on a NON-RETAIL BIOS — {identity.Description}");
        Console.WriteLine(value: "  !! Cycle-parity / co-sim numbers are UNTRUSTWORTHY on this image: the documented");
        Console.WriteLine(value: "  !! 'phantom cycle drift' trap. Supply the retail BIOS via PUCK_AGB_BIOS.");
        Console.WriteLine(value: "  ============================================================================");

        if (!allow) {
            Console.WriteLine(value: "  [bios] refusing to run (pass --allow-replacement-bios to override).");

            return true;
        }

        Console.WriteLine(value: "  [bios] --allow-replacement-bios set: proceeding on the non-retail BIOS anyway.");

        return false;
    }

    // Reads the value following a named flag (e.g. "--frames 600" -> "600"), or null when the flag is absent — the
    // same lookup Program.cs uses for its own knobs, duplicated here since local functions don't cross class scopes.
    private static string? ArgValue(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return args[(index + 1)];
            }
        }

        return null;
    }
    private static bool TryLoad(string romPath, string name, out AgbMachineInstance instance) {
        instance = null!;

        if (!File.Exists(path: romPath)) {
            Console.WriteLine(value: $"  [SKIP] {name}: not found at {romPath}");

            return false;
        }

        instance = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: BiosImage, rom: File.ReadAllBytes(path: romPath)));

        instance.Machine.DirectBoot();

        return true;
    }
}
