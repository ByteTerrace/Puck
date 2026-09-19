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
    public PatternOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, CompiledPattern pattern, BoardNeighbourQuery? board, int attributeOrdinal, MatchFacet matchFacet, CompiledExpressionToken[]? tokenExpression, long capacity, LiveRow? rowFrom = null) : base(valueKind: CellKind.Int) {
        ArgumentNullException.ThrowIfNull(argument: pattern);

        AttributeOrdinal = attributeOrdinal;
        Board = board;
        Capacity = capacity;
        Key = key;
        KeyFrom = keyFrom;
        MatchFacet = matchFacet;
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
        var values = reader.BoardScratch(cells: query.Topology.CellCount);
        var word = reader.PatternWord;
        var key = RuleReads.ResolveKey(
            keyFrom: KeyFrom,
            literal: Key,
            reader: reader
        );
        var origin = -1;

        if (
            key.IsValid &&
            reader.Catalog.Keys.TryGetName(
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

        var length = ((TokenExpression is { } tokenExpression)
            ? ReadTupleWord(
                expression: tokenExpression,
                kind: Pattern.Source.Kind,
                reader: reader,
                rowOrdinal: rowOrdinal,
                start: start,
                word: word
            )
            : arena.ReadWord(
                attributeOrdinal: ((AttributeOrdinal >= 0)
                ? AttributeOrdinal
                : rowOrdinal),
                rowOrdinal: rowOrdinal,
                start: start,
                word: word
            ).Length
        );

        return ((MatchFacet == MatchFacet.Prefix)
            ? Pattern.LongestAcceptedPrefix(values: word[..length])
            : Pattern.Match(values: word[..length])
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
        var count = arena.CellCount(rowOrdinal: rowOrdinal);
        var length = 0;

        try {
            for (var position = start; (position < count); position++) {
                if (!arena.TryKeyAt(
                    key: out var key,
                    position: position,
                    rowOrdinal: rowOrdinal
                )) {
                    continue;
                }

                reader.BoundTokenKey = key;
                reader.BoundPreviousKey = (((position > 0) && arena.TryKeyAt(
                    key: out var previous,
                    position: (position - 1),
                    rowOrdinal: rowOrdinal
                ))
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
    /// <summary>Returns the position the word starts at: 0 for an invalid key, the named token's position, or the
    /// row's count (an empty word) when the row does not hold the token.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The start token's key, or the invalid default.</param>
    /// <returns>The start position.</returns>
    public static int StartPosition(StateArena arena, int rowOrdinal, CellKey key) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (!key.IsValid) {
            return 0;
        }

        var count = arena.CellCount(rowOrdinal: rowOrdinal);

        for (var position = 0; (position < count); position++) {
            if (
                arena.TryKeyAt(
                key: out var candidate,
                position: position,
                rowOrdinal: rowOrdinal
            ) &&
                (candidate == key)
            ) {
                return position;
            }
        }

        return count;
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
    public override RuleWork Cost(IRuleCostContext context) => ((Board is { } board)
        ? RuleWork.Known(units: (board.Topology.CellCount + board.Visits))
        : (((long)((RowFrom is { } live)
            ? live.SelectionCapacity
            : Capacity)) * (1L + RuleWorkBudget.ExpressionCost(
                context: context,
                tokens: (TokenExpression ?? [])
            )))
    );
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
