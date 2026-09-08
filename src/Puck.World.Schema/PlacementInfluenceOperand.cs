namespace Puck.World;

/// <summary>A placement's coverage by an author-defined spatial influence channel.</summary>
public sealed class PlacementInfluenceOperand : WorldOperandFact {
    /// <summary>Compiles a fixed placement address, or a keyed child of a deal template.</summary>
    /// <param name="channel">The opaque influence label.</param>
    /// <param name="placementId">The target placement or deal template.</param>
    /// <param name="key">An optional literal child key.</param>
    /// <param name="keyFrom">An optional dynamic child key.</param>
    public PlacementInfluenceOperand(string channel, string placementId, string? key = null, CompiledCellRef? keyFrom = null)
        : base(CellKind.Int) {
        Channel = channel;
        PlacementId = placementId;
        Key = key;
        KeyFrom = keyFrom;
    }

    /// <summary>Gets the shared author label.</summary>
    public string Channel { get; }
    /// <summary>Gets the fixed target or child template.</summary>
    public string PlacementId { get; }
    /// <summary>Gets the literal child key.</summary>
    public string? Key { get; }
    /// <summary>Gets the dynamic child key.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(this);
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => Math.Max(1,
        ((WorldRuleCompileContext)context).Definition.Placements.Sum(placement => (long)(placement.Spatial?.Count ?? 0)));
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => RuleAccess.CollectReference(KeyFrom, into);
}
