using Puck.HumbleGamingBrick.Forge;
using ITimer = Puck.HumbleGamingBrick.Interfaces.ITimer;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>A reference cartridge the boot-ROM handoff stage boots beside its synthetic headers.</summary>
/// <param name="Path">The cartridge's path relative to the reference-ROM corpus root.</param>
/// <param name="Counter">The divider counter the cartridge's own assertions establish for the revisions it names.</param>
/// <param name="Models">The revisions the cartridge asserts that counter for.</param>
internal readonly record struct BootRomReferenceCartridge(string Path, ushort Counter, ConsoleModel[] Models);

/// <summary>
/// Tier-A stage: a machine that boots through the forge's authored boot ROM lands on the same observable handoff as a
/// machine started at the seeded post-boot state. Every revision runs against every header case — the licensee buckets,
/// both color flags, the title checksums the Color handoff reads, and the checksums whose boot timing is told apart by
/// the fourth title letter — and against each named reference cartridge the corpus holds.
/// <para>
/// The compared surface is what a cartridge can read at <c>0x0100</c>: the processor register file, the divider
/// counter, every readable high-page register, high RAM through <c>0xFFFE</c>, the interrupt-enable register, and
/// Color palette RAM where the hardware runs natively. One thing sits outside it: the sub-register phase of the
/// picture processor's pixel pipeline and of the audio generators is state the seeded handoff sets to captured
/// constants that no executing program reaches — the seeded square-channel timer exceeds its own reload period, and
/// the seeded dot phase is odd where every instruction boundary lands on a multiple of four.
/// </para>
/// <para>
/// The divider counter alone would otherwise compare a prediction against itself: the same table seeds the post-boot
/// state, sets the emitted image's target, and steers the builder's solve. The reference cartridges break that circle
/// — each asserts, from its own expectations, the counter the revisions it names hand off with, so the stage checks
/// both the prediction and the authored image against a number this repository did not choose. That pin covers only
/// those cartridges' shared header; every other row of the prediction tables is unpinned.
/// </para>
/// </summary>
internal sealed class BootRomHandoffStage : IPostStage<PostContext> {
    private const int HeaderChecksumEnd = 0x014C;
    private const int HeaderChecksumOffset = 0x014D;
    private const int HeaderChecksumStart = 0x0134;
    private const int MinimumRomLength = 0x0150;

    // The mooneye boot_div cartridges, whose per-revision divider assertions are this stage's only evidence from
    // outside the repository.
    private static readonly BootRomReferenceCartridge[] References = [
        new(
            Counter: 0x1830,
            Models: [ConsoleModel.Dmg0],
            Path: "mooneye-test-suite/acceptance/boot_div-dmg0.gb"
        ),
        new(
            Counter: 0xABCC,
            Models: [ConsoleModel.DmgB, ConsoleModel.DmgC, ConsoleModel.Mgb],
            Path: "mooneye-test-suite/acceptance/boot_div-dmgABCmgb.gb"
        ),
        new(
            Counter: 0xD860,
            Models: [ConsoleModel.Sgb],
            Path: "mooneye-test-suite/acceptance/boot_div-S.gb"
        ),
        new(
            Counter: 0xD850,
            Models: [ConsoleModel.Sgb2],
            Path: "mooneye-test-suite/acceptance/boot_div2-S.gb"
        ),
        new(
            Counter: 0x2884,
            Models: [ConsoleModel.Cgb0],
            Path: "mooneye-test-suite/misc/boot_div-cgb0.gb"
        ),
        new(
            Counter: 0x2678,
            Models: [ConsoleModel.CgbA, ConsoleModel.CgbB, ConsoleModel.CgbC, ConsoleModel.CgbD, ConsoleModel.CgbE],
            Path: "mooneye-test-suite/misc/boot_div-cgbABCDE.gb"
        ),
        new(
            Counter: 0x267C,
            Models: [ConsoleModel.Agb, ConsoleModel.Ags],
            Path: "mooneye-test-suite/misc/boot_div-A.gb"
        ),
    ];

    /// <inheritdoc/>
    public string Name =>
        "boot-rom-handoff";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var loaded = LoadReferences(context: context);

        if (
            context.RequireAssets &&
            (loaded.Count != References.Length)
        ) {
            return PostStageOutcome.Infra(detail: $"{(References.Length - loaded.Count)} of {References.Length} reference cartridges are missing from the corpus at \"{context.TestRomRoot}\"");
        }

