namespace Puck.State;

/// <summary>The board-combine contract: which sources an operation reads, its admission, and the member cells it
/// produces, computed on cell sets held as words (one bit per cell, <see cref="CompiledTopology.CellWords"/> words) so
/// an operation over a 361-cell board is a handful of word operations rather than a pass over its cells. Storage
/// adapters resolve the source rows to member sets and write the result.</summary>
public static class BoardCombination {
    /// <summary>Whether the operation reads a left board.</summary>
    /// <param name="operation">The operation.</param>
    public static bool NeedsLeft(BoardCombineOp operation) => (operation is not (BoardCombineOp.Fill or BoardCombineOp.Clear));
    /// <summary>Whether the operation reads a right board.</summary>
    /// <param name="operation">The operation.</param>
    public static bool NeedsRight(BoardCombineOp operation) => (operation is BoardCombineOp.And or BoardCombineOp.Or or BoardCombineOp.Xor or BoardCombineOp.AndNot or BoardCombineOp.Reach);
    /// <summary>Validates operation fields and the member value against an already resolved target board.</summary>
    /// <param name="combine">The authored operation.</param>
    /// <param name="row">The target row.</param>
    /// <param name="empty">The target's empty value.</param>
    /// <param name="topology">The target's topology.</param>
    /// <param name="direction">The resolved shift direction, or -1.</param>
    /// <param name="element">The resolved image element, or -1.</param>
    /// <param name="value">The admitted member value to write — <see cref="StateTransform.BoardCombine.Value"/> as
    /// the row's <see cref="StateRowShape.TryAdmitWrite"/> decides it (clamped under
    /// <see cref="StateOverflow.Saturate"/>), or zero when refused.</param>
    /// <param name="reason">The refusal, or empty.</param>
    /// <returns>Whether the operation is admissible; source existence and topology agreement remain the adapter's responsibility.</returns>
    public static bool TryValidate(StateTransform.BoardCombine combine, StateRowShape row, long empty, CompiledTopology topology, out int direction, out int element, out long value, out string reason) {
        ArgumentNullException.ThrowIfNull(combine);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(topology);
        direction = -1;
        element = -1;
        value = 0L;
        var operation = combine.Operation;

        if (
            !Enum.IsDefined(value: operation) ||
            (NeedsLeft(operation: operation) != (combine.Left is not null)) ||
            (NeedsRight(operation: operation) != (combine.Right is not null)) ||
            ((operation == BoardCombineOp.Shift) != (combine.Direction is not null)) ||
            ((operation == BoardCombineOp.Image) != (combine.Element is not null))
        ) {
            reason = "boardCombine takes left for every operation but fill and clear, right for and/or/xor/andNot/reach, direction for shift alone, and element for image alone";
            return false;
        }
        if (
            !row.TryAdmitWrite(
            current: 0L,
            operand: combine.Value,
            write: StateWriteKind.Set,
            stored: out value,
            reason: out _
        ) ||
            ((row.Kind == CellKind.Bool) && (combine.Value is not (0 or 1))) ||
            (value == empty)
        ) {
            value = 0L;
            reason = "boardCombine writes a member value the board admits and that is not the board's own empty value";
            return false;
        }
        direction = ((combine.Direction is { } directionName)
            ? topology.Direction(token: directionName)
            : -1
        );
        element = ((combine.Element is { } elementName)
            ? topology.Element(name: elementName)
            : -1
        );
        if (
            ((combine.Direction is not null) && (direction < 0)) ||
            ((combine.Element is not null) && (element < 0))
        ) {
            reason = "boardCombine names a direction or point-group element its topology does not declare";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Writes the member cells a validated membership operation produces from its sources' member cells.
    /// <see cref="BoardCombineOp.Copy"/> carries values rather than membership, so it is the adapter's: this answers its
    /// left members unchanged.</summary>
    /// <param name="operation">An operation admitted by <see cref="TryValidate"/>.</param>
    /// <param name="topology">The shared topology.</param>
    /// <param name="left">The left board's member cells, <see cref="CompiledTopology.CellWords"/> words, when the
    /// operation reads it.</param>
    /// <param name="right">The right board's member cells, when the operation reads it.</param>
    /// <param name="direction">The validated direction.</param>
    /// <param name="element">The validated element.</param>
    /// <param name="members">The result's member cells, <see cref="CompiledTopology.CellWords"/> words; must overlap
    /// neither source.</param>
    /// <param name="scratch">The working storage a reach borrows its frontier from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> or <paramref name="scratch"/> is
    /// <see langword="null"/>.</exception>
    public static void Members(BoardCombineOp operation, CompiledTopology topology, ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, int direction, int element, Span<ulong> members, ArenaScratch scratch) {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scratch);

        var words = topology.CellWords;
        var result = members[..words];

        switch (operation) {
            case BoardCombineOp.Shift:
                topology.ShiftCells(destination: result, direction: direction, source: left);

                return;
            case BoardCombineOp.Image:
                topology.ImageCells(destination: result, element: element, source: left);

                return;
            case BoardCombineOp.Reach:
                _ = topology.Reach(passable: right, reach: result, scratch: scratch, seeds: left);

                return;
        }
        // Fill and Not are complements (of nothing and of left), so every operation is formed without them first.
        for (var word = 0; (word < words); word++) {
            result[word] = operation switch {
                BoardCombineOp.Copy or BoardCombineOp.Not => left[word],
                BoardCombineOp.And => (left[word] & right[word]),
                BoardCombineOp.Or => (left[word] | right[word]),
                BoardCombineOp.Xor => (left[word] ^ right[word]),
                BoardCombineOp.AndNot => (left[word] & ~right[word]),
                _ => 0UL,
            };
        }
        if (operation is BoardCombineOp.Fill or BoardCombineOp.Not) {
            Puck.Maths.BitWords.Complement(members: topology.CellCount, words: result);
        }
    }
    /// <summary>Returns the most element operations <see cref="Members"/> costs for one operation over a topology,
    /// read from the topology alone: a word operation per word, a shift or image of the left set, or a whole reach
    /// (<see cref="CompiledTopology.ReachWork"/>).</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="topology">The topology.</param>
    /// <param name="direction">The validated direction of a shift, or -1.</param>
    /// <returns>The element operations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> is <see langword="null"/>.</exception>
    public static long MembersWork(BoardCombineOp operation, CompiledTopology topology, int direction) {
        ArgumentNullException.ThrowIfNull(topology);

        var words = (long)topology.CellWords;
        var perMember = (words + (2L * topology.CellCount));

        return (operation switch {
            BoardCombineOp.Reach => topology.ReachWork(),
            // A uniform step is a shift and a mask of every word; any other step, and an image, reads each member's cell.
            BoardCombineOp.Shift => (topology.TryUniformStepWords(direction: direction, landing: out _, step: out _) ? (2L * words) : perMember),
            BoardCombineOp.Image => perMember,
            _ => (2L * words),
        });
    }
}
