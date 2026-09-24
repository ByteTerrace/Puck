using System.Globalization;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;
using Puck.World;

namespace Puck.Cli.Counters;

/// <summary>
/// What two counter readings must agree on, by each count's <see cref="WorkClass"/>. Across the two backends of one
/// report, only <see cref="WorkClass.Deterministic"/> counts and the passes' states are held equal. Between two reports,
/// each backend's run is held to itself: deterministic and per-backend-deterministic counts equal, an allocation
/// reading equally zero or equally not zero, and pacing counts never compared. A count one side has and the other lacks
/// is a difference, and so is a count whose class moved.
/// </summary>
internal static class CountersComparison {
    private const string Absent = "absent";

    /// <summary>Compares the deterministic counts and pass states of two backends' runs of one workload.</summary>
    /// <param name="left">The first backend's run.</param>
    /// <param name="right">The second backend's run.</param>
    /// <returns>One line per difference, naming the kind, pass and node; empty when the runs agree.</returns>
    public static IReadOnlyList<string> AcrossBackends(WorldCountersRun left, WorldCountersRun right) {
        var differences = new List<string>();
        var leftCounts = Index(run: left);
        var rightCounts = Index(run: right);

        foreach (var key in leftCounts.Keys.Union(second: rightCounts.Keys).Order()) {
            var leftCount = leftCounts.GetValueOrDefault(key: key);
            var rightCount = rightCounts.GetValueOrDefault(key: key);

            if (((leftCount?.Class ?? rightCount!.Class) != WorkClass.Deterministic) || (leftCount?.Value == rightCount?.Value)) {
                continue;
            }

            differences.Add(item: $"{Describe(key: key, workClass: WorkClass.Deterministic)} {left.Backend}={Value(count: leftCount)} {right.Backend}={Value(count: rightCount)}");
        }

        ComparePasses(
            differences: differences,
            left: left,
            leftName: left.Backend,
            prefix: string.Empty,
            right: right,
            rightName: right.Backend
        );

        return differences;
    }
    /// <summary>Compares two reports backend by backend.</summary>
    /// <param name="left">The first report.</param>
    /// <param name="right">The second report.</param>
    /// <returns>One line per difference, each naming its backend and the kind, pass and node; empty when every
    /// comparable count agrees.</returns>
    public static IReadOnlyList<string> Reports(WorldCountersReport left, WorldCountersReport right) {
        var differences = new List<string>();
        var leftRuns = left.Runs.ToDictionary(keySelector: static run => run.Backend, comparer: StringComparer.Ordinal);
        var rightRuns = right.Runs.ToDictionary(keySelector: static run => run.Backend, comparer: StringComparer.Ordinal);

        foreach (var backend in leftRuns.Keys.Union(second: rightRuns.Keys).Order(comparer: StringComparer.Ordinal)) {
            if (!leftRuns.TryGetValue(key: backend, value: out var leftRun) || !rightRuns.TryGetValue(key: backend, value: out var rightRun)) {
                differences.Add(item: $"{backend}: the run is {((leftRun is null) ? "only in the right report" : "only in the left report")}");

                continue;
            }

            var leftCounts = Index(run: leftRun);
            var rightCounts = Index(run: rightRun);

            foreach (var key in leftCounts.Keys.Union(second: rightCounts.Keys).Order()) {
                var leftCount = leftCounts.GetValueOrDefault(key: key);
                var rightCount = rightCounts.GetValueOrDefault(key: key);
                var workClass = (leftCount?.Class ?? rightCount!.Class);

                if ((leftCount is not null) && (rightCount is not null) && (leftCount.Class != rightCount.Class)) {
                    differences.Add(item: $"{backend}: {Describe(key: key, workClass: workClass)} class left={EnumWireName<WorkClass>.Of(value: leftCount.Class)} right={EnumWireName<WorkClass>.Of(value: rightCount.Class)}");

                    continue;
                }

                var agrees = workClass switch {
                    WorkClass.Pacing => true,
                    WorkClass.AllocationZeroNonzero => ((leftCount is not null) && (rightCount is not null) && ((leftCount.Value == 0L) == (rightCount.Value == 0L))),
                    _ => (leftCount?.Value == rightCount?.Value),
                };

                if (!agrees) {
                    differences.Add(item: $"{backend}: {Describe(key: key, workClass: workClass)} left={Value(count: leftCount)} right={Value(count: rightCount)}");
                }
            }

            ComparePasses(
                differences: differences,
                left: leftRun,
                leftName: "left",
                prefix: $"{backend}: ",
                right: rightRun,
                rightName: "right"
            );
        }

        return differences;
    }

    private static void ComparePasses(List<string> differences, WorldCountersRun left, string leftName, WorldCountersRun right, string rightName, string prefix) {
        var leftPasses = left.Passes.ToDictionary(keySelector: static pass => (pass.Node, pass.Label), elementSelector: static pass => pass.State);
        var rightPasses = right.Passes.ToDictionary(keySelector: static pass => (pass.Node, pass.Label), elementSelector: static pass => pass.State);

        foreach (var (node, label) in leftPasses.Keys.Union(second: rightPasses.Keys).OrderBy(keySelector: static pass => pass.Node, comparer: StringComparer.Ordinal).ThenBy(keySelector: static pass => pass.Label, comparer: StringComparer.Ordinal)) {
            var leftState = (leftPasses.TryGetValue(key: (node, label), value: out var leftFound) ? EnumWireName<GpuPassState>.Of(value: leftFound) : Absent);
            var rightState = (rightPasses.TryGetValue(key: (node, label), value: out var rightFound) ? EnumWireName<GpuPassState>.Of(value: rightFound) : Absent);

            if (!string.Equals(a: leftState, b: rightState, comparisonType: StringComparison.Ordinal)) {
                differences.Add(item: $"{prefix}pass state node={node} pass={label} {leftName}={leftState} {rightName}={rightState}");
            }
        }
    }
    private static string Describe(CountKey key, WorkClass workClass) =>
        $"{EnumWireName<WorkClass>.Of(value: workClass)} kind={key.Kind} pass={(key.Pass ?? "-")} node={(key.Node ?? "-")} source={key.Source}";
    private static Dictionary<CountKey, WorldCount> Index(WorldCountersRun run) {
        var index = new Dictionary<CountKey, WorldCount>();

        foreach (var count in run.Counts) {
            index[new CountKey(
                Kind: count.Kind,
                Node: count.Node,
                Pass: count.Pass,
                Source: count.Source
            )] = count;
        }

        return index;
    }
    private static string Value(WorldCount? count) =>
        ((count is null)
            ? Absent
            : count.Value.ToString(provider: CultureInfo.InvariantCulture)
        );

    // Where a count was read; the order a difference list is printed in.
    private readonly record struct CountKey(string Source, string? Node, string? Pass, string Kind) : IComparable<CountKey> {
        public int CompareTo(CountKey other) {
            var order = string.CompareOrdinal(strA: Source, strB: other.Source);

            order = ((order != 0) ? order : string.CompareOrdinal(strA: Node, strB: other.Node));
            order = ((order != 0) ? order : string.CompareOrdinal(strA: Pass, strB: other.Pass));

            return ((order != 0) ? order : string.CompareOrdinal(strA: Kind, strB: other.Kind));
        }
    }
}
