using System.Globalization;
using System.Text;

namespace Puck.HumbleGamingBrick.Conformance;

/// <summary>The overall judgement the corpus renders on the "cycle-accurate" claim.</summary>
public enum Verdict {
    /// <summary>No timing tests were decided (e.g. the corpus is unavailable).</summary>
    Insufficient,
    /// <summary>Timing failures are broad, or a make-or-break timing suite fails: the claim is not supported.</summary>
    Disproven,
    /// <summary>The headline timing suites pass but coverage is not complete: the claim is partially supported.</summary>
    PartiallyProven,
    /// <summary>The make-or-break timing suites pass and timing coverage is near-complete: the claim is supported.</summary>
    Proven,
}

/// <summary>Aggregates raw <see cref="TestOutcome"/> results into per-suite and per-subsystem rollups and computes
/// the cycle-accuracy verdict, and renders the whole thing as a Markdown scorecard. The verdict weighs the
/// make-or-break timing suites (blargg instr_timing/mem_timing and mooneye timer/ppu/oam_dma) above all else.</summary>
public static class Scorecard {
    /// <summary>The pass rate a scope must reach (over decided tests) to count as fully accurate.</summary>
    public const double ProvenThreshold = 0.98d;

    /// <summary>The mooneye-timing pass rate below which the claim is considered disproven.</summary>
    public const double DisprovenThreshold = 0.80d;

    /// <summary>A per-grouping tally of outcomes.</summary>
    /// <param name="Name">The group label.</param>
    /// <param name="Pass">The number of passing tests.</param>
    /// <param name="Fail">The number of failing tests.</param>
    /// <param name="Inconclusive">The number of tests with no clear result.</param>
    public sealed record Tally(string Name, int Pass, int Fail, int Inconclusive) {
        /// <summary>Gets the number of tests that produced a clear pass or fail.</summary>
        public int Decided =>
            (Pass + Fail);

        /// <summary>Gets the total number of tests in the group.</summary>
        public int Total =>
            (Pass + Fail + Inconclusive);

        /// <summary>Gets the pass rate over decided tests (1.0 when none were decided).</summary>
        public double PassRate =>
            (Decided == 0) ? 1.0d : ((double)Pass / Decided);
    }

    /// <summary>Computes the overall verdict from a set of outcomes.</summary>
    /// <param name="outcomes">The outcomes to judge.</param>
    /// <returns>The cycle-accuracy verdict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcomes"/> is <see langword="null"/>.</exception>
    public static Verdict ComputeVerdict(IReadOnlyCollection<TestOutcome> outcomes) {
        ArgumentNullException.ThrowIfNull(argument: outcomes);

        var timing = outcomes.Where(predicate: static o => o.Case.Tier == TestTier.Timing).ToList();

        if (timing.Count(predicate: static o => o.Status is TestStatus.Pass or TestStatus.Fail) == 0) {
            return Verdict.Insufficient;
        }

        // The make-or-break timing suites: blargg CPU/memory timing and mooneye timer/ppu/oam_dma.
        var criticalFails = outcomes.Count(predicate: static o =>
            (o.Status == TestStatus.Fail)
            && (
                (string.Equals(a: o.Case.Suite, b: "blargg", comparisonType: StringComparison.Ordinal) && (o.Case.Subsystem == TestSubsystem.CpuTiming))
                || (string.Equals(a: o.Case.Suite, b: "mooneye", comparisonType: StringComparison.Ordinal) && (o.Case.Subsystem is TestSubsystem.TimerDiv or TestSubsystem.PpuTiming or TestSubsystem.OamDma))
            ));

        var blarggTimingFails = outcomes.Count(predicate: static o =>
            (o.Status == TestStatus.Fail)
            && string.Equals(a: o.Case.Suite, b: "blargg", comparisonType: StringComparison.Ordinal)
            && (o.Case.Subsystem == TestSubsystem.CpuTiming));

        var mooneyeTiming = outcomes
            .Where(predicate: static o => string.Equals(a: o.Case.Suite, b: "mooneye", comparisonType: StringComparison.Ordinal) && (o.Case.Tier == TestTier.Timing))
            .ToList();
        var mooneyeRate = Rate(outcomes: mooneyeTiming);
        var timingRate = Rate(outcomes: timing);

        if ((blarggTimingFails > 0) || (mooneyeRate < DisprovenThreshold)) {
            return Verdict.Disproven;
        }

        if ((criticalFails == 0) && (timingRate >= ProvenThreshold)) {
            return Verdict.Proven;
        }

        return Verdict.PartiallyProven;
    }

