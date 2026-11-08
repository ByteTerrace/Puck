using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>The arena-facing reads every compiled operand shares: resolving a live key or index, reading one cell as
/// a fact, and reducing a row. Every address is a catalog row ordinal and an interned cell key.</summary>
public static class RuleReads {
    /// <summary>Reads one cell as a fact at the reader's own tick pair: the absent fact when the address named no
    /// cell at all, the faulted fact when computing the address faulted, zero for a cell no row holds, and the live
    /// value otherwise.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal, or <c>-1</c> for no row.</param>
    /// <param name="resolved">What the address named — see
    /// <see cref="ResolveKey(IStateReader, CellKey, CompiledCellRef?)"/>. A named invalid key is a cell no row holds.</param>
    /// <param name="kind">The encoding the fact carries.</param>
    /// <returns>The fact.</returns>
    public static Fact ReadCell(IStateReader reader, int rowOrdinal, Resolved<CellKey> resolved, CellKind kind) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (!resolved.TryNamed(value: out var key)) {
            return ((resolved.Case == FaultCase.Faulted)
                ? Fact.Faulted(kind: kind)
                : Fact.Absent(kind: kind));
        }
        if (
            (rowOrdinal < 0) ||
            !key.IsValid
        ) {
            // A name no key table interns is a cell no row in this world holds, which reads zero like any other
            // unheld cell — never the absent fact, which would fault every arithmetic spelled around it.
            return Fact.Finite(
                kind: kind,
                value: 0L
            );
        }

        var time = reader.Time;

