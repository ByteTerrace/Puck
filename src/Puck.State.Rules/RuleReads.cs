using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>The arena-facing reads every compiled operand shares: resolving a live key or index, reading one cell as
/// a fact, and reducing a row. Every address is a catalog row ordinal and an interned cell key.</summary>
public static class RuleReads {
    /// <summary>Reads one cell as a fact at the reader's own tick pair: the absent fact when the address named no
    /// cell at all, zero for a cell no row holds, and the live value otherwise.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal, or <c>-1</c> for no row.</param>
    /// <param name="key">The interned cell key, or the invalid default for a cell no row holds.</param>
    /// <param name="kind">The encoding the fact carries.</param>
    /// <param name="named">Whether the address named a cell — see
    /// <see cref="ResolveKey(IStateReader, CellKey, CompiledCellRef?, out bool)"/>.</param>
    /// <returns>The fact.</returns>
    public static RuleFact ReadCell(IStateReader reader, int rowOrdinal, CellKey key, CellKind kind, bool named) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (!named) {
            return RuleFact.Absent(kind: kind);
        }
        if (
            (rowOrdinal < 0) ||
            !key.IsValid
        ) {
            // A name no key table interns is a cell no row in this world holds, which reads zero like any other
            // unheld cell — never the absent fact, which would fault every arithmetic spelled around it.
            return RuleFact.Finite(
                kind: kind,
                value: 0L
            );
        }

        var time = reader.Time;

