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
    /// <summary>Gets the lexical pool-instance registers for the evaluation in flight. A compiled qualified field
    /// names one register slot; the evaluator sets and restores that slot around a claim or pool sweep. Readers
    /// that cannot evaluate pool rules expose the empty default span, so ordinary rules carry no extra state.</summary>
    Span<StateInstanceHandle> InstanceBindings => [];
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
    /// <summary>Gets a value indicating whether <see cref="SearchPly"/> never moves on this host, so a schedule may
    /// treat a read of it as a constant. A host that judges hypothetical positions answers <see langword="false"/>,
    /// the default.</summary>
    bool SearchPlyIsFixed => false;
    /// <summary>Gets a count the host moves whenever what <see cref="TryScheduleFact"/> answers for a fact could
    /// change (a register file rebound to other rows), so a schedule derived under another count is derived again.</summary>
    int FactScheduleEpoch => 0;

    /// <summary>Names what one host fact depends on, when the host versions it: the arena rows the read resolves to,
    /// and the host's own versioned sources it reads. A fact the host cannot name keeps every schedule that reads
    /// it volatile, which is the default.</summary>
    /// <param name="fact">The compiled fact a rule's judgement reads.</param>
    /// <param name="rows">The arena row ordinals the read depends on, appended.</param>
    /// <param name="facts">The host sources the read depends on, appended.</param>
    /// <returns><see langword="true"/> when the host names every dependency the read can have.</returns>
    bool TryScheduleFact(ICompiledFact fact, List<int> rows, List<FactRead> facts) => false;
    /// <summary>Names the versioned source a sweep over the live bodies depends on: the roster, whose version moves
    /// when a body joins or leaves.</summary>
    /// <param name="facts">The host sources the roster depends on, appended.</param>
    /// <returns><see langword="true"/> when the host versions its roster.</returns>
    bool TryScheduleLiveBodies(List<FactRead> facts) => false;
    /// <summary>Returns one host source's version for one body (or for the source as a whole): a counter that moves
    /// whenever what the source answers for that body changes, read after <see cref="RefreshFacts"/>.</summary>
    /// <param name="source">The host's source number.</param>
    /// <param name="body">The body index, or <see cref="FactRead.Whole"/>.</param>
    /// <param name="version">The version, when the host answers.</param>
    /// <returns><see langword="true"/> when the host versions the source for that body.</returns>
    bool TryFactVersion(int source, int body, out ulong version) {
        version = 0UL;

        return false;
    }
    /// <summary>Returns a host source's name for a read-back.</summary>
    /// <param name="source">The host's source number.</param>
    /// <returns>The name.</returns>
    string DescribeFactSource(int source) => $"source {source}";
    /// <summary>Brings every host source's version up to what the host's state answers now, recording each source and
    /// body that moved for <see cref="DrainFactMoves"/>. The evaluator calls it before a family's walk and after a
    /// firing that delivered an arm, the only work during rule evaluation that changes host state.</summary>
    void RefreshFacts() { }
    /// <summary>Hands every host source and body whose version moved since the last drain to the caller and forgets
    /// them.</summary>
    /// <param name="into">The moved sources, appended, each as the body it moved for or <see cref="FactRead.Whole"/>.</param>
    /// <returns><see langword="true"/> when every source must be taken as moved (the host's sources were rebuilt).</returns>
    bool DrainFactMoves(List<FactRead> into) => false;
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
