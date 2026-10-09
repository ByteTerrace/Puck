using System.Globalization;

namespace Puck.Cli.Determinism;

/// <summary>Where two streams first disagree in one scenario: a document hash, or a tick and the first component of
/// its vector that differs.</summary>
/// <param name="Scenario">The scenario.</param>
/// <param name="Tick">The first divergent tick, counted from one, or zero for a document hash.</param>
/// <param name="Component">The differing document hash's name, or the first differing tick component.</param>
/// <param name="Left">The left stream's value.</param>
/// <param name="Right">The right stream's value.</param>
public sealed record DeterminismDivergence(string Scenario, int Tick, string Component, string Left, string Right) {
    /// <inheritdoc/>
    public override string ToString() => ((Tick == 0)
        ? $"{Scenario}: document {Component} is {Left} on the left and {Right} on the right"
        : $"{Scenario}: tick {Tick.ToString(provider: CultureInfo.InvariantCulture)} first diverges in {Component}: {Left} on the left, {Right} on the right");
}
/// <summary>What <c>puck determinism compare</c> counted and found.</summary>
/// <param name="Scenarios">The scenarios compared.</param>
/// <param name="Ticks">The ticks compared, summed over scenarios.</param>
/// <param name="Hashes">The hashes compared: every document hash and every component of every tick.</param>
/// <param name="Divergences">The first divergence of each scenario that diverges, in manifest order.</param>
public sealed record DeterminismComparison(int Scenarios, long Ticks, long Hashes, IReadOnlyList<DeterminismDivergence> Divergences) {
    /// <summary>Compares two streams of one manifest. A scenario is compared document hash by document hash, then
    /// tick by tick until its first divergence, which is the one reported for it: what follows a divergence is its
    /// consequence, not more evidence.</summary>
    /// <param name="left">One stream.</param>
    /// <param name="right">The other.</param>
    /// <param name="comparison">What was compared and found, or <see langword="null"/> when the streams cannot be
    /// compared.</param>
    /// <param name="refusal">Why the two streams cannot be compared, or empty.</param>
    /// <returns><see langword="true"/> when the streams were compared, whatever the outcome.</returns>
    public static bool TryCompare(DeterminismStream left, DeterminismStream right, out DeterminismComparison? comparison, out string refusal) {
        comparison = null;
        refusal = string.Empty;

        if (left.ManifestPin != right.ManifestPin) {
            refusal = $"the streams were recorded from different manifests ({left.ManifestPin} and {right.ManifestPin}); record both from one manifest";

            return false;
        }

        var leftNames = left.Scenarios.Select(selector: static scenario => $"{scenario.Name}/{scenario.Ticks.Count}").ToArray();
        var rightNames = right.Scenarios.Select(selector: static scenario => $"{scenario.Name}/{scenario.Ticks.Count}").ToArray();

        if (!leftNames.SequenceEqual(second: rightNames, comparer: StringComparer.Ordinal)) {
            refusal = $"the streams record different scenarios or tick counts ({string.Join(separator: ", ", values: leftNames)} against {string.Join(separator: ", ", values: rightNames)})";

            return false;
        }

        var divergences = new List<DeterminismDivergence>();
        var ticks = 0L;
        var hashes = 0L;

        for (var index = 0; (index < left.Scenarios.Count); index++) {
            var (a, b) = (left.Scenarios[index], right.Scenarios[index]);
            var aNames = a.Documents.Select(selector: static document => document.Name).ToArray();
            var bNames = b.Documents.Select(selector: static document => document.Name).ToArray();

            if (!aNames.SequenceEqual(second: bNames, comparer: StringComparer.Ordinal)) {
                var first = Enumerable.Range(start: 0, count: Math.Max(val1: aNames.Length, val2: bNames.Length)).First(predicate: position => ((position >= aNames.Length) || (position >= bNames.Length) || (aNames[position] != bNames[position])));

                divergences.Add(item: new DeterminismDivergence(
                    Component: "document-set",
                    Left: ((first < aNames.Length) ? aNames[first] : "(none)"),
                    Right: ((first < bNames.Length) ? bNames[first] : "(none)"),
                    Scenario: a.Name,
                    Tick: 0
                ));

                continue;
            }

            DeterminismDivergence? found = null;

            for (var document = 0; ((document < a.Documents.Count) && (found is null)); document++) {
                hashes++;

                if (a.Documents[document].Value != b.Documents[document].Value) {
                    found = new DeterminismDivergence(
                        Component: a.Documents[document].Name,
                        Left: a.Documents[document].Value,
                        Right: b.Documents[document].Value,
                        Scenario: a.Name,
                        Tick: 0
                    );
                }
            }
            for (var tick = 0; ((tick < a.Ticks.Count) && (found is null)); tick++) {
                ticks++;

                for (var component = 0; (component < a.Ticks[tick].Length); component++) {
                    hashes++;

                    if (a.Ticks[tick][component] != b.Ticks[tick][component]) {
                        found = new DeterminismDivergence(
                            Component: DeterminismStream.Components[component],
                            Left: a.Ticks[tick][component].ToString(format: "x16", provider: CultureInfo.InvariantCulture),
                            Right: b.Ticks[tick][component].ToString(format: "x16", provider: CultureInfo.InvariantCulture),
                            Scenario: a.Name,
                            Tick: (tick + 1)
                        );

                        break;
                    }
                }
            }

            if (found is not null) {
                divergences.Add(item: found);
            }
        }

        comparison = new DeterminismComparison(
            Divergences: divergences,
            Hashes: hashes,
            Scenarios: left.Scenarios.Count,
            Ticks: ticks
        );

        return true;
    }
}
