namespace Puck.State.Rules;

/// <summary>A document-owned bounded source for the ordinary rule sweep and its existing transactional evaluator.</summary>
/// <param name="Capacity">The most keys one sweep visits, which the work sheet multiplies the rule by.</param>
public abstract record RuleIteration(int Capacity) {
    /// <summary>Gets how many index keys a sweep can bind: the keys below this are interned when the rule compiles. A
    /// source that visits a few of a larger set of positions each sweep spans more keys than it visits.</summary>
    public virtual int KeySpan => Capacity;

    /// <summary>Snapshots the current ordered keys into reusable evaluator storage.</summary>
    public abstract void Collect(IEffectHost host, List<CellKey> into);
    /// <summary>Distinguishes successive lifetimes which reuse a bounded key.</summary>
    public abstract long Generation(IEffectHost host, CellKey key);
}
