using System.Globalization;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>Compares every CPU instruction boundary with the pinned automation reference log: registers, the CPU cycle
/// count, and the PPU line and dot, which checks the CPU and PPU clocks against each other.</summary>
internal sealed class NestestStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "nestest";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (context.TestRomRoot is null) {
            return PostStageOutcome.Skip(detail: "no nes-test-roms corpus; use --fetch-corpora or --roms");
        }
        var imagePath = Path.Combine(path1: context.TestRomRoot, path2: "other/nestest.nes");
        var logPath = Path.Combine(path1: context.TestRomRoot, path2: "other/nestest.log");

        if (!File.Exists(path: imagePath) || !File.Exists(path: logPath)) {
            return PostStageOutcome.Infra(detail: "incomplete nes-test-roms corpus: requires other/nestest.nes and other/nestest.log");
        }
        using var instance = HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: File.ReadAllBytes(path: imagePath))));
        var machine = instance.Machine;

        machine.RunCycles(masterTicks: 84);
        var cpu = machine.Cpu;

        cpu.ProgramCounter = 0xC000;
        var lines = 0;

        foreach (var line in File.ReadLines(path: logPath)) {
            if (string.IsNullOrWhiteSpace(value: line)) {
                continue;
            }
            ++lines;
            var expectedPc = ushort.Parse(s: line.AsSpan(length: 4, start: 0), style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture);
            var expectedCycles = ulong.Parse(s: line.AsSpan(start: (line.IndexOf(comparisonType: StringComparison.Ordinal, value: "CYC:") + 4)).Trim(), provider: CultureInfo.InvariantCulture);

            var (expectedLine, expectedDot) = PpuPosition(line: line);

            if ((cpu.ProgramCounter != expectedPc) || (cpu.A != Register(label: "A:", line: line)) ||
                (cpu.X != Register(label: "X:", line: line)) || (cpu.Y != Register(label: "Y:", line: line)) ||
                (cpu.P != Register(label: "P:", line: line)) || (cpu.S != Register(label: "SP:", line: line)) || (cpu.Cycles != expectedCycles) ||
                (machine.Ppu.Line != expectedLine) || (machine.Ppu.Dot != expectedDot)) {
                return PostStageOutcome.Fail(detail: $"line {lines}: PC={cpu.ProgramCounter:X4} A={cpu.A:X2} X={cpu.X:X2} Y={cpu.Y:X2} P={cpu.P:X2} SP={cpu.S:X2} PPU={machine.Ppu.Line},{machine.Ppu.Dot} CYC={cpu.Cycles}; expected {line}");
            }
            var start = cpu.Cycles;

            do {
                machine.RunCycles(masterTicks: 1);
                if (cpu.IsJammed || ((cpu.Cycles - start) > 8)) {
                    return PostStageOutcome.Fail(detail: $"line {lines}: instruction did not reach its next boundary");
                }
            } while ((cpu.Cycles == start) || !cpu.AtInstructionBoundary);
        }

        return ((lines == 0) ? PostStageOutcome.Infra(detail: "empty nestest reference log")
            : PostStageOutcome.Pass(detail: $"{lines} lines through the last log line from $C000; PC/A/X/Y/P/SP, PPU line and dot, and CYC match"));
    }

    // The log's "PPU:lll,ddd" column is the PPU line and the dot it is about to run when the instruction starts.
    private static (int Line, int Dot) PpuPosition(string line) {
        var start = (line.IndexOf(comparisonType: StringComparison.Ordinal, value: "PPU:") + 4);
        var comma = line.IndexOf(startIndex: start, value: ',');
        var end = line.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: comma, value: "CYC:");

        return (
            int.Parse(s: line.AsSpan(length: (comma - start), start: start).Trim(), provider: CultureInfo.InvariantCulture),
            int.Parse(s: line.AsSpan(length: ((end - comma) - 1), start: (comma + 1)).Trim(), provider: CultureInfo.InvariantCulture)
        );
    }
    private static byte Register(string line, string label) {
        var start = (line.IndexOf(comparisonType: StringComparison.Ordinal, value: label) + label.Length);

        return byte.Parse(s: line.AsSpan(length: 2, start: start), style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture);
    }
}
