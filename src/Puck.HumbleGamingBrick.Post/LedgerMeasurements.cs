namespace Puck.HumbleGamingBrick.Post;

/// <summary>What a run learned about the ledger, gathered across every ledger stage: which suites it discovered (and
/// exactly which rows), and every row it measured. <see cref="LedgerAcceptance"/> folds this with the existing ledger
/// into the run's candidate. Stages report from their own threads, so every mutation takes the lock.</summary>
internal sealed class LedgerMeasurements {
    private readonly HashSet<(string Suite, string Path, string Model)> m_discoveredKeys = [];
    private readonly HashSet<string> m_discoveredSuites = new(comparer: StringComparer.Ordinal);
    private readonly List<LedgerEntry> m_entries = [];
    private readonly Lock m_lock = new();

    /// <summary>Gets the keys of every case a stage discovered on disk.</summary>
    public IReadOnlySet<(string Suite, string Path, string Model)> DiscoveredKeys {
        get {
            lock (m_lock) {
                return m_discoveredKeys.ToHashSet();
            }
        }
    }
    /// <summary>Gets the suites whose discovery ran, whether or not it found anything.</summary>
    public IReadOnlySet<string> DiscoveredSuites {
        get {
            lock (m_lock) {
                return m_discoveredSuites.ToHashSet(comparer: StringComparer.Ordinal);
            }
        }
    }
    /// <summary>Gets every measured row, in the order stages reported them.</summary>
    public IReadOnlyList<LedgerEntry> Entries {
        get {
            lock (m_lock) {
                return m_entries.ToArray();
            }
        }
    }

    /// <summary>Records that a stage's discovery ran over <paramref name="suites"/> and found <paramref name="keys"/>.</summary>
    /// <param name="suites">Every suite the stage is responsible for.</param>
    /// <param name="keys">The keys of the cases it found.</param>
    public void NoteDiscovery(IEnumerable<string> suites, IEnumerable<(string Suite, string Path, string Model)> keys) {
        lock (m_lock) {
            m_discoveredSuites.UnionWith(other: suites);
            m_discoveredKeys.UnionWith(other: keys);
        }
    }
    /// <summary>Records freshly measured rows.</summary>
    /// <param name="entries">The rows.</param>
    public void NoteMeasured(IEnumerable<LedgerEntry> entries) {
        lock (m_lock) {
            m_entries.AddRange(collection: entries);
        }
    }
}
