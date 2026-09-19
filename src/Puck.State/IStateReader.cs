namespace Puck.State;

/// <summary>What every host answers, whatever else it serves: the arena a read finds its value in, the catalog the
/// compiled addresses were minted against, the tick pair, the bound-key context of the evaluation in flight, and
/// the scratch a pattern word is read into.</summary>
/// <remarks>
/// <para>Every member is read on the tick path and allocates nothing: the scratch span is a reused buffer and the
/// bound keys are interned <see cref="CellKey"/> values.</para>
/// <para>There is no accessor that hands out a facet by type. A host widens this interface by implementing the
/// facets it serves (<see cref="IFacet"/>); the only question asked here is
/// <see cref="Advertises{TFacet}"/>, and the facet instance itself reaches a fact as the typed argument its base
/// passes it.</para>
/// </remarks>
public interface IStateReader {
    /// <summary>Gets the columnar store every state read and write addresses.</summary>
    StateArena Arena { get; }
    /// <summary>Gets the raw values the enclosing rule's locals computed for the evaluation in flight, each in its
    /// own local's kind. The evaluator writes the slots it computes before the gate reads them, so the span is the
    /// host's own scratch and is at least <see cref="RuleCapacity.MaxLocalsPerRule"/> wide.</summary>
    Span<long> Locals { get; }
    /// <summary>Gets or sets the cell key bound to <see cref="BoundKey.Each"/>, set by the evaluator for the
    /// duration of one iteration and the default invalid key outside a <see cref="Rule.ForEach"/> evaluation.</summary>
    CellKey BoundEachKey { get; set; }
    /// <summary>Gets or sets the cell key bound to <see cref="BoundKey.Previous"/> — the token before
    /// <see cref="BoundTokenKey"/> in the word being read, the default invalid key on the first token and outside a
    /// pattern read.</summary>
    CellKey BoundPreviousKey { get; set; }
    /// <summary>Gets or sets the cell key bound to <see cref="BoundKey.Token"/>, set by a pattern's tuple-word read
    /// for the duration of one token's value expression and the default invalid key otherwise.</summary>
    CellKey BoundTokenKey { get; set; }
    /// <summary>Gets the catalog every compiled address was minted against.</summary>
    StateCatalog Catalog => Arena.Catalog;
    /// <summary>Gets the engine tick (<see cref="Puck.Maths.FixedTickConversion.TicksPerSecond"/> per second) the
    /// evaluation in flight answers as of — what a <see cref="StateAdvance"/> read is computed at. Never a
    /// simulation tick converted at the world's current rate.</summary>
    ulong EngineTick { get; }
    /// <summary>Gets scratch for one pattern word, sized to the longest word any source in the section can produce.</summary>
    Span<long> PatternWord { get; }
    /// <summary>Gets the simulation tick the evaluation in flight answers as of — what a <see cref="StateCycle"/> or
    /// <see cref="StateDynamics"/> read is computed at.</summary>
    ulong Tick { get; }
    /// <summary>Gets the hypothetical search ply; zero on a live host.</summary>
    int SearchPly => 0;
    /// <summary>Gets the clocks and dynamics rows a live cell read is evaluated against. The default carries the
    /// tick pair alone, which is what a section declaring no dynamics row needs; a host whose document declares
    /// dynamics rows answers with them and with the simulation rate.</summary>
    ArenaTime Time => ArenaTime.At(
        engineTick: EngineTick,
        tick: Tick
    );

    /// <summary>Returns whether this host serves a facet, so a rule naming it can be admitted.</summary>
    /// <typeparam name="TFacet">The facet.</typeparam>
    /// <returns><see langword="true"/> when the host implements the facet.</returns>
    bool Advertises<TFacet>() where TFacet : IFacet => (this is TFacet);
    /// <summary>Returns the value the enclosing rule's local at <paramref name="ordinal"/> computed for the
    /// evaluation in flight, in that local's own kind.</summary>
    /// <param name="ordinal">The local's slot in the rule.</param>
    /// <returns>The local's raw value.</returns>
    long LocalValue(int ordinal) => Locals[ordinal];
    /// <summary>Gets the working storage a read borrows: a board read's cell values, an expression's value
    /// stack.</summary>
    ArenaScratch Scratch => Arena.Scratch;
    /// <summary>Returns the participant index a binding names for the evaluation in flight, or -1 when it is not in
    /// play.</summary>
    /// <param name="key">The binding.</param>
    /// <returns>The index, or -1.</returns>
    int BoundIndex(BoundKey key);
    /// <summary>Returns a row's version — the counter a commit moves only when it left the row's bytes different —
    /// so a scheduler can prove a row's content unchanged between two evaluations without rereading it.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="version">The version, when the ordinal names a row.</param>
    /// <returns><see langword="true"/> when the ordinal names a row.</returns>
    bool TryRowVersion(int rowOrdinal, out ulong version) {
        var arena = Arena;

        if (((uint)rowOrdinal) >= ((uint)arena.Layout.RowCount)) {
            version = 0UL;

            return false;
        }

        version = arena.RowVersion(rowOrdinal: rowOrdinal);

        return true;
    }
}
