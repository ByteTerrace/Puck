using Puck.State.Rules;

namespace Puck.World;

/// <summary>The authority's bounded semantic event sweep.</summary>
public interface IWorldInputEvents : IFacet {
    /// <summary>Collects current-epoch events admitted in this step, oldest first.</summary>
    void CollectInputEvents(WorldInputOwnerReference owner, List<CellKey> into);
    /// <summary>Returns the event identity occupying one retained-history position.</summary>
    long InputEventGeneration(WorldInputOwnerReference owner, CellKey key);
}
/// <summary>Compiled inputEvents(owner) iteration over the existing rule sweep. A sweep binds the events admitted in
/// this step, keyed by their position in the retained history.</summary>
/// <param name="Owner">The input owner whose events the sweep binds.</param>
/// <param name="StepCapacity">The most current-epoch events one step can admit: the step's ingress bound plus the
/// terminal cancellations one switch writes. Admission refuses a batch past the bound, and a switch's tick admits no
/// fresh input, so no sweep visits more.</param>
/// <param name="HistoryCapacity">The retained history's positions, every one a key a step's event can occupy.</param>
public sealed record WorldInputIteration(WorldInputOwnerReference Owner, int StepCapacity, int HistoryCapacity) : RuleIteration(StepCapacity) {
    /// <inheritdoc/>
    public override int KeySpan => HistoryCapacity;

    /// <summary>Returns the sweep over one owner's events under a document's input policy.</summary>
    /// <param name="owner">The input owner.</param>
    /// <param name="definition">The document whose input history policy and catalog bound the sweep.</param>
    /// <returns>The iteration.</returns>
    public static WorldInputIteration For(WorldInputOwnerReference owner, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var policy = (definition.InputHistory ?? new WorldInputHistory());
        var catalog = WorldInputCatalog.For(definition: definition);

        return new WorldInputIteration(
            HistoryCapacity: catalog.HistoryCapacity(ingressCapacity: policy.EventCapacity),
            Owner: owner,
            StepCapacity: catalog.HistoryCapacity(ingressCapacity: policy.MaximumEventsPerStep)
        );
    }

    /// <inheritdoc/>
    public override void Collect(IEffectHost host, List<CellKey> into) => ((IWorldInputEvents)host).CollectInputEvents(Owner, into);
    /// <inheritdoc/>
    public override long Generation(IEffectHost host, CellKey key) => ((IWorldInputEvents)host).InputEventGeneration(Owner, key);
}
