namespace Puck.HumbleGamingDeck.Post;

/// <summary>
/// Boots nestest through its reset vector the way a player does and runs both of its menu pages with the controller:
/// Start runs every test on the official-opcode page, Select turns to the unofficial-opcode page, and Start runs that.
/// nestest reports a page that passes as "OK" beside its first entry and a failing one as "Er", in nametable text, so
/// this reaches the PPU, NMI, and controllers as well as the CPU.
/// </summary>
internal sealed class NestestBootStage : IPostStage<PostContext> {
    private const ulong FrameTicks = 357_366UL;

    /// <inheritdoc/>
    public string Name => "nestest-boot";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (context.TestRomRoot is null) {
            return PostStageOutcome.Skip(detail: "no nes-test-roms corpus; use --fetch-corpora or --roms");
        }

        var imagePath = Path.Combine(path1: context.TestRomRoot, path2: "other/nestest.nes");

        if (!File.Exists(path: imagePath)) {
            return PostStageOutcome.Infra(detail: "incomplete nes-test-roms corpus: requires other/nestest.nes");
        }

        using var instance = HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: File.ReadAllBytes(path: imagePath))));
        var machine = instance.Machine;

        machine.RunCycles(masterTicks: (30 * FrameTicks));
        if (!Contains(machine: machine, text: "Select: Invalid ops") || Contains(machine: machine, text: "OK")) {
            return PostStageOutcome.Fail(detail: "the official-opcode menu was absent or showed OK before any test ran");
        }
        Press(button: HgdButtons.Start, machine: machine);
        machine.RunCycles(masterTicks: (90 * FrameTicks));
        if (!HasPageVerdict(machine: machine, verdict: "OK"u8)) {
            return PostStageOutcome.Fail(detail: "the official-opcode page did not report OK");
        }
        Press(button: HgdButtons.Select, machine: machine);
        machine.RunCycles(masterTicks: (10 * FrameTicks));
        if (!Contains(machine: machine, text: "Select: Normal ops") || Contains(machine: machine, text: "OK")) {
            return PostStageOutcome.Fail(detail: "Select did not open an unrun unofficial-opcode page");
        }
        Press(button: HgdButtons.Start, machine: machine);
        machine.RunCycles(masterTicks: (90 * FrameTicks));
        if (!HasPageVerdict(machine: machine, verdict: "OK"u8)) {
            return PostStageOutcome.Fail(detail: "the unofficial-opcode page did not report OK");
        }

        return PostStageOutcome.Pass(detail: "booted through the reset vector; both menu pages ran from the controller and reported OK in nametable text");
    }

    private static void Press(HgdButtons button, HgdMachine machine) {
        machine.Controllers.SetButtons(buttons: button, port: 0);
        machine.RunCycles(masterTicks: (4 * FrameTicks));
        machine.Controllers.SetButtons(buttons: HgdButtons.None, port: 0);
        machine.RunCycles(masterTicks: (4 * FrameTicks));
    }
    // nestest's own readme defines the whole-page verdict beside "Run all tests"; an individual test's OK is insufficient.
    private static bool HasPageVerdict(HgdMachine machine, ReadOnlySpan<byte> verdict) {
        Span<byte> row = stackalloc byte[32];

        for (var page = 0; (page < 2); ++page) {
            for (var offset = 0; (offset < 960); offset += row.Length) {
                for (var column = 0; (column < row.Length); ++column) {
                    row[column] = machine.Nametables.Read(offset: (offset + column), page: page);
                }
                if ((row.IndexOf(value: "Run all tests"u8) >= 0) && (row.IndexOf(value: verdict) >= 0)) {
                    return true;
                }
            }
        }

        return false;
    }
    // nestest's font places each character at its ASCII code, so text on screen is ASCII in nametable RAM.
    private static bool Contains(HgdMachine machine, string text) {
        for (var page = 0; (page < 2); ++page) {
            for (var offset = 0; (offset <= (960 - text.Length)); ++offset) {
                var match = true;

                for (var index = 0; (index < text.Length); ++index) {
                    if (machine.Nametables.Read(offset: (offset + index), page: page) != text[index]) {
                        match = false;

                        break;
                    }
                }
                if (match) {
                    return true;
                }
            }
        }

        return false;
    }
}
