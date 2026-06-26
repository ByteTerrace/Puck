using System.Globalization;
using Puck.HumbleGamingBrick.Ares;
using Xunit;

namespace Puck.HumbleGamingBrick.Conformance.Tests;

/// <summary>
/// Runs the ares-architecture DMG core (<see cref="AresMachine"/>) through the same mooneye / blargg / gbmicrotest
/// protocols the old core (<see cref="Sm83Machine"/>) is judged by, and reports a per-suite pass/fail scorecard with
/// the failing-ROM list — plus, when run together, a side-by-side comparison against the old core so regressions (a
/// ROM the ares core fails but the old core passes) and improvements (the reverse) are explicit.
///
/// The ares core is DMG-only (CGB deferred), so only the DMG-eligible cases from <see cref="RomCatalog"/> are run; CGB
/// rows are skipped, not counted as failures. The runners here are thin copies of the Protocol/ runners adapted to the
/// AresMachine surface (<c>Peek</c>, <c>Cpu.RegisterX</c>, <c>Step</c>, instruction-capped loops — the ares core has no
/// public dot clock and shifts serial per-bit, so loops are bounded by an instruction cap, never the serial channel).
/// </summary>
public sealed class AresConformanceScorecard {
    // Mooneye/age settle well within a few million instructions; cap generously per the task brief.
    private const long MooneyeInstructionCap = 30_000_000L;

    // blargg cpu_instrs sub-ROMs are the longest; bound by instructions (no public dot clock on the ares core).
    private const long BlarggInstructionCap = 250_000_000L;

    // Guard one frame's worth of instructions so a runaway ROM cannot wedge the gbmicrotest frame loop.
    private const long FrameInstructionGuard = 2_000_000L;

    private static readonly byte[] MooneyePassRegisters = [3, 5, 8, 13, 21, 34];

    private readonly ITestOutputHelper m_output;

    public AresConformanceScorecard(ITestOutputHelper output) =>
        m_output = output;

    [Fact]
    public void GenerateAresScorecard() {
        Assert.SkipUnless(condition: RomCatalog.IsAvailable, reason: "PUCK_GB_TESTROMS not set; GB test corpus unavailable.");

        // Only the DMG-eligible mooneye + blargg + gbmicrotest cases (the ares core is DMG-only).
        var cases = RomCatalog.Enumerate()
            .Where(predicate: static c => c.Model == ConsoleModel.Dmg)
            .Where(predicate: static c => c.Protocol is ResultProtocol.Mooneye or ResultProtocol.Blargg or ResultProtocol.GbMicrotest)
            .Where(predicate: static c => string.Equals(a: c.Suite, b: "mooneye", comparisonType: StringComparison.Ordinal)
                || string.Equals(a: c.Suite, b: "blargg", comparisonType: StringComparison.Ordinal)
                || string.Equals(a: c.Suite, b: "gbmicrotest", comparisonType: StringComparison.Ordinal))
            .OrderBy(keySelector: static c => c.Suite, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static c => c.RelativePath, comparer: StringComparer.Ordinal)
            .ToList();

        Assert.SkipUnless(condition: cases.Count > 0, reason: "no DMG mooneye/blargg/gbmicrotest ROMs found in corpus.");

        var rows = new List<Row>(capacity: cases.Count);

        foreach (var romCase in cases) {
            var ares = RunAres(romCase: romCase);
            var old = RunOld(romCase: romCase);

            rows.Add(item: new Row(Case: romCase, Ares: ares, Old: old));
        }

        Report(rows: rows);

        // The scorecard is a measurement, not a gate on a specific number; only fail if the corpus produced no
        // decided ares results at all (a broken harness), so this surfaces real signal in CI without being brittle.
        var aresDecided = rows.Count(predicate: static r => r.Ares.Status is TestStatus.Pass or TestStatus.Fail);

        Assert.True(condition: aresDecided > 0, userMessage: "harness produced no decided ares-core results.");
    }