        return RuleFact.Finite(
            kind: kind,
            value: (reader.Arena.TryReadLiveNumber(
                key: key,
                rowOrdinal: rowOrdinal,
                time: in time,
                value: out var value
            )
            ? value
            : 0L)
        );
    }
    /// <summary>Reads one cell as a fixed-point value, lifting an integer or boolean cell to Q48.16.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="key">The interned cell key.</param>
    /// <returns>The value; zero when the cell is absent.</returns>
    public static FixedQ4816 ReadFixed(IStateReader reader, int rowOrdinal, CellKey key) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (
            (rowOrdinal < 0) ||
            !key.IsValid
        ) {
            return FixedQ4816.Zero;
        }

        var time = reader.Time;

        if (!reader.Arena.TryReadLive(
            key: key,
            rowOrdinal: rowOrdinal,
            time: in time,
            value: out var carried
        )) {
            return FixedQ4816.Zero;
        }

        return (carried.Kind switch {
            CellKind.Fixed => FixedQ4816.FromRawBits(value: carried.AsFixed),
            CellKind.Bool => FixedQ4816.FromInteger(value: (carried.AsBool
            ? 1L
            : 0L)),
            CellKind.Int => FixedQ4816.FromInteger(value: carried.AsInt),
            _ => FixedQ4816.Zero,
        });
    }
    /// <summary>Reduces one row's live cells in the row's own raw encoding, with an optional keyed filter whose
    /// nonzero cells admit a candidate and an optional inclusive range over each candidate's live value.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The reduced row's catalog ordinal.</param>
    /// <param name="op">The aggregate.</param>
    /// <param name="filterOrdinal">The filter row's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="range">Inclusive bounds in the source's raw encoding, or <see langword="null"/>.</param>
    /// <returns>The aggregate; zero for no admitted cell.</returns>
    public static long Reduce(IStateReader reader, int rowOrdinal, StateReduceOp op, int filterOrdinal, (long Lower, long Upper)? range) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var arena = reader.Arena;
        var count = arena.CellCount(rowOrdinal: rowOrdinal);

        if (
            (op == StateReduceOp.Count) &&
            (filterOrdinal < 0) &&
            (range is null)
        ) {
            return count;
        }
        if (count == 0) {
            return 0L;
        }

        var time = reader.Time;
        var admitted = 0L;
        var sum = 0L;
        var extremum = 0L;
        var seen = false;
        var cursor = 0;

        while (arena.TryNextCell(
            cursor: ref cursor,
            key: out var key,
            rowOrdinal: rowOrdinal
        )) {
            if (
                (filterOrdinal >= 0) &&
                (!arena.TryReadLiveNumber(
                key: key,
                rowOrdinal: filterOrdinal,
                time: in time,
                value: out var gate
            ) || (gate == 0L))
            ) {
                continue;
            }
            if (!arena.TryReadLiveNumber(
                key: key,
                rowOrdinal: rowOrdinal,
                time: in time,
                value: out var value
            )) {
                continue;
            }
            if (
                (range is { } bounds) &&
                ((value < bounds.Lower) || (value > bounds.Upper))
            ) {
                continue;
            }

            admitted++;
            sum = RuleWorkBudget.SaturatingAdd(
                left: sum,
                right: value
            );
            if (
                !seen ||
                ((op == StateReduceOp.Max)
                ? (value > extremum)
                : (value < extremum))
            ) {
                extremum = value;
                seen = true;
            }
        }

        return (op switch {
            StateReduceOp.Count => admitted,
            StateReduceOp.Sum => sum,
            StateReduceOp.Max or StateReduceOp.Min => (seen
            ? extremum
            : 0L),
            _ => 0L,
        });
    }
    /// <summary>Returns the Lehmer rank of an ordered zone's token order relative to its token domain's cell order —
    /// over k! for k tokens — read off the live arena: <see cref="StateReader.DomainOrdinals(StateArena, int, int, Span{int})"/>
    /// ranked by <see cref="StateReader.ArrangementRank(ReadOnlySpan{int})"/>.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The ordered zone's catalog ordinal.</param>
    /// <param name="domainOrdinal">The token domain row's catalog ordinal, or <c>-1</c>.</param>
    /// <returns>The rank, or <c>-1</c> when <paramref name="domainOrdinal"/> names no row, the zone holds more than
    /// <see cref="StateReader.MaxArrangementTokens"/> tokens, or it holds a token its domain does not
    /// declare.</returns>
    public static long ArrangementRank(IStateReader reader, int rowOrdinal, int domainOrdinal) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        Span<int> ordinals = stackalloc int[StateReader.MaxArrangementTokens];
        var count = StateReader.DomainOrdinals(
            arena: reader.Arena,
            domainOrdinal: domainOrdinal,
            ordinals: ordinals,
            rowOrdinal: rowOrdinal
        );

        return ((count < 0)
            ? -1L
            : StateReader.ArrangementRank(domainOrdinals: ordinals[..count])
        );
    }
    /// <summary>Resolves the interned key a compiled address names for the evaluation in flight: a literal key, a
    /// bound key token, another cell's value read as a key, or a compiled key fact.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="literal">The compile-time key, used when <paramref name="keyFrom"/> is
    /// <see langword="null"/>.</param>
    /// <param name="keyFrom">The live indirection, or <see langword="null"/>.</param>
    /// <returns>The key, or the invalid default when the indirection names no cell.</returns>
    public static CellKey ResolveKey(IStateReader reader, CellKey literal, CompiledCellRef? keyFrom) => ResolveKey(
        keyFrom: keyFrom,
        literal: literal,
        named: out _,
        reader: reader
    );
    /// <summary>Attempts to resolve the key a state-writing effect addresses. Only a dynamic key family that must
    /// create a new runtime address admits one here; read resolution never changes the key table.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="literal">The compile-time key, used when <paramref name="keyFrom"/> is <see langword="null"/>.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="key">The resolved key, or invalid when no cell was named.</param>
    /// <param name="reason">Why a required runtime-key admission failed, or empty on success.</param>
    /// <returns><see langword="true"/> when the write may proceed; otherwise the firing must refuse.</returns>
    public static bool TryResolveKeyForWrite(IStateReader reader, CellKey literal, CompiledCellRef? keyFrom, out CellKey key, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (keyFrom is not { } reference) {
            key = literal;
            reason = string.Empty;

            return true;
        }

        return TryResolveReferenceForWrite(
            key: out key,
            reader: reader,
            reason: out reason,
            reference: in reference
        );
    }
    /// <summary>Resolves the interned key a compiled address names, and whether it named a cell at all.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="literal">The compile-time key, used when <paramref name="keyFrom"/> is
    /// <see langword="null"/>.</param>
    /// <param name="keyFrom">The live indirection, or <see langword="null"/>.</param>
    /// <param name="named">Whether the address named a cell. An address naming a cell no key table interns answers
    /// <see langword="true"/> beside the invalid key: no row holds that cell, and it reads zero. An address naming
    /// nothing — an empty zone's endpoint, a pointer spelling no cell name — answers <see langword="false"/>, and
    /// reads absent.</param>
    /// <returns>The key, or the invalid default.</returns>
    public static CellKey ResolveKey(IStateReader reader, CellKey literal, CompiledCellRef? keyFrom, out bool named) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (keyFrom is not { } reference) {
            named = literal.IsValid;

            return literal;
        }

        return ResolveReference(
            named: out named,
            reader: reader,
            reference: in reference
        );
    }
    /// <summary>Resolves one live key indirection.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="reference">The indirection.</param>
    /// <returns>The key, or the invalid default when the indirection names no cell.</returns>
    public static CellKey ResolveReference(IStateReader reader, in CompiledCellRef reference) => ResolveReference(
        named: out _,
        reader: reader,
        reference: in reference
    );
    /// <summary>Resolves one live key indirection, and whether it named a cell at all.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="reference">The indirection.</param>
    /// <param name="named">Whether the indirection named a cell — see
    /// <see cref="ResolveKey(IStateReader, CellKey, CompiledCellRef?, out bool)"/>.</param>
    /// <returns>The key, or the invalid default.</returns>
    public static CellKey ResolveReference(IStateReader reader, in CompiledCellRef reference, out bool named) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (reference.Custom is { } custom) {
            return custom.Resolve(
                named: out named,
                reader: reader
            );
        }
        if (reference.Binding is BoundKey.Each or BoundKey.Token or BoundKey.Previous) {
            // A binding that is not bound for this evaluation falls back to the reference's own key, which is how
            // the first token of a pattern word reads $previous as the cell no row holds rather than as no cell.
            var bound = BoundKeyOf(
                binding: reference.Binding,
                reader: reader
            );
            var token = (bound.IsValid
                ? bound
                : reference.Key
            );

            named = token.IsValid;

            return token;
        }
        if (reference.Binding != BoundKey.None) {
            named = true;

            return IndexKey(
                keys: reader.Arena.Keys,
                index: reader.BoundIndex(key: reference.Binding)
            );
        }
        if (reference.RowOrdinal < 0) {
            named = false;

            return default;
        }

        var inner = ((reference.InnerKeyBinding == BoundKey.None)
            ? reference.Key
            : BoundKeyOf(
                binding: reference.InnerKeyBinding,
                reader: reader
            )
        );

        if (!inner.IsValid) {
            named = false;

            return default;
        }

        var keys = reader.Arena.Keys;

        if (reference.Kind == CellKind.Text) {
            // Text that spells no cell name addresses no cell: a rule may write any text into the pointer cell, so
            // the read answers the absence rather than refusing.
            if (
                !reader.Arena.TryRead(
                key: inner,
                rowOrdinal: reference.RowOrdinal,
                value: out var carried
            ) ||
                (carried.Kind != CellKind.Text) ||
                (carried.AsText is not { Length: > 0 } text) ||
                !CellName.TryParse(
                candidate: text,
                name: out var name,
                reason: out _
            )
            ) {
                named = false;

                return default;
            }

            named = true;

            return (keys.TryResolve(
                key: out var textKey,
                name: name
            )
                ? textKey
                : default
            );
        }

        named = true;

        return IndexKey(
            keys: keys,
            index: ReadPointer(
                inner: inner,
                reader: reader,
                reference: in reference
            )
        );
    }

    private static bool TryResolveReferenceForWrite(IStateReader reader, in CompiledCellRef reference, out CellKey key, out string reason) {
        if (reference.Custom is { } custom) {
            return custom.TryResolveForWrite(
                key: out key,
                reader: reader,
                reason: out reason
            );
        }

        key = ResolveReference(
            named: out _,
            reader: reader,
            reference: in reference
        );
        reason = string.Empty;

        return true;
    }

    /// <summary>Resolves one live indirection as an integer index — a live row's table index, a static table's key.
    /// An indirection that spells no integer names nothing.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="reference">The indirection.</param>
    /// <param name="index">The index, on success.</param>
    /// <returns><see langword="true"/> when the indirection spells an integer.</returns>
    public static bool TryResolveIndex(IStateReader reader, in CompiledCellRef reference, out long index) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (reference.Custom is { } custom) {
            return custom.TryResolveIndex(
                index: out index,
                reader: reader
            );
        }
        if (
            (reference.Binding == BoundKey.None) &&
            (reference.Kind != CellKind.Text) &&
            (reference.RowOrdinal >= 0)
        ) {
            var inner = ((reference.InnerKeyBinding == BoundKey.None)
                ? reference.Key
                : BoundKeyOf(
                    binding: reference.InnerKeyBinding,
                    reader: reader
                )
            );

            if (!inner.IsValid) {
                index = 0L;

                return false;
            }

            // A pointer answers the integer its cell carries; routing it through the key table would lose every
            // index no name is interned for.
            index = ReadPointer(
                inner: inner,
                reader: reader,
                reference: in reference
            );

            return true;
        }

        var key = ResolveReference(
            reader: reader,
            reference: in reference
        );

        return TryKeyIndex(
            keys: reader.Arena.Keys,
            index: out index,
            key: key
        );
    }
    /// <summary>Returns the interned key whose name is an integer's decimal spelling, without minting one the
    /// arena does not already hold.</summary>
    /// <param name="keys">The runtime key table whose names resolve through.</param>
    /// <param name="index">The integer.</param>
    /// <param name="key">The key, on success.</param>
    /// <returns><see langword="true"/> when the arena already interns the name.</returns>
    public static bool TryIndexKey(CellKeyTable keys, long index, out CellKey key) {
        ArgumentNullException.ThrowIfNull(argument: keys);

        key = default;

        return (CellName.TryParse(
            candidate: IndexKeyCache.Get(index: index),
            name: out var name,
            reason: out _
        ) && keys.TryResolve(
            key: out key,
            name: name
        ));
    }
    /// <summary>Returns the integer an interned key's name spells.</summary>
    /// <param name="keys">The runtime key table that resolves the key.</param>
    /// <param name="key">The key.</param>
    /// <param name="index">The integer, on success.</param>
    /// <returns><see langword="true"/> when the key's name is an integer.</returns>
    public static bool TryKeyIndex(CellKeyTable keys, CellKey key, out long index) {
        ArgumentNullException.ThrowIfNull(argument: keys);

        if (keys.TryGetName(
            key: key,
            name: out var name
        )) {
            return long.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out index,
                s: name.Value,
                style: System.Globalization.NumberStyles.Integer
            );
        }

        index = 0L;

        return false;
    }

    private static CellKey BoundKeyOf(IStateReader reader, BoundKey binding) => (binding switch {
        BoundKey.Each => reader.BoundEachKey,
        BoundKey.Token => reader.BoundTokenKey,
        BoundKey.Previous => reader.BoundPreviousKey,
        _ => IndexKey(
        keys: reader.Arena.Keys,
        index: reader.BoundIndex(key: binding)
    ),
    });
    private static CellKey IndexKey(CellKeyTable keys, long index) => (TryIndexKey(
        index: index,
        key: out var key,
        keys: keys
    )
        ? key
        : default
    );
    // A pointer cell the row does not hold reads zero, the same as any other unheld cell.
    private static long ReadPointer(IStateReader reader, in CompiledCellRef reference, CellKey inner) {
        var time = reader.Time;

        return (reader.Arena.TryReadLiveNumber(
            key: inner,
            rowOrdinal: reference.RowOrdinal,
            time: in time,
            value: out var index
        )
            ? index
            : 0L
        );
    }
}
