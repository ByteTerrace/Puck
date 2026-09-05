using Puck.HumbleGamingBrick.Forge;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: a machine that boots through the forge's authored boot ROM lands on the same observable handoff as a
/// machine started at the seeded post-boot state. Every revision runs against every header case — the licensee buckets,
/// both color flags, the title checksums the Color handoff reads, and the checksums whose boot timing is told apart by
/// the fourth title letter — and any real cartridge in the reference corpus whose header the hardware would accept.
/// <para>
/// The compared surface is what a cartridge can read at <c>0x0100</c>: the processor register file, the divider
/// counter, every readable high-page register, the interrupt-enable register, and Color palette RAM. Two things sit
/// outside it. The sub-register phase of the picture processor's pixel pipeline and of the audio generators is state
/// the seeded handoff sets to captured constants that no executing program reaches — the seeded square-channel timer
/// exceeds its own reload period, and the seeded dot phase is odd where every instruction boundary lands on a multiple
/// of four. And on the revisions whose seeded handoff parks on the first line, the status register's LY-comparison bit
/// is masked: the seeded state has that latch clear while LY and LYC are both zero, which the running processor cannot
/// hold because it recomputes the latch every dot.
/// </para>
/// </summary>
internal sealed class BootRomHandoffStage : IPostStage<PostContext> {
    private const int HeaderChecksumEnd = 0x014C;
    private const int HeaderChecksumOffset = 0x014D;
    private const int HeaderChecksumStart = 0x0134;
    private const int MaximumCorpusRoms = 3;
    private const int MinimumRomLength = 0x0150;

    /// <inheritdoc/>
    public string Name =>
        "boot-rom-handoff";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = BootRomHandoffCases.Create();
        var corpus = CorpusRoms(context: context);
        var comparisons = 0;

        foreach (var model in Enum.GetValues<ConsoleModel>()) {
            var image = BootRomBuilder.Build(model: model);

            foreach (var (name, rom) in cases.Concat(second: corpus)) {
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

        return PostStageOutcome.Pass(detail: $"{comparisons} boots across {Enum.GetValues<ConsoleModel>().Length} revisions reached the seeded handoff; {corpus.Length} corpus cartridges included; the picture-pipeline and audio-generator phase and the first-line LY-comparison bit are outside the compared surface");
    }

    // Real cartridges from the reference corpus, kept to the ones whose logo and header checksum the hardware would
    // accept — a boot ROM wedges on anything else, exactly as the hardware does.
    private static (string Name, byte[] Rom)[] CorpusRoms(PostContext context) {
        if (
            string.IsNullOrEmpty(value: context.TestRomRoot) ||
            !Directory.Exists(path: context.TestRomRoot)
        ) {
            return [];
        }

        var accepted = new List<(string Name, byte[] Rom)>();

        foreach (var path in Directory.EnumerateFiles(
            path: context.TestRomRoot,
            searchPattern: "*.gb*",
            searchOption: SearchOption.AllDirectories
        ).Order(comparer: StringComparer.Ordinal)) {
            if (accepted.Count == MaximumCorpusRoms) {
                break;
            }

            byte[] rom;

            try {
                rom = File.ReadAllBytes(path: path);
            } catch (IOException) {
                continue;
            }

            if (IsBootable(rom: rom)) {
                accepted.Add(item: (Path.GetFileName(path: path), rom));
            }
        }

        return [.. accepted];
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