    /// <summary>Renders the full Markdown scorecard: the verdict, per-suite and per-subsystem rollups, and the
    /// list of failing and inconclusive ROMs (the accuracy backlog).</summary>
    /// <param name="outcomes">The outcomes to report.</param>
    /// <returns>The Markdown document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcomes"/> is <see langword="null"/>.</exception>
    public static string ToMarkdown(IReadOnlyCollection<TestOutcome> outcomes) {
        ArgumentNullException.ThrowIfNull(argument: outcomes);

        var builder = new StringBuilder();
        var verdict = ComputeVerdict(outcomes: outcomes);

        builder.AppendLine(value: "# GB/GBC cycle-accuracy scorecard");
        builder.AppendLine();
        builder.AppendLine(value: FormattableString.Invariant($"**Verdict: {Describe(verdict: verdict)}**"));
        builder.AppendLine();
        builder.AppendLine(value: FormattableString.Invariant($"Generated from {outcomes.Count} test runs across the focused cycle-accuracy corpus. Tier-2 (timing) results are the discriminators; tier-1 (functional) and CGB results are reported for context."));
        builder.AppendLine();

        AppendTallyTable(
            builder: builder,
            title: "## Per-suite",
            tallies: outcomes
                .GroupBy(keySelector: static o => o.Case.Suite, comparer: StringComparer.Ordinal)
                .Select(selector: group => MakeTally(name: group.Key, outcomes: group))
                .OrderBy(keySelector: static t => t.Name, comparer: StringComparer.Ordinal)
        );

        AppendTallyTable(
            builder: builder,
            title: "## Per-subsystem (timing tier)",
            tallies: outcomes
                .Where(predicate: static o => o.Case.Tier == TestTier.Timing)
                .GroupBy(keySelector: static o => o.Case.Subsystem)
                .Select(selector: group => MakeTally(name: group.Key.ToString(), outcomes: group))
                .OrderBy(keySelector: static t => t.Name, comparer: StringComparer.Ordinal)
        );

        AppendTallyTable(
            builder: builder,
            title: "## Per-tier",
            tallies: outcomes
                .GroupBy(keySelector: static o => o.Case.Tier)
                .Select(selector: group => MakeTally(name: group.Key.ToString(), outcomes: group))
                .OrderBy(keySelector: static t => t.Name, comparer: StringComparer.Ordinal)
        );

        AppendList(builder: builder, title: "## Failing tests (accuracy backlog)", outcomes: outcomes, status: TestStatus.Fail);
        AppendList(builder: builder, title: "## Inconclusive tests (no result / harness gaps)", outcomes: outcomes, status: TestStatus.Inconclusive);

        return builder.ToString();
    }

    private static double Rate(IReadOnlyCollection<TestOutcome> outcomes) {
        var pass = outcomes.Count(predicate: static o => o.Status == TestStatus.Pass);
        var decided = outcomes.Count(predicate: static o => o.Status is TestStatus.Pass or TestStatus.Fail);

        return (decided == 0) ? 1.0d : ((double)pass / decided);
    }

    private static Tally MakeTally(string name, IEnumerable<TestOutcome> outcomes) {
        var pass = 0;
        var fail = 0;
        var inconclusive = 0;

        foreach (var outcome in outcomes) {
            switch (outcome.Status) {
                case TestStatus.Pass:
                    pass += 1;

                    break;
                case TestStatus.Fail:
                    fail += 1;

                    break;
                default:
                    inconclusive += 1;

                    break;
            }
        }

        return new(Name: name, Pass: pass, Fail: fail, Inconclusive: inconclusive);
    }

    private static void AppendTallyTable(StringBuilder builder, string title, IEnumerable<Tally> tallies) {
        builder.AppendLine(value: title);
        builder.AppendLine();
        builder.AppendLine(value: "| Group | Pass | Fail | Inconclusive | Pass rate |");
        builder.AppendLine(value: "| --- | ---: | ---: | ---: | ---: |");

        foreach (var tally in tallies) {
            var rate = (tally.PassRate * 100.0d).ToString(format: "F1", provider: CultureInfo.InvariantCulture);

            builder.AppendLine(value: FormattableString.Invariant($"| {tally.Name} | {tally.Pass} | {tally.Fail} | {tally.Inconclusive} | {rate}% |"));
        }

        builder.AppendLine();
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyCollection<TestOutcome> outcomes, TestStatus status) {
        var matching = outcomes
            .Where(predicate: o => o.Status == status)
            .OrderBy(keySelector: static o => o.Case.RelativePath, comparer: StringComparer.Ordinal)
            .ToList();

        builder.AppendLine(value: title);
        builder.AppendLine();

        if (matching.Count == 0) {
            builder.AppendLine(value: "_None._");
            builder.AppendLine();

            return;
        }

        foreach (var outcome in matching) {
            builder.AppendLine(value: FormattableString.Invariant($"- `{outcome.Case.RelativePath}` ({outcome.Case.Model}) — {outcome.Detail}"));
        }

        builder.AppendLine();
    }

    private static string Describe(Verdict verdict) =>
        verdict switch {
            Verdict.Proven => "cycle-accuracy PROVEN",
            Verdict.PartiallyProven => "cycle-accuracy PARTIALLY PROVEN",
            Verdict.Disproven => "cycle-accuracy DISPROVEN",
            _ => "INSUFFICIENT DATA",
        };
}
