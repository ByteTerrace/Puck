namespace Puck.World;

/// <summary>One reading of an authority net a rule takes (<see cref="WorldRuleFacts.NetPrefix"/>), as the net's latest
/// step left it.</summary>
public enum WorldNetReading : byte {
    /// <summary>The farthest any node stands from its rest position, in metres.</summary>
    Deflection = 0,
    /// <summary>Whether the net steps: <c>1</c> awake, <c>0</c> asleep.</summary>
    Awake,
    /// <summary>How many bodies held the net.</summary>
    Holders,
    /// <summary>How many body volumes leaned on the net.</summary>
    Pushers,
}
/// <summary>Spells and reads <see cref="WorldNetReading"/> names as a world rule writes them
/// (<c>$net:&lt;placementId&gt;:deflection</c>).</summary>
public static class WorldNetVocabulary {
    private static readonly string[] Names = ["deflection", "awake", "holders", "pushers"];

    /// <summary>Gets every reading's name, in the order of <see cref="WorldNetReading"/>.</summary>
    public static IReadOnlyList<string> All => Names;

    /// <summary>Resolves a reading by the name a rule spells it with, case-sensitively.</summary>
    /// <param name="name">The name.</param>
    /// <param name="reading">The reading, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when no reading has that name.</returns>
    public static bool TryResolve(string name, out WorldNetReading reading) {
        var index = Array.IndexOf(array: Names, value: name);

        reading = (WorldNetReading)Math.Max(val1: 0, val2: index);
        return (index >= 0);
    }
    /// <summary>Returns the kind of value a reading reads as: a deflection is <see cref="CellKind.Fixed"/>, the rest
    /// <see cref="CellKind.Int"/>.</summary>
    /// <param name="reading">The reading.</param>
    /// <returns>The cell kind.</returns>
    public static CellKind KindOf(WorldNetReading reading) => ((reading == WorldNetReading.Deflection) ? CellKind.Fixed : CellKind.Int);
}
/// <summary>A reading of the net a placement carries (<see cref="WorldRuleFacts.NetPrefix"/>).</summary>
public sealed class WorldNetOperand : WorldFactOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="placementId">The placement carrying the net.</param>
    /// <param name="reading">The reading.</param>
    public WorldNetOperand(string placementId, WorldNetReading reading) : base(reach: WorldDisclosureReach.Bodies, valueKind: WorldNetVocabulary.KindOf(reading: reading)) {
        PlacementId = placementId;
        Reading = reading;
    }

    /// <summary>Gets the placement carrying the net.</summary>
    public string PlacementId { get; }
    /// <summary>Gets the reading.</summary>
    public WorldNetReading Reading { get; }

    /// <inheritdoc/>
    public override void CollectDisclosure(WorldDisclosureNeeds into) {
        base.CollectDisclosure(into: into);
        into.AddPlacement(placementId: PlacementId);
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return facet.Read(operand: this);
    }
}
