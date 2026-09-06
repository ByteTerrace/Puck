namespace Puck.AdvancedGamingBrick.Post;

// Helpers shared across the per-mode Diagnostics partial-class files: CLI arg parsing, the ROM-load-and-direct-boot
// shortcut most single-ROM inspectors start from, and the cycle-parity co-sim pre-flight gate.
internal sealed partial class Diagnostics {
    /// <summary>
    /// Pre-flight BIOS gate for the cycle-parity / co-simulation diagnostics. It classifies <see cref="BiosImage"/>
    /// by content hash and, when the image is not the verified retail BIOS, prints a prominent warning — the
    /// documented "phantom cycle drift" trap caused by diffing against the replacement BIOS. Returns
    /// <see langword="true"/> (abort the diagnostic) unless <c>--allow-replacement-bios</c>
    /// is passed, which downgrades the refusal to a warning and proceeds.
    /// </summary>
    private bool ParityBiosGuard(string mode, string[] args) {
        var identity = AgbBiosProfile.Identify(image: BiosImage.Span);

        if (identity.IsCycleParityTrustworthy) {
            Console.WriteLine(value: $"  [bios] {mode}: {identity.Description} (sha1 {identity.Sha1}) — OK for cycle parity");

            return false;
        }

        var allow = (Array.IndexOf(
            array: args,
            value: "--allow-replacement-bios"
        ) >= 0);

        Console.WriteLine(value: "  ============================================================================");
        Console.WriteLine(value: $"  !! WARNING: {mode} is running on a NON-RETAIL BIOS — {identity.Description}");
        Console.WriteLine(value: "  !! Cycle-parity / co-sim numbers are UNTRUSTWORTHY on this image: the documented");
        Console.WriteLine(value: "  !! 'phantom cycle drift' trap. Supply the retail BIOS via --bios <path>.");
        Console.WriteLine(value: "  ============================================================================");

        if (!allow) {
            Console.WriteLine(value: "  [bios] refusing to run (pass --allow-replacement-bios to override).");

            return true;
        }

        Console.WriteLine(value: "  [bios] --allow-replacement-bios set: proceeding on the non-retail BIOS anyway.");

        return false;
    }
    // Loads a ROM with this runner's explicit BIOS and machine options, then direct-boots it.
    private bool TryLoad(string romPath, string name, out AgbMachineInstance instance) {
        instance = null!;

        if (!File.Exists(path: romPath)) {
            Console.WriteLine(value: $"  [SKIP] {name}: not found at {romPath}");

            return false;
        }

        instance = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(
            bios: BiosImage, options: MachineOptions,
            rom: File.ReadAllBytes(path: romPath)
        ));

        instance.Machine.DirectBoot();

        return true;
    }
}
