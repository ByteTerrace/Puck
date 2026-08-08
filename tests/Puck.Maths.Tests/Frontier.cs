using System.Collections.Concurrent;

namespace Puck.Maths.Tests;

/// <summary>
/// The rolling coverage frontier: a committed per-domain block counter <c>k</c> that advances on every GREEN run. A run
/// consuming a domain reads sample indices <c>[k·B, (k+1)·B)</c> from the house stratified sampler (see
/// <see cref="Domains"/>), then the assembly ledger advances <c>k</c> by ONE — but only when every law that ran passed —
/// so the next run takes the adjacent window and consecutive runs sweep contiguous ground. Persistence is
/// <c>frontier.json</c> — stable key order, written only by a GREEN run that actually consumed a domain — so successive
/// runs sweep fresh operands without re-covering ground, without churning the tree when nothing ran, and without ever
/// stepping the window past a failure.
/// </summary>
internal static class Frontier {
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, byte> ConsumedKeys = new();
    private static Model State = ArtifactJson.ReadOrDefault<Model>(path: TestPaths.Artifact(fileName: "frontier.json")) ?? new Model();

    /// <summary>Records that <paramref name="key"/> was consumed this run and returns its current block window.</summary>
    /// <param name="key">The domain key.</param>
    /// <param name="block">The block size to register when the domain is first seen.</param>
    /// <returns>The current counter <c>k</c> and the block size <c>B</c> for the domain.</returns>
    public static (long Index, int Block) Consume(string key, int block) {
        _ = ConsumedKeys.TryAdd(key: key, value: 0);

        lock (Gate) {
            var entry = State.Domains.Find(match: candidate => (candidate.Key == key));

            if (entry is null) {
                entry = new Entry { Key = key, Block = block, Index = 0L };

                State.Domains.Add(item: entry);
            }

            return (entry.Index, entry.Block);
        }
    }

    /// <summary>Advances the block counter of every domain consumed this run by one and rewrites the artifact when any
    /// counter moved — but only on a GREEN run. Two runs own nothing here: one that consumed no domain, and one in
    /// which any law failed.</summary>
    /// <remarks>
    /// The committed counter is what decides which operands the NEXT run sweeps, so advancing past a red would hand the
    /// re-run a different window and let the failure vanish unfixed — the mechanism that once let a latent divergence
    /// hide until a third consecutive run happened to land on it again. Leaving the counters where they are makes the
    /// re-run take the same window, the same derived seeds and the same indices, so a red reproduces exactly where it
    /// was found.
    /// Two properties of the gate are deliberate. It sits HERE, at persistence, and never at <see cref="Consume"/>: a
    /// domain must hand out its index while the sweep is running, so operand determinism WITHIN a run is untouched by
    /// how that run ends. And a single law failure withholds the advance for EVERY key, not just the failing case's: a
    /// red run's whole sweep is suspect, and a partial advance would leave the committed frontier in a state no run ever
    /// swept from.
    /// </remarks>
    /// <param name="lawsPassed">Whether every law case this session ran passed — <see cref="LedgerState.LawsPassed"/>.
    /// REQUIRED, so no caller can advance the frontier without stating the session's verdict.</param>
    /// <returns>The persisted per-domain counters after advancement, ordered by key; <see langword="null"/> when this
    /// run persisted nothing, because it consumed no domain or because a law failed.</returns>
    public static IReadOnlyList<(string Key, int Block, long Index)>? AdvanceAndPersist(bool lawsPassed) {
        lock (Gate) {
            if (ConsumedKeys.IsEmpty || !lawsPassed) {
                return null;
            }

            foreach (var entry in State.Domains) {
                if (ConsumedKeys.ContainsKey(key: entry.Key)) {
                    ++entry.Index;
                }
            }

            State.Domains.Sort(comparison: static (left, right) => string.CompareOrdinal(strA: left.Key, strB: right.Key));

            _ = ArtifactJson.WriteIfChanged(path: TestPaths.Artifact(fileName: "frontier.json"), content: ArtifactJson.Serialize(value: State));

            return [.. State.Domains.Select(selector: static entry => (entry.Key, entry.Block, entry.Index))];
        }
    }

    /// <summary>The persisted counter for one domain.</summary>
    internal sealed class Entry {
        /// <summary>Gets or sets the domain key.</summary>
        public string Key { get; set; } = "";
        /// <summary>Gets or sets the block size <c>B</c> — the number of sample indices consumed per run.</summary>
        public int Block { get; set; }
        /// <summary>Gets or sets the current block counter <c>k</c>; a consuming run takes indices
        /// <c>[k·B, (k+1)·B)</c> and leaves <c>k + 1</c> behind.</summary>
        public long Index { get; set; }
    }

    /// <summary>The persisted frontier document.</summary>
    internal sealed class Model {
        /// <summary>Gets the per-domain counters, ordered by key.</summary>
        public List<Entry> Domains { get; init; } = [];
    }
}