    private void Report(IReadOnlyList<Row> rows) {
        m_output.WriteLine(message: "=== ares-core conformance scorecard (DMG) ===");
        m_output.WriteLine(message: "corpus root: " + RomCatalog.Root);
        m_output.WriteLine(message: string.Empty);

        // Per-suite summary, ares vs old.
        m_output.WriteLine(message: "Suite          | ares P/F/I       | old P/F/I        | ares regressions | ares gains");
        m_output.WriteLine(message: "-------------- | ---------------- | ---------------- | ---------------- | ----------");

        foreach (var suite in rows.Select(selector: static r => r.Case.Suite).Distinct(comparer: StringComparer.Ordinal).OrderBy(keySelector: static s => s, comparer: StringComparer.Ordinal)) {
            var group = rows.Where(predicate: r => string.Equals(a: r.Case.Suite, b: suite, comparisonType: StringComparison.Ordinal)).ToList();

            var ares = Tally(group: group, selector: static r => r.Ares.Status);
            var old = Tally(group: group, selector: static r => r.Old.Status);
            var regressions = group.Count(predicate: static r => (r.Ares.Status == TestStatus.Fail) && (r.Old.Status == TestStatus.Pass));
            var gains = group.Count(predicate: static r => (r.Ares.Status == TestStatus.Pass) && (r.Old.Status == TestStatus.Fail));

            m_output.WriteLine(message: FormattableString.Invariant($"{suite,-14} | {ares,-16} | {old,-16} | {regressions,16} | {gains,10}"));
        }

        m_output.WriteLine(message: string.Empty);

        // Blargg families — the headline timing/functional suites — broken out by sub-folder for the brief's callouts.
        ReportBlarggFamilies(rows: rows);

        // The per-ROM ares failure list, with the old core's verdict beside each so regressions stand out.
        AppendDetail(title: "ARES FAILURES (status FAIL)", rows: rows, predicate: static r => r.Ares.Status == TestStatus.Fail);
        AppendDetail(title: "ARES INCONCLUSIVE (no result within cap)", rows: rows, predicate: static r => r.Ares.Status == TestStatus.Inconclusive);

        // Regressions vs old core, called out explicitly (the things to investigate).
        var regressionRows = rows.Where(predicate: static r => (r.Ares.Status == TestStatus.Fail) && (r.Old.Status == TestStatus.Pass)).ToList();

        m_output.WriteLine(message: FormattableString.Invariant($"REGRESSIONS — ares FAILS where old core PASSES ({regressionRows.Count}):"));

        if (regressionRows.Count == 0) {
            m_output.WriteLine(message: "  (none)");
        }
        else {
            foreach (var row in regressionRows.OrderBy(keySelector: static r => r.Case.RelativePath, comparer: StringComparer.Ordinal)) {
                m_output.WriteLine(message: FormattableString.Invariant($"  {row.Case.RelativePath} — {row.Ares.Detail}"));
            }
        }

        m_output.WriteLine(message: string.Empty);

        var gainRows = rows.Where(predicate: static r => (r.Ares.Status == TestStatus.Pass) && (r.Old.Status != TestStatus.Pass)).ToList();

        m_output.WriteLine(message: FormattableString.Invariant($"GAINS — ares PASSES where old core does NOT ({gainRows.Count}):"));

        if (gainRows.Count == 0) {
            m_output.WriteLine(message: "  (none)");
        }
        else {
            foreach (var row in gainRows.OrderBy(keySelector: static r => r.Case.RelativePath, comparer: StringComparer.Ordinal)) {
                m_output.WriteLine(message: FormattableString.Invariant($"  {row.Case.RelativePath} (old: {row.Old.Status})"));
            }
        }
    }