        var cases = BootRomHandoffCases.Create();
        var images = Enum.GetValues<ConsoleModel>().ToDictionary(
            elementSelector: static model => BootRomBuilder.Build(model: model),
            keySelector: static model => model
        );
        var comparisons = 0;

        foreach (var model in Enum.GetValues<ConsoleModel>()) {
            var image = images[model];

            foreach (var (name, rom) in cases.Concat(second: loaded.Select(selector: static reference => (reference.Reference.Path, reference.Rom)))) {
                var difference = BootRomHandoff.Compare(
                    bootRom: image,
                    model: model,
                    rom: rom
                );

                ++comparisons;

                if (difference is not null) {
                    return PostStageOutcome.Fail(detail: $"{model} booting \"{name}\" diverged from the seeded handoff: {difference}");
                }
            }
        }

        var pinned = 0;

        foreach (var (reference, rom) in loaded) {
            var header = CartridgeHeader.Parse(rom: rom);

            foreach (var model in reference.Models) {
                var predicted = BootDivPrediction.Compute(
                    header: header,
                    model: model
                );

                if (predicted != reference.Counter) {
                    return PostStageOutcome.Fail(detail: $"the divider prediction for {model} on \"{reference.Path}\" is 0x{predicted:X4}, but the cartridge asserts 0x{reference.Counter:X4}");
                }

                var observed = BootedDivider(
                    image: images[model],
                    model: model,
                    rom: rom
                );

                if (observed != reference.Counter) {
                    return PostStageOutcome.Fail(detail: $"{model} booting \"{reference.Path}\" through the authored image handed off with divider 0x{observed:X4}, but the cartridge asserts 0x{reference.Counter:X4}");
                }

                ++pinned;
            }
        }

        return PostStageOutcome.Pass(detail: $"{comparisons} boots across {Enum.GetValues<ConsoleModel>().Length} revisions reached the seeded handoff; {loaded.Count} of {References.Length} reference cartridges present; the divider is pinned to their own assertions on {pinned} of {Enum.GetValues<ConsoleModel>().Length} revisions for their one header, and is otherwise compared only against the prediction that seeded it; the picture-pipeline and audio-generator phase is outside the compared surface");
    }

    // Boots one cartridge through the revision's authored image and reads the divider counter it hands off with. A
    // wedge cannot reach here: the handoff comparison above boots the same pair first and reports it.
    private static ushort BootedDivider(ConsoleModel model, byte[] image, byte[] rom) {
        using var instance = MachineFactory.Create(
            configuration: new MachineConfiguration(
                bootRom: image,
                cartridgeRom: rom,
                model: model
            ),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        _ = BootRomHandoff.TryRunToHandoff(
            instance: instance,
            instructionCeiling: BootRomHandoff.DefaultInstructionCeiling
        );

        return instance.GetRequiredService<ITimer>().DivCounter;
    }
    // The named reference cartridges the corpus actually holds, kept to the ones whose logo and header checksum the
    // hardware would accept — a boot ROM wedges on anything else, exactly as the hardware does.
    private static List<(BootRomReferenceCartridge Reference, byte[] Rom)> LoadReferences(PostContext context) {
        var loaded = new List<(BootRomReferenceCartridge, byte[])>(capacity: References.Length);

        if (string.IsNullOrEmpty(value: context.TestRomRoot)) {
            return loaded;
        }

        foreach (var reference in References) {
            byte[] rom;

            try {
                rom = File.ReadAllBytes(path: Path.Combine(
                    path1: context.TestRomRoot,
                    path2: reference.Path
                ));
            } catch (Exception exception) when (exception is (IOException or UnauthorizedAccessException)) {
                continue;
            }

            if (IsBootable(rom: rom)) {
                loaded.Add(item: (reference, rom));
            }
        }

        return loaded;
    }
    private static bool IsBootable(byte[] rom) {
        if (rom.Length < MinimumRomLength) {
            return false;
        }

        if (!rom.AsSpan(
            length: CartridgeHeader.Logo.Length,
            start: CartridgeHeader.LogoOffset
        ).SequenceEqual(other: CartridgeHeader.Logo)) {
            return false;
        }

        byte checksum = 0;

        for (var offset = HeaderChecksumStart; (offset <= HeaderChecksumEnd); ++offset) {
            checksum = ((byte)((checksum - rom[offset]) - 1));
        }

        return (checksum == rom[HeaderChecksumOffset]);
    }
}
