using Puck.State.Rules;

namespace Puck.World;

/// <summary>The authority's bounded semantic event sweep.</summary>
public interface IWorldInputEvents : IFacet {
    /// <summary>Collects current-epoch events admitted in this step, oldest first.</summary>
    void CollectInputEvents(WorldInputOwnerReference owner, List<CellKey> into);
    /// <summary>Returns the event identity occupying one retained-history position.</summary>
    long InputEventGeneration(WorldInputOwnerReference owner, CellKey key);
}

/// <summary>Compiled inputEvents(owner) iteration over the existing rule sweep.</summary>
public sealed record WorldInputIteration(WorldInputOwnerReference Owner, int EventCapacity) : RuleIteration(EventCapacity) {
    /// <inheritdoc/>
    public override void Collect(IEffectHost host, List<CellKey> into) => ((IWorldInputEvents)host).CollectInputEvents(Owner, into);
    /// <inheritdoc/>
    public override long Generation(IEffectHost host, CellKey key) => ((IWorldInputEvents)host).InputEventGeneration(Owner, key);
}
