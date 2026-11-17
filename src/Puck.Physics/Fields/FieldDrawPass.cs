namespace Puck.Physics.Fields;

/// <summary>Fills a lattice's whole-field draw buffer: the host's draw source writes one raw
/// <see cref="Puck.Maths.FixedQ4816"/> value per cell, in cell-index order, into the span a
/// <see cref="FieldLattice"/> hands it.</summary>
/// <param name="raw">The lattice's cleared draw buffer, exactly <see cref="FieldLattice.CellCount"/> long; valid for the call only.</param>
/// <returns><see langword="true"/> when every cell was written; <see langword="false"/> when the source refused the pass.</returns>
public delegate bool FieldDrawPass(Span<long> raw);