    private void ReportBlarggFamilies(IReadOnlyList<Row> rows) {
        var blargg = rows.Where(predicate: static r => string.Equals(a: r.Case.Suite, b: "blargg", comparisonType: StringComparison.Ordinal)).ToList();

        if (blargg.Count == 0) {
            return;
        }

        m_output.WriteLine(message: "blargg families (ares P/F/I vs old P/F/I):");

        foreach (var family in blargg.Select(selector: static r => BlarggFamily(relativePath: r.Case.RelativePath)).Distinct(comparer: StringComparer.Ordinal).OrderBy(keySelector: static f => f, comparer: StringComparer.Ordinal)) {
            var group = blargg.Where(predicate: r => string.Equals(a: BlarggFamily(relativePath: r.Case.RelativePath), b: family, comparisonType: StringComparison.Ordinal)).ToList();
            var ares = Tally(group: group, selector: static r => r.Ares.Status);
            var old = Tally(group: group, selector: static r => r.Old.Status);

            m_output.WriteLine(message: FormattableString.Invariant($"  {family,-24} ares {ares,-12} | old {old}"));
        }

        m_output.WriteLine(message: string.Empty);
    }

    // The blargg sub-suite folder (cpu_instrs, instr_timing, mem_timing, mem_timing-2, interrupt_time, ...).
    private static string BlarggFamily(string relativePath) {
        var normalized = relativePath.Replace(oldChar: '\\', newChar: '/');
        var parts = normalized.Split(separator: '/');

        // parts[0] == "blargg"; the family is the next path segment (or the file stem for root ROMs like halt_bug).
        return (parts.Length >= 2) ? parts[1].Replace(oldValue: ".gb", newValue: string.Empty, comparisonType: StringComparison.OrdinalIgnoreCase) : normalized;
    }

    private void AppendDetail(string title, IReadOnlyList<Row> rows, Func<Row, bool> predicate) {
        var matching = rows.Where(predicate: predicate).OrderBy(keySelector: static r => r.Case.RelativePath, comparer: StringComparer.Ordinal).ToList();

        m_output.WriteLine(message: FormattableString.Invariant($"{title} ({matching.Count}):"));

        if (matching.Count == 0) {
            m_output.WriteLine(message: "  (none)");
        }
        else {
            foreach (var row in matching) {
                m_output.WriteLine(message: FormattableString.Invariant($"  [old: {row.Old.Status,-12}] {row.Case.RelativePath} — {row.Ares.Detail}"));
            }
        }

        m_output.WriteLine(message: string.Empty);
    }

    private static string Tally(IReadOnlyList<Row> group, Func<Row, TestStatus> selector) {
        var pass = group.Count(predicate: r => selector(arg: r) == TestStatus.Pass);
        var fail = group.Count(predicate: r => selector(arg: r) == TestStatus.Fail);
        var inconclusive = group.Count(predicate: r => selector(arg: r) == TestStatus.Inconclusive);

        return FormattableString.Invariant($"{pass}/{fail}/{inconclusive}");
    }

    // === ares-core runners (adapted from Protocol/) ===

    private static Outcome RunAres(RomCase romCase) {
        byte[] rom;

        try {
            rom = File.ReadAllBytes(path: romCase.FullPath);
        }
        catch (IOException exception) {
            return new(Status: TestStatus.Inconclusive, Detail: "read error: " + exception.Message);
        }

#pragma warning disable CA1031 // A test ROM can fault the core many ways; record the fault, never crash the run.
        try {
            var machine = new AresMachine(rom: rom, color: false);

            return romCase.Protocol switch {
                ResultProtocol.Mooneye => RunAresMooneye(machine: machine),
                ResultProtocol.Blargg => RunAresBlargg(machine: machine),
                ResultProtocol.GbMicrotest => RunAresGbMicrotest(machine: machine, frameLimit: romCase.FrameLimit),
                _ => new(Status: TestStatus.Inconclusive, Detail: "unsupported protocol"),
            };
        }
        catch (Exception exception) {
            return new(Status: TestStatus.Inconclusive, Detail: exception.GetType().Name + ": " + exception.Message);
        }
#pragma warning restore CA1031
    }

