namespace Puck.World;

/// <summary>The world's host facet: one read per world operand fact, the two resolutions the world's body
/// references need, and the reads of the rows the world's own storage serves instead of the arena. The evaluator
/// that owns bodies, regions, machines, and channels implements it, and a fact naming
/// <see cref="IWorldFacts"/> as its facet type receives the instance as an argument.</summary>
/// <remarks>Every member is read on the tick path and allocates nothing.</remarks>
public interface IWorldFacts : IFacet {
    /// <summary>Returns the active body count.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(PopulationOperand operand);
    /// <summary>Returns whether every rigid body is at rest.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(PhysicsQuiescentOperand operand);
    /// <summary>Returns the music clock's phase error.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(ClockOperand operand);
    /// <summary>Returns a region placement's occupant count.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(RegionOccupancyOperand operand);
    /// <summary>Returns a placement's coverage by a spatial influence channel.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(PlacementInfluenceOperand operand);
    /// <summary>Returns one byte of a screen's machine memory.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(MachineMemoryOperand operand);
    /// <summary>Returns the body whose keyed-row cell is the extreme.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(ArgBodyOperand operand);
    /// <summary>Returns the distance between two bodies.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(BodyDistanceOperand operand);
    /// <summary>Returns whether two bodies see each other.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(LineOfSightOperand operand);
    /// <summary>Returns the ticks a parked body has left, or forever.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(ParkedOperand operand);
    /// <summary>Returns a body's uprightness.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(UprightOperand operand);
    /// <summary>Returns whether a body's fact bit holds.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(BodyFactOperand operand);
    /// <summary>Returns an adjacency link's staleness, in ticks.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(LinkStalenessOperand operand);
    /// <summary>Returns a seat's live composition-channel value.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(ChannelOperand operand);
    /// <summary>Returns a seat's pointer ray mapped through a screen.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(PointerOperand operand);
    /// <summary>Returns the nearest body carrying a tag-row cell.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(NearestOperand operand);
    /// <summary>Returns one facet of a body's navigation state.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(NavigationOperand operand);
    /// <summary>Returns the board cell under a body, or -1 off the board.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(BoardCellOfOperand operand);
    /// <summary>Returns the body index a compiled reference names for the evaluation in flight, or -1.</summary>
    /// <param name="bodyRef">The reference.</param>
    /// <returns>The index, or -1.</returns>
    int ResolveBody(in CompiledBodyRef bodyRef);
    /// <summary>Returns an already-admitted cell key a <c>$pair:</c> indirection names for the evaluation in flight.
    /// A writing effect admits a missing pair key inside its journal scope.</summary>
    /// <param name="key">The pair key.</param>
    /// <returns>The interned key.</returns>
    CellKey PairKey(PairKeyFact key);
    /// <summary>Returns one cell of a host-owned lattice row, whose values the world's own storage holds instead of
    /// the arena.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="cell">The cell's index in the row's topology.</param>
    /// <param name="value">The value, in the row's cell kind.</param>
    /// <returns><see langword="true"/> when the row is one this host serves and the index names a cell.</returns>
    bool TryReadHostOwnedCell(int rowOrdinal, int cell, out long value);
    /// <summary>Returns the one value a host-owned slot row holds.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the row is one this host serves.</returns>
    bool TryReadHostOwnedSlot(int rowOrdinal, out CellValue value);
}
