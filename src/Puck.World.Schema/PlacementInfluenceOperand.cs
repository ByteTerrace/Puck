namespace Puck.World;

/// <summary>A placement's coverage by an author-defined spatial influence channel.</summary>
public sealed class PlacementInfluenceOperand : WorldOperandFact {
    /// <summary>Initializes a fixed placement address, or a keyed child of a deal template.</summary>
    /// <param name="channel">The opaque influence label.</param>
    /// <param name="placementId">The target placement or deal template.</param>
    /// <param name="key">An optional literal child key.</param>
    public PlacementInfluenceOperand(string channel, string placementId, string? key = null)
        : base(CellKind.Int) {
        Channel = channel;
        PlacementId = placementId;
        Key = key;
    }

    /// <summary>Gets the shared author label.</summary>
    public string Channel { get; }
    /// <summary>Gets the literal child key.</summary>
    public string? Key { get; }
    /// <summary>Gets the fixed target or child template.</summary>
    public string PlacementId { get; }
}