    private static Outcome RunAresMooneye(AresMachine machine) {
        var cpu = machine.Cpu;
        var instructions = 0L;

        while (instructions < MooneyeInstructionCap) {
            // The LD B,B (0x40) breakpoint is the result point; guard a few leading instructions so an incidental
            // early 0x40 in start-up code cannot be mistaken for the terminal.
            if ((instructions > 16L) && (machine.Peek(address: cpu.ProgramCounter) == 0x40)) {
                break;
            }

            machine.Step();
            instructions += 1L;
        }

        var pass = (cpu.RegisterB == 3) && (cpu.RegisterC == 5) && (cpu.RegisterD == 8) && (cpu.RegisterE == 13) && (cpu.RegisterH == 21) && (cpu.RegisterL == 34);
        var fail = (cpu.RegisterB == 0x42) && (cpu.RegisterC == 0x42) && (cpu.RegisterD == 0x42) && (cpu.RegisterE == 0x42) && (cpu.RegisterH == 0x42) && (cpu.RegisterL == 0x42);

        if (pass) {
            return new(Status: TestStatus.Pass, Detail: "registers = 3/5/8/13/21/34");
        }

        if (fail) {
            return new(Status: TestStatus.Fail, Detail: DescribeRegisters(cpu: cpu));
        }

        return new(Status: TestStatus.Inconclusive, Detail: "no result signal; " + DescribeRegisters(cpu: cpu));
    }

    private static Outcome RunAresBlargg(AresMachine machine) {
        var sawRunning = false;
        var steps = 0L;

        while (steps < BlarggInstructionCap) {
            machine.Step();

            // The 0xA000 memory protocol is the only reliable channel (serial is per-bit on this core); sample
            // periodically — polling every step is needlessly expensive.
            if ((++steps & 0x3FFFL) != 0L) {
                continue;
            }

            if ((machine.Peek(address: 0xA001) == 0xDE) && (machine.Peek(address: 0xA002) == 0xB0) && (machine.Peek(address: 0xA003) == 0x61)) {
                var status = machine.Peek(address: 0xA000);

                if (status == 0x80) {
                    sawRunning = true;
                }
                else if (sawRunning) {
                    return (status == 0x00)
                        ? new(Status: TestStatus.Pass, Detail: "result code 0x00")
                        : new(Status: TestStatus.Fail, Detail: FormattableString.Invariant($"result code 0x{status:X2}"));
                }
            }
        }

        return new(Status: TestStatus.Inconclusive, Detail: "no result within instruction cap");
    }

    private static Outcome RunAresGbMicrotest(AresMachine machine, int frameLimit) {
        var frames = (frameLimit > 0) ? frameLimit : 2;

        for (var frame = 0; frame < frames; frame += 1) {
            var guard = 0L;

            while (!machine.Ppu.ConsumeFrameReady() && (guard < FrameInstructionGuard)) {
                machine.Step();
                guard += 1L;
            }
        }

        var result = machine.Peek(address: 0xFF82);
        var actual = machine.Peek(address: 0xFF80);
        var expected = machine.Peek(address: 0xFF81);

        return result switch {
            0x01 => new(Status: TestStatus.Pass, Detail: "0xFF82 = 0x01"),
            0xFF => new(Status: TestStatus.Fail, Detail: FormattableString.Invariant($"0xFF82 = 0xFF (actual 0x{actual:X2}, expected 0x{expected:X2})")),
            _ => new(Status: TestStatus.Inconclusive, Detail: FormattableString.Invariant($"0xFF82 = 0x{result:X2} (never resolved)")),
        };
    }

    private static string DescribeRegisters(AresCpu cpu) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"B={cpu.RegisterB:X2} C={cpu.RegisterC:X2} D={cpu.RegisterD:X2} E={cpu.RegisterE:X2} H={cpu.RegisterH:X2} L={cpu.RegisterL:X2}"
        );

    // === old core, run through the existing engine for an apples-to-apples comparison column ===

    private static Outcome RunOld(RomCase romCase) {
        var outcome = ConformanceEngine.Execute(romCase: romCase);

        return new(Status: outcome.Status, Detail: outcome.Detail);
    }

    private sealed record Outcome(TestStatus Status, string Detail);

    private sealed record Row(RomCase Case, Outcome Ares, Outcome Old);
}
