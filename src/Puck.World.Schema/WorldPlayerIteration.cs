using Puck.State.Rules;

namespace Puck.World;

/// <summary>The authority's view of which bodies players occupy: what a rule sweeping <c>forEach: $players</c> binds.</summary>
public interface IWorldPlayers : IFacet {
    /// <summary>Appends the index key of every body a player occupies at this instant (a local seat's active body, or
    /// an admitted remote human's, parked through its grace included), in ascending body index order.</summary>
    /// <param name="into">The list the keys are appended to.</param>
    void CollectPlayers(List<CellKey> into);
}
/// <summary>A compiled <c>forEach: $players</c> sweep: one binding of <c>$each</c> per body a player occupies, keyed by
/// the body's index, in ascending index order. A rule about what any player does (a press, a toggle) is one rule over
/// the players, never one copy per seat or visitor slot: it is priced at the players the document admits
/// (<c>bodies.localSeats</c> plus <c>bodies.networkPlayers</c>) and binds only the bodies players hold now, wherever
/// admission placed them.</summary>
/// <param name="Players">The players the work sheet prices one sweep at: the document's local seats plus its network
/// players, every player the population can admit at once.</param>
/// <param name="BodyCapacity">The document's body capacity: every body index a sweep can bind, since a remote human
/// takes whichever slot admission frees for it.</param>
public sealed record WorldPlayerIteration(int Players, int BodyCapacity) : RuleIteration(Players) {
    /// <summary>The reserved sweep source's spelling.</summary>
    public const string Spelling = "$players";
    /// <summary>The reserved channel the spelling parses to: <c>players</c>, called with no argument.</summary>
    public const string Channel = "players";

    /// <summary>Returns the sweep over a document's players.</summary>
    /// <param name="definition">The document whose population bounds the sweep.</param>
    /// <returns>The iteration.</returns>
    public static WorldPlayerIteration For(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var population = definition.Population;

        return new WorldPlayerIteration(BodyCapacity: population.Capacity, Players: Math.Min(val1: population.Capacity, val2: (population.LocalSeats + population.NetworkPlayers)));
    }

    /// <inheritdoc/>
    public override int KeySpan => BodyCapacity;
    /// <inheritdoc/>
    public override bool Versioned => true;
    /// <inheritdoc/>
    public override bool BindsBodies => true;

    /// <inheritdoc/>
    public override void Collect(IEffectHost host, List<CellKey> into) => ((IWorldPlayers)host).CollectPlayers(into: into);
    /// <inheritdoc/>
    /// <remarks>A binding is a body's index, which names one player's body for as long as a player holds it: a body
    /// that stops being a player's is not swept, so its binding closes with the sweep.</remarks>
    public override long Generation(IEffectHost host, CellKey key) => 0L;
    /// <inheritdoc/>
    /// <remarks>Which bodies players hold changes only when a slot's liveness does: a local seat's body is a player's
    /// exactly while it is active, and a remote human's mark is set when its slot activates and cleared when it
    /// retires (<c>Server.WorldPopulation</c>'s admission and retirement writers), so the live-bodies version names
    /// every change to the key set.</remarks>
    public override bool TrySchedule(IStateReader reader, List<int> rows, List<FactRead> facts) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return reader.TryScheduleLiveBodies(facts: facts);
    }
}