        return Fact.Finite(
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
            sum = BinaryIntegerFunctions.AddSaturating(
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

    /// <summary>Why an address whose key computation faulted refused: the one sentence every read and write that
    /// meets a <see cref="FaultCase.Faulted"/> address reports.</summary>
    public const string KeyFaultReason = "the address's key faulted (overflow, a divide by zero, or a value with no number)";

    /// <summary>Attempts to resolve the key a state-writing effect addresses. Only a dynamic key family that must
    /// create a new runtime address admits one here; read resolution never changes the key table.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="literal">The compile-time key, used when <paramref name="keyFrom"/> is <see langword="null"/>.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <returns>The key (invalid when no cell was named), <see cref="WriteKeyCase.Faulted"/> when computing it
    /// faulted, or <see cref="WriteKeyCase.Refused"/> when a required runtime-key admission failed.</returns>
    public static WriteKey ResolveKeyForWrite(IStateReader reader, CellKey literal, CompiledCellRef? keyFrom) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (keyFrom is not { } reference) {
            return WriteKey.Resolved(key: literal);
        }

        return ResolveReferenceForWrite(
            reader: reader,
            reference: in reference
        );
    }
    /// <summary>Returns what an operand's address named, as the resolution <see cref="ReadCell"/> reads: the key the
    /// interpreter resolved, or nothing.</summary>
    /// <param name="key">What the interpreter resolved the address to.</param>
    /// <returns>The named key, or <see cref="FaultCase.Absent"/>.</returns>
    public static Resolved<CellKey> Named(in ResolvedKey key) => (key.Named
        ? Resolved<CellKey>.Named(value: key.Key)
        : Resolved<CellKey>.Nothing);
    /// <summary>Returns what an operand's literal key named: the key, or nothing when it names no cell.</summary>
    /// <param name="literal">The compile-time key.</param>
    /// <returns>The named key, or <see cref="FaultCase.Absent"/>.</returns>
    public static Resolved<CellKey> Literal(CellKey literal) => (literal.IsValid
        ? Resolved<CellKey>.Named(value: literal)
        : Resolved<CellKey>.Nothing);
    /// <summary>Resolves the interned key a compiled address names for the evaluation in flight (a literal key, a
    /// bound key token, another cell's value read as a key, or a compiled key fact), and what it named. Each caller
    /// decides what a faulted key means where it sits, a read raising it as arithmetic and a write or remove
    /// refusing the firing.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="literal">The compile-time key, used when <paramref name="keyFrom"/> is
    /// <see langword="null"/>.</param>
    /// <param name="keyFrom">The live indirection, or <see langword="null"/>.</param>
    /// <returns>What the address named. An address naming a cell no key table interns is named with the invalid key: no
    /// row holds that cell, and it reads zero. An address naming nothing (an empty zone's endpoint, a pointer spelling
    /// no cell name) is <see cref="FaultCase.Absent"/>, and reads absent. One whose computation faulted is
    /// <see cref="FaultCase.Faulted"/>.</returns>
    public static Resolved<CellKey> ResolveKey(IStateReader reader, CellKey literal, CompiledCellRef? keyFrom) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (keyFrom is not { } reference) {
            return (literal.IsValid
                ? Resolved<CellKey>.Named(value: literal)
                : Resolved<CellKey>.Nothing);
        }

        return ResolveReference(
            reader: reader,
            reference: in reference
        );
    }
    /// <summary>Resolves one live key indirection, and what it named.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="reference">The indirection.</param>
    /// <returns>What the indirection named — see <see cref="ResolveKey(IStateReader, CellKey, CompiledCellRef?)"/>.</returns>
    public static Resolved<CellKey> ResolveReference(IStateReader reader, in CompiledCellRef reference) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return (TryFeed(
            feed: out var feed,
            reader: reader,
            reference: in reference
        )
            ? ResolveReference(
                feed: in feed,
                reader: reader,
                reference: in reference
            )
            : Resolved<CellKey>.Faulted);
    }
    /// <summary>Runs the program an address resolves through, for a caller no enclosing program feeds: an effect's
    /// destination, or a live row selected where no operand reads it. An operand's own address programs are tokens of
    /// the program that reads it, and never reach here.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="reference">The address.</param>
    /// <param name="feed">The value the program left, or the integer zero for an address that carries none.</param>
    /// <returns><see langword="false"/> when the program faulted, which is already raised into the evaluation's
    /// scope; an absence is an answer.</returns>
    public static bool TryFeed(IStateReader reader, in CompiledCellRef reference, out AddressFeed feed) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        feed = default;

        if (reference.FeedProgram is not { } program) {
            return true;
        }

        var frame = new EvaluationFrame(reader: reader);
        var outcome = RuleExpressions.Evaluate(
            frame: ref frame,
            kind: CellKind.Int,
            program: program
        );

        // An absence the program read names no cell; a fault is the caller's.
        if (outcome.TryFinite(raw: out var value)) {
            feed = new AddressFeed(
                Absent: false,
                Value: value
            );
        } else if (outcome.Case == FaultCase.Absent) {
            feed = new AddressFeed(
                Absent: true,
                Value: 0L
            );
        } else {
            return false;
        }

        return true;
    }
    /// <summary>Resolves one live key indirection once the program it resolves through has left its value.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="reference">The indirection.</param>
    /// <param name="feed">What its program left; ignored when the indirection carries none.</param>
    /// <returns>What the indirection named — see <see cref="ResolveKey(IStateReader, CellKey, CompiledCellRef?)"/>.</returns>
    public static Resolved<CellKey> ResolveReference(IStateReader reader, in CompiledCellRef reference, in AddressFeed feed) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (reference.Program is not null) {
            // An integer names that key even when no row holds it. An absence the program read names no cell.
            return (feed.Absent
                ? Resolved<CellKey>.Nothing
                : Resolved<CellKey>.Named(value: IndexKey(
                    index: feed.Value,
                    keys: reader.Arena.Keys
                )));
        }
        if (reference.Custom is { } custom) {
            return custom.Resolve(
                feed: in feed,
                reader: reader
            );
        }

        var key = ResolveBuiltIn(named: out var named, reader: reader, reference: in reference);

        return (named
            ? Resolved<CellKey>.Named(value: key)
            : Resolved<CellKey>.Nothing);
    }

    private static Resolved<long> NamedIndex(bool named, long index) => (named
        ? Resolved<long>.Named(value: index)
        : Resolved<long>.Nothing);
    // The engine's own indirections: none computes arithmetic, so none faults.
    private static CellKey ResolveBuiltIn(IStateReader reader, in CompiledCellRef reference, out bool named) {
        if (reference.Binding is BoundKey.Each or BoundKey.Token or BoundKey.Previous or BoundKey.Write) {
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
    private static WriteKey ResolveReferenceForWrite(IStateReader reader, in CompiledCellRef reference) {
        if (reference.Program is not null) {
            var resolved = ResolveReference(
                reader: reader,
                reference: in reference
            );

            if (resolved.Case == FaultCase.Faulted) {
                return WriteKey.Faulted();
            }

            return WriteKey.Resolved(key: (resolved.TryNamed(value: out var named)
                ? named
                : default));
        }
        if (reference.Custom is { } custom) {
            return custom.ResolveForWrite(reader: reader);
        }

        return WriteKey.Resolved(key: ResolveBuiltIn(
            named: out _,
            reader: reader,
            reference: in reference
        ));
    }

    /// <summary>Resolves one live indirection as an integer index — a live row's table index, a static table's key.
    /// An indirection that spells no integer names nothing.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="reference">The indirection.</param>
    /// <returns>The index when the indirection spells an integer, <see cref="FaultCase.Faulted"/> when computing it
    /// faulted, and <see cref="FaultCase.Absent"/> otherwise.</returns>
    public static Resolved<long> ResolveIndex(IStateReader reader, in CompiledCellRef reference) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return (TryFeed(
            feed: out var feed,
            reader: reader,
            reference: in reference
        )
            ? ResolveIndex(
                feed: in feed,
                reader: reader,
                reference: in reference
            )
            : Resolved<long>.Faulted);
    }
    /// <summary>Resolves one live indirection as an integer index once the program it resolves through has left its
    /// value.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="reference">The indirection.</param>
    /// <param name="feed">What its program left; ignored when the indirection carries none.</param>
    /// <returns>The index when the indirection names one, <see cref="FaultCase.Absent"/> when it names nothing.</returns>
    public static Resolved<long> ResolveIndex(IStateReader reader, in CompiledCellRef reference, in AddressFeed feed) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (reference.Program is not null) {
            return (feed.Absent
                ? Resolved<long>.Nothing
                : Resolved<long>.Named(value: feed.Value));
        }
        if (reference.Custom is { } custom) {
            return custom.ResolveIndex(
                feed: in feed,
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
                return Resolved<long>.Nothing;
            }

            // A pointer answers the integer its cell carries; routing it through the key table would lose every
            // index no name is interned for.
            return Resolved<long>.Named(value: ReadPointer(
                inner: inner,
                reader: reader,
                reference: in reference
            ));
        }

        var key = ResolveBuiltIn(
            named: out _,
            reader: reader,
            reference: in reference
        );
        var spelled = TryKeyIndex(
            keys: reader.Arena.Keys,
            index: out var keyIndex,
            key: key
        );

        return NamedIndex(
            index: keyIndex,
            named: spelled
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

        return keys.TryResolveIndex(
            index: index,
            key: out key
        );
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
        BoundKey.Write => reader.Scratch.WriteKey,
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
