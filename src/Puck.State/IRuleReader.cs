namespace Puck.State;

/// <summary>What a compiled operand reads its live fact through: the evaluation in flight (its tick, its bound keys
/// and values) and the section being read. A document project's evaluator implements this once and hands it to every
/// <see cref="OperandFact.Read"/>; an operand a document project registers casts the reader to the project's own
/// widening of this interface for the facts only that project can answer.</summary>
/// <remarks>Every member is read on the tick path. An implementation allocates nothing per call: the scratch spans
/// are reused buffers, the bound keys are cached strings, and the table lookups index a compiled array.</remarks>
public interface IRuleReader {
    /// <summary>Gets the tick the evaluation in flight answers as of.</summary>
    ulong Tick { get; }
    /// <summary>Gets where every state read finds a cell's stored value as of this tick: the section's own rows, or a
    /// frame over them.</summary>
    StateStore Store { get; }
    /// <summary>Gets the catalog every compiled handle was minted against.</summary>
    StateCatalog Catalog { get; }
    /// <summary>Gets the compiled patterns every <see cref="RuleFacts.MatchPrefix"/> operand names.</summary>
    CompiledPatterns Patterns { get; }
    /// <summary>Gets the cell key bound to <see cref="BoundKey.Each"/> for the evaluation in flight, or
    /// <see langword="null"/> outside a <see cref="Rule.ForEach"/> evaluation.</summary>
    string? BoundEachKey { get; }
    /// <summary>Gets or sets the cell key bound to <see cref="BoundKey.Token"/> — set by a pattern's tuple-word read
    /// for the duration of one token's value expression, <see langword="null"/> otherwise.</summary>
    string? BoundTokenKey { get; set; }
    /// <summary>Gets or sets the cell key bound to <see cref="BoundKey.Previous"/> — the token before
    /// <see cref="BoundTokenKey"/> in the word being read, <see langword="null"/> on the first token and outside a
    /// pattern read.</summary>
    string? BoundPreviousKey { get; set; }
    /// <summary>Gets or sets a value indicating whether a table read since the last clear named a key its table does
    /// not carry; the enclosing gate or expression evaluation clears it and fails, so a missing entry is a reported
    /// refusal rather than a value.</summary>
    bool TableKeyMissing { get; set; }
    /// <summary>Returns the participant index a binding names for the evaluation in flight, or -1 when it is not in
    /// play.</summary>
    /// <param name="key">The binding.</param>
    int BoundIndex(BoundKey key);
    /// <summary>Returns the value the enclosing rule's binding of that ordinal computed for this evaluation.</summary>
    /// <param name="ordinal">The binding's slot (<see cref="CompiledRuleBinding"/>'s position).</param>
    long BindingValue(int ordinal);
    /// <summary>Returns a compiled table by its ordinal in the section's <c>tables</c> rows.</summary>
    /// <param name="ordinal">The table's ordinal.</param>
    CompiledTable Table(int ordinal);
    /// <summary>Reports a dynamic table key the table does not carry and sets <see cref="TableKeyMissing"/>.</summary>
    /// <param name="table">The table's authored name.</param>
    /// <param name="key">The missing key.</param>
    void ReportTableKeyMissing(string table, long key);
    /// <summary>Returns scratch for one board's cell values, at least <paramref name="cells"/> long. A board read
    /// never nests another, so one buffer serves every reader.</summary>
    /// <param name="cells">The cell count to hold.</param>
    Span<long> BoardScratch(int cells);
    /// <summary>Gets scratch for one pattern word, sized to the longest word any source in the section can produce.</summary>
    Span<long> PatternWord { get; }
}
