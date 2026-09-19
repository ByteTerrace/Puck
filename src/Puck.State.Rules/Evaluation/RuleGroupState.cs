using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>One group's progress: where a staged cursor stands, whether the group is running, and whether a
/// fixpoint group breached its pass ceiling and is waiting to be re-armed.</summary>
/// <param name="Step">The staged cursor's index into the group's members; 0 for a fixpoint group.</param>
/// <param name="Running">Whether a staged group has started and has not passed its terminal step.</param>
/// <param name="Breached">Whether a fixpoint group ran its whole pass ceiling without closing. It runs again only
/// once its trigger has read false and holds again.</param>
public readonly record struct RuleGroupProgress(int Step, bool Running, bool Breached);
/// <summary>Group progress beside the edge latch: one entry per compiled group name. Like the latch, this is
/// simulation state — it hashes, flattens and restores.</summary>
public sealed class RuleGroupState {
    private readonly Dictionary<string, RuleGroupProgress> m_byGroup = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets how many groups the state holds progress for.</summary>
    public int Count => m_byGroup.Count;

    /// <summary>Folds the progress into a state hash, in compiled order, so two hosts holding the same progress hash
    /// the same regardless of insertion order.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="compiled">The section's compiled groups.</param>
    public void AppendStateHash(ref Fnv1aHash hash, CompiledRuleGroup[] compiled) {
        ArgumentNullException.ThrowIfNull(argument: compiled);

        hash.Add(value: ((uint)compiled.Length));

        foreach (var group in compiled) {
            var progress = Progress(name: group.Name);

            hash.Add(value: Fnv1aHash.Compute(values: group.Name.AsSpan()));
            hash.Add(value: ((uint)progress.Step));
            hash.Add(value: ((byte)(progress.Running
                ? 1
                : 0)));
            hash.Add(value: ((byte)(progress.Breached
                ? 1
                : 0)));
        }
    }
    /// <summary>Forgets every group's progress.</summary>
    public void Clear() => m_byGroup.Clear();
    /// <summary>Appends every group's progress — the checkpoint spelling <see cref="Restore"/> reads back.</summary>
    /// <param name="into">The list to append to.</param>
    public void Flatten(List<(string, RuleGroupProgress)> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var (name, progress) in m_byGroup) {
            into.Add(item: (name, progress));
        }
    }
    /// <summary>Returns one group's progress, or the unstarted default.</summary>
    /// <param name="name">The group's name.</param>
    /// <returns>The progress.</returns>
    public RuleGroupProgress Progress(string name) => m_byGroup.GetValueOrDefault(key: name);
    /// <summary>Keeps every surviving group's progress and drops the names no longer compiled.</summary>
    /// <param name="compiled">The section's compiled groups after a recompile.</param>
    public void Prune(CompiledRuleGroup[] compiled) {
        ArgumentNullException.ThrowIfNull(argument: compiled);

        if (m_byGroup.Count == 0) {
            return;
        }

        var live = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var group in compiled) {
            _ = live.Add(item: group.Name);
        }

        foreach (var name in m_byGroup.Keys) {
            if (!live.Contains(item: name)) {
                _ = m_byGroup.Remove(key: name);
            }
        }
    }
    /// <summary>Restores one flattened entry.</summary>
    /// <param name="name">The group's name.</param>
    /// <param name="progress">The progress.</param>
    public void Restore(string name, RuleGroupProgress progress) => m_byGroup[name] = progress;
    /// <summary>Records one group's progress.</summary>
    /// <param name="name">The group's name.</param>
    /// <param name="progress">The progress.</param>
    public void Set(string name, RuleGroupProgress progress) => m_byGroup[name] = progress;
}
