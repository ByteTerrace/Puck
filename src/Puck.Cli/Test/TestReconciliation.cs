using System.Globalization;
using Puck.World;

namespace Puck.Cli.Test;

/// <summary>What a leg's recorded run says about the document it was asked to measure.</summary>
internal enum TestReconciliationVerdict {
    /// <summary>The run reached the authored export tick and every declared row answered as it said it would, so the
    /// verdicts may be reported.</summary>
    Accepted,
    /// <summary>Nothing was measured: the run stopped early, a declared row has no recorded outcome, or a line never
    /// reached a handler at all. A usage refusal, not a failing verdict.</summary>
    Unmeasured,
    /// <summary>The run measured the world and a declared row answered something other than what it declared, so the
    /// world fails.</summary>
    Unexpected,
}
/// <summary>
/// Whether a leg's recorded run measured the document at all — it reached the tick the document declares its export
/// at, and every declared row answered the outcome it declared.
/// </summary>
/// <remarks>A run that stopped early still writes an export, of whatever tick it reached, and the verdicts read off
/// it answer a different question from the one the document asked — a gate that is true early and false late passes
/// there. So this check runs before any verdict is reported, and it separates "nothing was measured" (a usage
/// refusal) from "a step answered something the test did not ask for" (a failing world).</remarks>
internal static class TestReconciliation {
    // The refusals that mean the line never reached a handler: a verb the codec could not route, or a principal it
    // discarded. A handler that judged the line and said no is the world's own answer, and a row may declare it.
    private const string IngressRefusalMarker = "[wire.reject:";

    /// <summary>Judges a leg's reading against the document's own schedule.</summary>
    /// <param name="name">The world's name, for the refusal line.</param>
    /// <param name="schedule">The document's own declared schedule.</param>
    /// <param name="reading">What the leg wrote.</param>
    /// <param name="reason">Why the reading is not accepted, or <see langword="null"/>.</param>
    /// <returns>The verdict.</returns>
    public static TestReconciliationVerdict Judge(string name, TestSchedule schedule, TestReading reading, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: reading);
        ArgumentNullException.ThrowIfNull(argument: schedule);

        if (
            reading.Truncated ||
            (reading.ExportTick != schedule.ExportTick)
        ) {
            reason = $"{name} exported at tick {reading.ExportTick.ToString(provider: CultureInfo.InvariantCulture)}, authored {schedule.ExportTick.ToString(provider: CultureInfo.InvariantCulture)} — the run ended before the tick its schedule declares, so its verdicts answer a different question.";

            return TestReconciliationVerdict.Unmeasured;
        }

        if (reading.Submissions.Count != schedule.Rows.Count) {
            reason = $"{name} declares {schedule.Rows.Count.ToString(provider: CultureInfo.InvariantCulture)} scheduled row(s) and its manifest records {reading.Submissions.Count.ToString(provider: CultureInfo.InvariantCulture)} — a declared step with no recorded outcome is a step nothing can say ran.";

            return TestReconciliationVerdict.Unmeasured;
        }

        string? unexpected = null;

        for (var index = 0; (index < schedule.Rows.Count); index++) {
            var declared = schedule.Rows[index];
            var recorded = reading.Submissions[index];
            var at = $"{name} schedule.rows[{index.ToString(provider: CultureInfo.InvariantCulture)}]";

            if (
                (declared.Tick != recorded.Tick) ||
                !string.Equals(
                a: declared.Command,
                b: recorded.Command,
                comparisonType: StringComparison.Ordinal
            ) ||
                !string.Equals(
                a: declared.Principal,
                b: recorded.Principal,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                reason = $"{at} declares tick {declared.Tick.ToString(provider: CultureInfo.InvariantCulture)} {declared.Principal}: {declared.Command}, and the manifest's entry at that position is tick {recorded.Tick.ToString(provider: CultureInfo.InvariantCulture)} {recorded.Principal}: {recorded.Command}.";

                return TestReconciliationVerdict.Unmeasured;
            }

            if (Unmeasurable(
                recorded: recorded,
                why: out var why
            )) {
                reason = $"{at} at tick {declared.Tick.ToString(provider: CultureInfo.InvariantCulture)} {why}: {declared.Command}";

                return TestReconciliationVerdict.Unmeasured;
            }

            unexpected ??= Mismatch(
                at: at,
                declared: declared,
                recorded: recorded
            );
        }

        reason = unexpected;

        return ((unexpected is null)
            ? TestReconciliationVerdict.Accepted
            : TestReconciliationVerdict.Unexpected
        );
    }

    // What the test did not ask for, named with the row's index, its command and the outcome the manifest recorded.
    private static string? Mismatch(string at, TestScheduleRow declared, TestSubmission recorded) {
        if (!string.Equals(
            a: declared.Outcome,
            b: recorded.Outcome,
            comparisonType: StringComparison.Ordinal
        )) {
            return $"{at} expects '{declared.Outcome}' and the run recorded '{recorded.Outcome}'{((recorded.Detail is { } detail)
                ? $" — {detail}"
                : string.Empty)}: {declared.Command}";
        }

        if (
            (declared.Refusal is { } wanted) &&
            !(recorded.Detail ?? string.Empty).Contains(
            comparisonType: StringComparison.Ordinal,
            value: wanted
        )
        ) {
            return $"{at} expects a refusal carrying '{wanted}' and the run recorded '{recorded.Detail ?? string.Empty}': {declared.Command}";
        }

        return null;
    }
    // The outcomes that say the runner, not the world, is what answered.
    private static bool Unmeasurable(TestSubmission recorded, out string why) {
        why = recorded.Outcome switch {
            WorldScheduleSection.OutcomeUnreached => "was never reached",
            WorldScheduleSection.OutcomePending => "was still awaiting its answer when the export was written",
            WorldScheduleSection.OutcomeUnroutable => $"had no ingress ({recorded.Detail ?? string.Empty})",
            WorldScheduleSection.OutcomeFaulted => $"crashed its handler ({recorded.Detail ?? string.Empty})",
            _ => string.Empty,
        };

        if (
            (why.Length == 0) &&
            (recorded.Outcome == WorldScheduleSection.OutcomeRefused) &&
            (recorded.Detail ?? string.Empty).Contains(
            comparisonType: StringComparison.Ordinal,
            value: IngressRefusalMarker
        )
        ) {
            why = $"never reached a handler ({recorded.Detail})";
        }

        return (why.Length != 0);
    }
}
