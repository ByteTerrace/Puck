namespace Puck.GameBoy.Conformance;

/// <summary>
/// A diagnostic harness for a single mooneye test ROM. It runs the ROM to its <c>LD B,B</c> breakpoint and dumps
/// the CPU registers plus the high-RAM state, labelling each byte from the test's <c>.sym</c> file. Most mooneye
/// tests stash their measured-versus-expected diagnostics in high RAM (for example
/// <c>hram.fail_round</c>/<c>fail_expect</c>/<c>fail_actual</c>), so this turns an opaque pass/fail into the exact
/// sample point and value that diverged — the difference between guessing at sub-cycle timing and converging on it.
/// </summary>
internal static class MooneyeTrace {
    private const long InstructionBudget = 20_000_000;
    private const byte MagicBreakpoint = 0x40;
    private const ushort HighRamBase = 0xFF80;
    private const ushort HighRamEnd = 0xFFFE;

    public static int Run(string romPath, TextWriter output) {
        if (!File.Exists(path: romPath)) {
            output.WriteLine(value: $"trace: ROM not found: {romPath}");

            return 2;
        }

        var machine = new GameBoyMachine(
            cartridge: Cartridge.Load(rom: File.ReadAllBytes(path: romPath)),
            model: ConsoleModel.Dmg
        );

        var reachedBreakpoint = false;

        for (var step = 0L; step < InstructionBudget; step += 1) {
            if (machine.Bus.ReadByte(address: machine.Cpu.ProgramCounter) == MagicBreakpoint) {
                reachedBreakpoint = true;

                break;
            }

            machine.Step();
        }

        var cpu = machine.Cpu;

        output.WriteLine(value: $"trace: {Path.GetFileName(path: romPath)} ({(reachedBreakpoint ? "breakpoint" : "TIMEOUT")})");
        output.WriteLine(
            value: $"  AF={cpu.AF:X4} BC={cpu.BC:X4} DE={cpu.DE:X4} HL={cpu.HL:X4} SP={cpu.StackPointer:X4} PC={cpu.ProgramCounter:X4}"
        );

        // Raw high-RAM dump (where most tests stash per-sample result arrays). The loop counter is an int to
        // avoid a ushort wrap at 0xFFF0 + 16.
        for (var rowBase = (int)HighRamBase; rowBase <= HighRamEnd; rowBase += 16) {
            var row = new System.Text.StringBuilder(value: $"  {rowBase:X4}:");

            for (var offset = 0; (offset < 16) && ((rowBase + offset) <= HighRamEnd); offset += 1) {
                row.Append(value: $" {machine.Bus.ReadByte(address: (ushort)(rowBase + offset)):X2}");
            }

            output.WriteLine(value: row.ToString());
        }

        var labels = LoadHighRamLabels(symbolPath: Path.ChangeExtension(path: romPath, extension: ".sym"));

        foreach (var (address, name) in labels) {
            output.WriteLine(value: $"  {name} (0x{address:X4}) = 0x{machine.Bus.ReadByte(address: address):X2}");
        }

        return (reachedBreakpoint ? 0 : 2);
    }

    private static IReadOnlyList<(ushort Address, string Name)> LoadHighRamLabels(string symbolPath) {
        var labels = new List<(ushort, string)>();

        if (!File.Exists(path: symbolPath)) {
            return labels;
        }

        foreach (var rawLine in File.ReadLines(path: symbolPath)) {
            var line = rawLine.Trim();
            var colon = line.IndexOf(value: ':');
            var space = line.IndexOf(value: ' ');

            // Label lines look like "01:ff98 hram.fail_round"; keep only the high-RAM ones.
            if ((colon < 0) || (space <= colon) ||
                !ushort.TryParse(s: line.AsSpan(start: (colon + 1), length: (space - colon - 1)), style: System.Globalization.NumberStyles.HexNumber, provider: null, result: out var address)) {
                continue;
            }

            if ((address >= HighRamBase) && (address <= HighRamEnd)) {
                labels.Add(item: (address, line[(space + 1)..]));
            }
        }

        labels.Sort(comparison: static (a, b) => a.Item1.CompareTo(value: b.Item1));

        return labels;
    }
}
