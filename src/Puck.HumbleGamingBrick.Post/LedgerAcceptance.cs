namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Turns a run's <see cref="LedgerMeasurements"/> into a candidate ledger and decides whether the candidate may
/// replace <c>Expectations.json</c>. The candidate is the existing ledger with every measured row replaced, every
/// row of a discovered suite whose case is no longer on disk removed, and every row of an undiscovered suite (an
/// unselected tier or filter, a corpus this checkout does not carry) carried over unchanged — so the diff between the
/// two only ever shows what this run measured. Accepting refuses when a stage ended in infrastructure failure or a
/// case could not be measured (a ledger built from an incomplete run is worse than no ledger), when a recorded verdict
/// regressed unless that is acknowledged, and when a recorded case vanished unless that is acknowledged. A recorded
/// fail resolving to a pass is the one direction accepted on its own.
/// </summary>
internal static class LedgerAcceptance {
    /// <summary>What changed between the existing ledger and a candidate.</summary>
    /// <param name="Ratcheted">Rows whose recorded fail now passes.</param>
    /// <param name="Regressed">Rows whose recorded verdict moved anywhere else.</param>
    /// <param name="Dropped">Recorded rows the candidate no longer carries.</param>
    /// <param name="Added">Candidate rows the ledger did not have.</param>
    public sealed record Delta(IReadOnlyList<string> Ratcheted, IReadOnlyList<string> Regressed, IReadOnlyList<string> Dropped, IReadOnlyList<string> Added) {
        /// <summary>Gets a value indicating whether the candidate differs from the ledger at all.</summary>
        public bool IsEmpty =>
            ((Ratcheted.Count == 0) && (Regressed.Count == 0) && (Dropped.Count == 0) && (Added.Count == 0));

        /// <summary>Writes every changed row to the console, one line each, prefixed by its kind.</summary>
        public void Print() {
            foreach (var line in Ratcheted) {
                Console.Out.WriteLine(value: $"ratchet: {line}");
            }

            foreach (var line in Regressed) {
                Console.Out.WriteLine(value: $"regression: {line}");
            }

            foreach (var line in Dropped) {
                Console.Out.WriteLine(value: $"dropped: {line}");
            }

            foreach (var line in Added) {
                Console.Out.WriteLine(value: $"added: {line}");
            }
        }
    }

    /// <summary>Builds the candidate ledger from a run's measurements.</summary>
    /// <param name="existing">The ledger the run gated against.</param>
    /// <param name="measurements">What the run discovered and measured.</param>
    /// <returns>The candidate rows, keyed like the ledger.</returns>
    public static IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry> BuildCandidate(IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry> existing, LedgerMeasurements measurements) {
        var discoveredSuites = measurements.DiscoveredSuites;
        var discoveredKeys = measurements.DiscoveredKeys;
        var candidate = existing
            .Where(predicate: pair => (!discoveredSuites.Contains(item: pair.Value.Suite) || discoveredKeys.Contains(item: pair.Key)))
            .ToDictionary(
            elementSelector: static pair => pair.Value,
            keySelector: static pair => pair.Key
        );

        foreach (var entry in measurements.Entries) {
            candidate[entry.Key] = entry;
        }

        return candidate;
    }
    /// <summary>Compares a candidate to the existing ledger.</summary>
    /// <param name="existing">The existing ledger.</param>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The changed rows by kind.</returns>
    public static Delta Compare(IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry> existing, IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry> candidate) {
        var ratcheted = new List<string>();
        var regressed = new List<string>();
        var dropped = new List<string>();
        var added = new List<string>();

        foreach (var old in Ordered(entries: existing.Values)) {
            if (!candidate.TryGetValue(
                key: old.Key,
                value: out var current
            )) {
                dropped.Add(item: $"{Name(entry: old)} recorded {old.Outcome}, no longer discovered");

                continue;
            }

            if (old.Outcome == current.Outcome) {
                continue;
            }

            if (
                (old.Outcome == LedgerOutcome.Fail) &&
                (current.Outcome == LedgerOutcome.Pass)
            ) {
                ratcheted.Add(item: $"{Name(entry: old)} recorded fail -> now pass");
            } else {
                regressed.Add(item: $"{Name(entry: old)} recorded {old.Outcome} -> now {current.Outcome} ({current.Reason})");
            }
        }

        foreach (var current in Ordered(entries: candidate.Values)) {
            if (!existing.ContainsKey(key: current.Key)) {
                added.Add(item: $"{Name(entry: current)} {current.Outcome}");
            }
        }

        return new Delta(
            Added: added,
            Dropped: dropped,
            Ratcheted: ratcheted,
            Regressed: regressed
        );
    }
    /// <summary>Decides whether a candidate may replace the ledger.</summary>
    /// <param name="delta">The candidate's changes.</param>
    /// <param name="blockers">Reasons the run itself is not trustworthy (an infrastructure failure, an unmeasured case), or empty.</param>
    /// <param name="acceptRegressions">Whether a regressed verdict is acknowledged.</param>
    /// <param name="acceptShrink">Whether a vanished case is acknowledged.</param>
    /// <returns>The refusal reasons; empty when the candidate may be written.</returns>
    public static IReadOnlyList<string> Refusals(Delta delta, IReadOnlyList<string> blockers, bool acceptRegressions, bool acceptShrink) {
        var refusals = new List<string>(collection: blockers);

        if (
            (delta.Regressed.Count > 0) &&
            !acceptRegressions
        ) {
            refusals.Add(item: $"{delta.Regressed.Count} case(s) regressed from a recorded verdict (pass --accept-regressions to acknowledge)");
        }

        if (
            (delta.Dropped.Count > 0) &&
            !acceptShrink
        ) {
            refusals.Add(item: $"{delta.Dropped.Count} recorded case(s) are no longer discovered (pass --accept-shrink to acknowledge)");
        }

        return refusals;
    }

    private static string Name(LedgerEntry entry) =>
        $"{entry.Suite}/{entry.Path}[{entry.Model}]";
    private static IEnumerable<LedgerEntry> Ordered(IEnumerable<LedgerEntry> entries) =>
        entries
            .OrderBy(
            keySelector: static entry => entry.Suite,
            comparer: StringComparer.Ordinal
        )
            .ThenBy(
            keySelector: static entry => entry.Path,
            comparer: StringComparer.Ordinal
        )
            .ThenBy(
            keySelector: static entry => entry.Model,
            comparer: StringComparer.Ordinal
        );
}
