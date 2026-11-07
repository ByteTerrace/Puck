namespace Puck.State.Rules;

/// <summary>An operand that reads a word a per-token program spells: a pattern over a zone, a keyed row or a ring.
/// The interpreter runs that program once per token (<see cref="RuleExpressions.FoldWord"/>) and hands the operand the
/// word's length, so the operand evaluates no expression of its own.</summary>
public interface IWordFoldOperand : IRuleOperand {
    /// <summary>Gets the program run once per token with <c>$token</c> and <c>$previous</c> bound, whose value is
    /// the token's letter, or <see langword="null"/> when the word is the row's own live values.</summary>
    CompiledExpressionToken[]? WordProgram { get; }
    /// <summary>Gets the kind the per-token program evaluates in.</summary>
    CellKind WordKind { get; }

    /// <summary>Locates the word the resolved addresses name.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="keys">What each of <see cref="IRuleOperand.Addresses"/> named.</param>
    /// <param name="rowOrdinal">The source row's catalog ordinal, when a word was located.</param>
    /// <param name="start">The position the word starts at, when a word was located.</param>
    /// <param name="answer">The fact the read answers when no word was located (the row a live selection chose
    /// is none, or the operand reads a board).</param>
    /// <returns><see langword="true"/> when the word is to be spelled and matched.</returns>
    bool TryLocateWord(IStateReader reader, ReadOnlySpan<ResolvedKey> keys, out int rowOrdinal, out int start, out Fact answer);
    /// <summary>Answers what the operand reports about the word the interpreter spelled into
    /// <see cref="IStateReader.PatternWord"/>.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="length">The word's length.</param>
    /// <returns>The fact.</returns>
    Fact MatchWord(IStateReader reader, int length);
}
public static partial class RuleExpressions {
    /// <summary>The most addresses one operand declares: the interpreter resolves them into a frame of this many
    /// entries on the machine stack.</summary>
    public const int MaxAddresses = 4;

    // An address key holds a reference to its key table's identity, so the frame of resolved addresses is an inline
    // array on the machine stack rather than a stackalloc.
    [System.Runtime.CompilerServices.InlineArray(length: MaxAddresses)]
    private struct KeyFrame {
        private ResolvedKey m_first;
    }

