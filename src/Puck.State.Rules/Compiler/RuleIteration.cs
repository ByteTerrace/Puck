namespace Puck.State.Rules;

/// <summary>A document-owned bounded source for the ordinary rule sweep and its existing transactional evaluator.</summary>
/// <param name="Capacity">The keys the work sheet multiplies the rule by: the most one sweep of a bounded source visits,
/// or the number a source that binds whatever it finds is priced at.</param>
public abstract record RuleIteration(int Capacity) {
    /// <summary>Gets how many index keys a sweep can bind: the keys below this are interned when the rule compiles. A
    /// source that visits a few of a larger set of positions each sweep spans more keys than it visits.</summary>
    public virtual int KeySpan => Capacity;

    /// <summary>Snapshots the current ordered keys into reusable evaluator storage.</summary>
    public abstract void Collect(IEffectHost host, List<CellKey> into);
    /// <summary>Gets a value indicating whether a host that serves the source names every change to its key set
    /// (<see cref="TrySchedule"/> answers <see langword="true"/> there), so the work sheet prices the sweep by what
    /// changes rather than as swept every tick.</summary>
    public virtual bool Versioned => false;

    /// <summary>Distinguishes successive lifetimes which reuse a bounded key.</summary>
    public abstract long Generation(IEffectHost host, CellKey key);
    /// <summary>Names the rows and host facts whose versions move whenever the set of keys a sweep binds changes, so
    /// a due list can leave the rule unvisited while they stand.</summary>
    /// <param name="reader">The host the sweep runs over.</param>
    /// <param name="rows">The rows the key set depends on, appended to.</param>
    /// <param name="facts">The host facts the key set depends on, appended to.</param>
    /// <returns><see langword="true"/> when the appended dependencies name every change to the key set;
    /// <see langword="false"/>, the default, for a source no version names, which is swept every tick.</returns>
    public virtual bool TrySchedule(IStateReader reader, List<int> rows, List<FactRead> facts) => false;
}
