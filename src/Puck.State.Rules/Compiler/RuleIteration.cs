namespace Puck.State.Rules;

/// <summary>A document-owned bounded source for the ordinary rule sweep and its existing transactional evaluator.</summary>
/// <param name="Capacity">Maximum number of keys a sweep visits.</param>
public abstract record RuleIteration(int Capacity) {
    /// <summary>Snapshots the current ordered keys into reusable evaluator storage.</summary>
    public abstract void Collect(IEffectHost host, List<CellKey> into);
    /// <summary>Distinguishes successive lifetimes which reuse a bounded key.</summary>
    public abstract long Generation(IEffectHost host, CellKey key);
}