    /// <summary>Returns how many values a token consumes from the value stack: the operands of an operator, the
    /// arguments of a call, and for an operand token the values the programs computing its addresses left.</summary>
    /// <param name="token">The compiled token.</param>
    /// <returns>The count.</returns>
    public static int Arity(in CompiledExpressionToken token) => (token.Call?.Arity ?? ((token.Operand is { } operand)
        ? FeedCount(operand: operand)
        : ExpressionOperators.Arity(operation: token.Operation)));
    /// <summary>Returns how many of an operand's addresses are computed by a program the enclosing program carries.</summary>
    /// <param name="operand">The operand.</param>
    /// <returns>The count.</returns>
    public static int FeedCount(IRuleOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        var fed = 0;

        foreach (var address in operand.Addresses) {
            if (address.Feed is not null) {
                fed++;
            }
        }

        return fed;
    }
    /// <summary>Appends the tokens that compute an operand's addresses, in address order: the part of an operand's
    /// lowering that precedes the operand's own token.</summary>
    /// <param name="operand">The operand.</param>
    /// <param name="into">The program being built.</param>
    public static void AppendFeeds(IRuleOperand operand, List<CompiledExpressionToken> into) {
        ArgumentNullException.ThrowIfNull(argument: operand);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var address in operand.Addresses) {
            if (address.Feed is { } feed) {
                into.AddRange(collection: feed);
            }
        }
    }
    /// <summary>Reads one operand: resolves each address it declares, then reads through them. An address whose
    /// resolution faulted is a faulted fact and the operand is not called; an address that names nothing reaches the
    /// operand as such, and the operand decides what that reads as. A pattern over a per-token program has the word
    /// spelled here first.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="operand">The operand.</param>
    /// <param name="feeds">The values the programs computing the operand's fed addresses left, one per fed address in
    /// address order.</param>
    /// <returns>The fact.</returns>
    public static Fact ReadOperand(IStateReader reader, IRuleOperand operand, ReadOnlySpan<AddressFeed> feeds) {
        ArgumentNullException.ThrowIfNull(argument: reader);
        ArgumentNullException.ThrowIfNull(argument: operand);

        var addresses = operand.Addresses;

        // Most operands declare no address and spell no word: they read straight away.
        if (addresses.IsEmpty && (operand is not IWordFoldOperand)) {
            return operand.Read(
                keys: default,
                reader: reader
            );
        }

        return ReadAddressed(
            addresses: addresses,
            feeds: feeds,
            operand: operand,
            reader: reader
        );
    }

    private static Fact ReadAddressed(IStateReader reader, IRuleOperand operand, ReadOnlySpan<OperandAddress> addresses, ReadOnlySpan<AddressFeed> feeds) {
        if (addresses.Length > MaxAddresses) {
            throw new InvalidOperationException(message: $"An operand declares at most {MaxAddresses} addresses; {operand.GetType().Name} declares {addresses.Length}.");
        }

        var frame = default(KeyFrame);
        Span<ResolvedKey> keys = frame;
        var fed = 0;

        for (var slot = 0; (slot < addresses.Length); slot++) {
            var address = addresses[slot];
            var reference = address.Reference;
            var feed = ((address.Feed is null)
                ? default
                : feeds[fed++]);

            if (address.Use == AddressUse.Key) {
                var resolved = RuleReads.ResolveReference(
                    feed: in feed,
                    reader: reader,
                    reference: in reference
                );

                if (resolved.Case == FaultCase.Faulted) {
                    return Fact.Faulted(kind: operand.ValueKind);
                }

                keys[slot] = (resolved.TryNamed(value: out var key)
                    ? new ResolvedKey(
                        Key: key,
                        Named: true
                    )
                    : default);

                continue;
            }

            var index = RuleReads.ResolveIndex(
                feed: in feed,
                reader: reader,
                reference: in reference
            );

            if (index.Case == FaultCase.Faulted) {
                return Fact.Faulted(kind: operand.ValueKind);
            }

            keys[slot] = (index.TryNamed(value: out var named)
                ? new ResolvedKey(
                    Index: named,
                    Key: default,
                    Named: true
                )
                : default);
        }

        var resolvedKeys = keys[..addresses.Length];

        if (operand is IWordFoldOperand { WordProgram: { } program } word) {
            if (!word.TryLocateWord(
                answer: out var answer,
                keys: resolvedKeys,
                reader: reader,
                rowOrdinal: out var rowOrdinal,
                start: out var start
            )) {
                return answer;
            }

            var spelled = FoldWord(
                body: program,
                kind: word.WordKind,
                reader: reader,
                rowOrdinal: rowOrdinal,
                start: start,
                word: reader.PatternWord
            );

            if (!spelled.TryFinite(raw: out var length)) {
                return ((spelled.Case == FaultCase.Absent)
                    ? Fact.Absent(kind: operand.ValueKind)
                    : Fact.Faulted(kind: operand.ValueKind));
            }

            return word.MatchWord(
                length: ((int)length),
                reader: reader
            );
        }

        return operand.Read(
            keys: resolvedKeys,
            reader: reader
        );
    }

    /// <summary>Spells a row's tokens in pile order as a word, each through the per-token program with
    /// <c>$token</c> and <c>$previous</c> bound. A letter is a number or the word spells nothing: the fold stops at the
    /// first token whose program is absent or faults and answers why, the fault already raised into the evaluation's
    /// scope. This is the one fold a pattern's word is spelled by, whether the interpreter reads the pattern in a
    /// program or a detached caller (the console's match read-back) reads it over a disclosed reader.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="body">The per-token program.</param>
    /// <param name="kind">The kind the program evaluates in.</param>
    /// <param name="word">The word buffer.</param>
    /// <param name="start">The position the word starts at.</param>
    /// <returns>The word's length as a finite answer when every token spelled a letter; an absence when a token's
    /// letter is absent; otherwise the token's fault.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.</exception>
    public static Outcome FoldWord(IStateReader reader, int rowOrdinal, CompiledExpressionToken[] body, CellKind kind, Span<long> word, int start = 0) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var arena = reader.Arena;
        var cursor = start;
        var frame = new EvaluationFrame(reader: reader);
        var length = 0;

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

                var letter = Evaluate(
                    frame: ref frame,
                    kind: kind,
                    program: body
                );

                if (!letter.TryFinite(raw: out var raw)) {
                    return letter;
                }

                word[length++] = raw;
            }
        } finally {
            reader.BoundPreviousKey = default;
            reader.BoundTokenKey = default;
        }

        return Outcome.Finite(raw: length);
    }
}
/// <summary>Reads an operand where no enclosing program feeds it: a gate side, an effect's source or a read-back.</summary>
public static class RuleOperandReads {
    /// <summary>Reads an operand, running the program behind each address it computes first. A program that faults
    /// is a faulted fact and an absent one names no cell. The interpreter reads an operand inside a program through
    /// <see cref="RuleExpressions.ReadOperand"/> with those programs already folded into the program's tokens, and
    /// this is the same read for an operand that is a source of its own.</summary>
    /// <param name="operand">The operand.</param>
    /// <param name="reader">The evaluation in flight.</param>
    /// <returns>The fact.</returns>
    public static Fact Read(this IRuleOperand operand, IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: operand);
        ArgumentNullException.ThrowIfNull(argument: reader);

        Span<AddressFeed> feeds = stackalloc AddressFeed[RuleExpressions.MaxAddresses];
        var fed = 0;

        foreach (var address in operand.Addresses) {
            if (address.Feed is null) {
                continue;
            }

            var reference = address.Reference;

            if (!RuleReads.TryFeed(
                feed: out var feed,
                reader: reader,
                reference: in reference
            )) {
                return Fact.Faulted(kind: operand.ValueKind);
            }

            feeds[fed++] = feed;
        }

        return RuleExpressions.ReadOperand(
            feeds: feeds[..fed],
            operand: operand,
            reader: reader
        );
    }
}
