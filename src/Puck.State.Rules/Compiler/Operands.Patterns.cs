namespace Puck.State.Rules;

/// <summary>A pattern-language match over a row's word (<see cref="RuleFacts.MatchPrefix"/>). The word is read at
/// this tick off the arena, so an advancing attribute cell reads its live value. A board origin that names no cell
/// reads the empty word, which the pattern decides like any other.</summary>
public sealed class PatternOperand : RuleOperand, IStateAddressedOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The source row's catalog ordinal, or <c>-1</c> when <paramref name="rowFrom"/>
    /// applies.</param>
    /// <param name="key">The literal board-origin cell key, or the token a zone or keyed word starts at.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="pattern">The compiled pattern.</param>
    /// <param name="board">The board ray descriptor, for a board source; <see langword="null"/> otherwise.</param>
    /// <param name="attributeOrdinal">The zone's attribute row's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="matchFacet">What this operand answers about its word.</param>
    /// <param name="tokenExpression">The zone's per-token value expression, when the pattern carries one.</param>
    /// <param name="capacity">The source row's cell capacity, for pricing the walk.</param>
    /// <param name="rowFrom">The live row read, or <see langword="null"/> for a fixed row.</param>
    /// <param name="occurrence">The zero-based occurrence selected by an at/length facet.</param>
    public PatternOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, CompiledPattern pattern, BoardNeighbourQuery? board, int attributeOrdinal, MatchFacet matchFacet, CompiledExpressionToken[]? tokenExpression, long capacity, LiveRow? rowFrom = null, int occurrence = 0) : base(valueKind: CellKind.Int) {
        ArgumentNullException.ThrowIfNull(argument: pattern);

        AttributeOrdinal = attributeOrdinal;
        Board = board;
        Capacity = capacity;
        Key = key;
        KeyFrom = keyFrom;
        MatchFacet = matchFacet;
        Occurrence = occurrence;
        Pattern = pattern;
        RowFrom = rowFrom;
        RowOrdinal = rowOrdinal;
        TokenExpression = tokenExpression;
    }

    /// <summary>Returns the position a word starts reading at: the position of <paramref name="start"/> among
    /// <paramref name="cells"/>, 0 when no start token is named, or the cell count when the named token is absent,
    /// so a word anchored at a token the row no longer carries reads empty rather than from the first cell.</summary>
    /// <param name="cells">The source row's cells, in their own order.</param>
    /// <param name="start">The token the word starts at, or <see langword="null"/> for the first cell.</param>
    /// <returns>The starting position.</returns>
    public static int StartIndex(IReadOnlyList<StateCell> cells, string? start) {
        ArgumentNullException.ThrowIfNull(argument: cells);

        if (start is null) {
            return 0;
        }
        for (var index = 0; (index < cells.Count); index++) {
            if (string.Equals(
                a: cells[index].Key.Value,
                b: start,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }

        return cells.Count;
    }

    /// <summary>Gets the zone's attribute row's catalog ordinal, or <c>-1</c>.</summary>
    public int AttributeOrdinal { get; }
    /// <summary>Gets the board ray descriptor, for a board source; <see langword="null"/> otherwise. Only its
    /// direction is read (<c>-1</c> meaning every direction).</summary>
    public BoardNeighbourQuery? Board { get; }
    /// <summary>Gets the source row's cell capacity, for pricing the walk.</summary>
    public long Capacity { get; }
    /// <summary>Gets the literal board-origin or start-token cell key, or the invalid default.</summary>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets what this operand answers about its word.</summary>
    public MatchFacet MatchFacet { get; }
    /// <summary>Gets the zero-based occurrence selected by <see cref="MatchFacet.At"/> or
    /// <see cref="MatchFacet.Length"/>.</summary>
    public int Occurrence { get; }
    /// <summary>Gets the compiled pattern.</summary>
    public CompiledPattern Pattern { get; }
    /// <summary>Gets the live row read, or <see langword="null"/> for a fixed row.</summary>
    public LiveRow? RowFrom { get; }
    /// <inheritdoc/>
    public int RowOrdinal { get; }
    /// <summary>Gets the zone's per-token value expression, when the pattern carries one.</summary>
    public CompiledExpressionToken[]? TokenExpression { get; }

    private long ReadBoardMatch(IStateReader reader, int rowOrdinal, BoardNeighbourQuery query) {
        var arena = reader.Arena;
        using var valuesLease = reader.Scratch.Rent<long>(length: query.Topology.CellCount);

        var values = valuesLease.Span;
        var word = reader.PatternWord;
        var key = RuleReads.ResolveKey(
            keyFrom: KeyFrom,
            literal: Key,
            reader: reader
        );
        var origin = -1;

        if (
            key.IsValid &&
            reader.Arena.Keys.TryGetName(
            key: key,
            name: out var name
        )
        ) {
            _ = query.Topology.TryCell(
                cell: out origin,
                key: name.Value
            );
        }

        if (query.Direction >= 0) {
            var length = (ArenaBoards.TryReadRay(
                arena: arena,
                direction: query.Direction,
                origin: origin,
                ray: out var ray,
                reason: out _,
                rowOrdinal: rowOrdinal,
                values: values,
                word: word
            )
                ? ray.Length
                : 0
            );

            if (MatchFacet is MatchFacet.Cell or MatchFacet.Distance) {
                var prefixLength = Pattern.LongestAcceptedPrefix(values: word[..length]);

                if (prefixLength == length) {
                    return -1L;
                }
                if (MatchFacet == MatchFacet.Distance) {
                    return (prefixLength + 1L);
                }

                var blocker = origin;

                for (var step = 0L; (step <= prefixLength); step++) {
                    blocker = query.Topology.Neighbour(
                        cell: blocker,
                        direction: query.Direction
                    );
                }

                return blocker;
            }
            if (MatchFacet is MatchFacet.At or MatchFacet.Length) {
                if (!Pattern.TryFindOccurrence(
                    length: out var matchLength,
                    occurrence: Occurrence,
                    start: out var matchStart,
                    values: word[..length]
                )) {
                    return ((MatchFacet == MatchFacet.At) ? -1L : 0L);
                }
                if (MatchFacet == MatchFacet.Length) {
                    return matchLength;
                }

                var matchCell = origin;

                for (var step = 0; (step <= matchStart); step++) {
                    matchCell = query.Topology.Neighbour(
                        cell: matchCell,
                        direction: query.Direction
                    );
                }

                return matchCell;
            }

            return ((MatchFacet == MatchFacet.Prefix)
                ? Pattern.LongestAcceptedPrefix(values: word[..length])
                : Pattern.Match(values: word[..length])
            );
        }

        var mask = 0L;
        var count = 0L;

        for (var direction = 0; (direction < query.Topology.DirectionCount); direction++) {
            var length = (ArenaBoards.TryReadRay(
                arena: arena,
                direction: direction,
                origin: origin,
                ray: out var ray,
                reason: out _,
                rowOrdinal: rowOrdinal,
                values: values,
                word: word
            )
                ? ray.Length
                : 0
            );

            if (Pattern.Match(values: word[..length]) == 1L) {
                count++;
                mask |= (1L << direction);
            }
        }

        return ((MatchFacet == MatchFacet.DirectionCount)
            ? count
            : mask
        );
    }
    private long ReadWordMatch(IStateReader reader, int rowOrdinal) {
        var arena = reader.Arena;
        var word = reader.PatternWord;
        var start = 0;

        if (
            (Key.IsValid || (KeyFrom is not null))
        ) {
            var startKey = RuleReads.ResolveKey(
                keyFrom: KeyFrom,
                literal: Key,
                reader: reader
            );

            start = StartPosition(
                arena: arena,
                key: startKey,
                rowOrdinal: rowOrdinal
            );
        }

        var length = ReadWord(
            attributeOrdinal: AttributeOrdinal,
            kind: Pattern.Source.Kind,
            reader: reader,
            rowOrdinal: rowOrdinal,
            start: start,
            tokenExpression: TokenExpression,
            word: word
        );

        if (MatchFacet is MatchFacet.At or MatchFacet.Length) {
            return (Pattern.TryFindOccurrence(
                length: out var matchLength,
                occurrence: Occurrence,
                start: out var matchStart,
                values: word[..length]
            )
                ? ((MatchFacet == MatchFacet.At) ? matchStart : matchLength)
                : ((MatchFacet == MatchFacet.At) ? -1L : 0L)
            );
        }

        return ((MatchFacet == MatchFacet.Prefix)
            ? Pattern.LongestAcceptedPrefix(values: word[..length])
            : Pattern.Match(values: word[..length])
        );
    }

    /// <summary>Reads the word a zone, keyed, pool, or history source spells at the reader's
    /// <see cref="IStateReader.Time"/>: each token through <paramref name="tokenExpression"/> when the pattern
    /// carries one (<see cref="ReadTupleWord"/>), else each member's live value in
    /// <paramref name="attributeOrdinal"/>, else the row's own live values
    /// (<see cref="StateArena.ReadWord(int, int, in ArenaTime, Span{long}, int)"/>).</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The source row's catalog ordinal.</param>
    /// <param name="attributeOrdinal">The attribute row's catalog ordinal, or <c>-1</c> for the row's own
    /// values.</param>
    /// <param name="tokenExpression">The per-token value expression, or <see langword="null"/>.</param>
    /// <param name="kind">The pattern's kind, which a value expression evaluates in.</param>
    /// <param name="word">The word buffer.</param>
    /// <param name="start">The position the word starts at.</param>
    /// <returns>The word's length.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.</exception>
    public static int ReadWord(IStateReader reader, int rowOrdinal, int attributeOrdinal, CompiledExpressionToken[]? tokenExpression, CellKind kind, Span<long> word, int start = 0) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return ((tokenExpression is not null)
            ? ReadTupleWord(
                expression: tokenExpression,
                kind: kind,
                reader: reader,
                rowOrdinal: rowOrdinal,
                start: start,
                word: word
            )
            : reader.Arena.ReadWord(
                attributeOrdinal: ((attributeOrdinal >= 0)
                    ? attributeOrdinal
                    : rowOrdinal
                ),
                rowOrdinal: rowOrdinal,
                start: start,
                time: reader.Time,
                word: word
            ).Length
        );
    }
    /// <summary>Reads a zone's tokens in pile order, each through the pattern's value expression with
    /// <c>$token</c> bound to it; an expression that fails on a token reads that letter as zero.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The zone row's catalog ordinal.</param>
    /// <param name="expression">The compiled value expression.</param>
    /// <param name="kind">The pattern's kind.</param>
    /// <param name="word">The word buffer.</param>
    /// <param name="start">The position the word starts at.</param>
    /// <returns>The word's length.</returns>
    public static int ReadTupleWord(IStateReader reader, int rowOrdinal, CompiledExpressionToken[] expression, CellKind kind, Span<long> word, int start = 0) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var arena = reader.Arena;
        var length = 0;
        var cursor = start;

        try {
            while (arena.TryNextCell(
                cursor: ref cursor,
                key: out var key,
                rowOrdinal: rowOrdinal
            )) {
                var position = (cursor - 1);
                var previousCursor = (position - 1);

                reader.BoundTokenKey = key;
                reader.BoundPreviousKey = (((position > 0) && arena.TryNextCell(
                    cursor: ref previousCursor,
                    key: out var previous,
                    rowOrdinal: rowOrdinal
                ) && (previousCursor == position))
                    ? previous
                    : default
                );
                word[length++] = (RuleExpressions.TryEvaluate(
                    fault: out _,
                    kind: kind,
                    program: expression,
                    reader: reader,
                    value: out var raw
                )
                    ? raw
                    : 0L
                );
            }
        } finally {
            reader.BoundPreviousKey = default;
            reader.BoundTokenKey = default;
        }

        return length;
    }
    /// <summary>Returns the position the word starts at: 0 for an invalid key, the named token's physical position,
    /// or <see cref="int.MaxValue"/> (an empty word) when the row does not hold the token.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The start token's key, or the invalid default.</param>
    /// <returns>The start position.</returns>
    public static int StartPosition(StateArena arena, int rowOrdinal, CellKey key) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (!key.IsValid) {
            return 0;
        }

        if (arena.TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            var position = (slot - arena.Layout[rowOrdinal].CellStart);
            var cursor = position;

            if (
                arena.TryNextCell(
                cursor: ref cursor,
                key: out var held,
                rowOrdinal: rowOrdinal
            ) &&
                (cursor == (position + 1)) &&
                (held == key)
            ) {
                return position;
            }
        }

        return int.MaxValue;
    }
    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (RowFrom is { } live) {
            live.CollectReads(into: into);
        } else {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: RowOrdinal
            ));
        }

        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
        if (AttributeOrdinal >= 0) {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: AttributeOrdinal
            ));
        }

        RuleDataflow.CollectExpression(
            into: into,
            tokens: TokenExpression
        );
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        var occurrenceSearch = (MatchFacet is MatchFacet.At or MatchFacet.Length);

        // A board read copies the row, walks one ray, and an occurrence search then tries every start along that
        // ray: the square is the ray's, never the whole board's.
        if (Board is { } board) {
            var cells = ((long)board.Topology.CellCount);
            var ray = ((board.Direction >= 0) ? ((long)board.Topology.LongestRay(direction: board.Direction)) : cells);

            return RuleWork.Known(units: ((board.Visits + cells) + (occurrenceSearch ? (ray * ray) : 0L)));
        }

        var capacity = ((long)((RowFrom is { } live) ? live.SelectionCapacity : Capacity));
        var expression = (1L + RuleWorkBudget.ExpressionCost(
            context: context,
            tokens: (TokenExpression ?? [])
        ));

        return ((capacity * expression) * (occurrenceSearch ? capacity : 1L));
    }
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var ordinal = RowOrdinal;

        if (
            (RowFrom is { } live) &&
            !live.TryResolve(
            reader: reader,
            rowOrdinal: out ordinal
        )
        ) {
            return RuleFact.Absent(kind: CellKind.Int);
        }

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: ((Board is { } query)
            ? ReadBoardMatch(
                    query: query,
                    reader: reader,
                    rowOrdinal: ordinal
                )
            : ReadWordMatch(
                    reader: reader,
                    rowOrdinal: ordinal
                ))
        );
    }
}
