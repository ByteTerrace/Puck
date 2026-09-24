using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.State;

/// <summary>The boolean-closed cell-set vocabulary: the same operators a pattern spells over a word, spelled over
/// the positions of a family, a board, or a zone. Every expression lowers to one
/// <see cref="CellSet"/> whose width is the carrier's own.</summary>
/// <remarks>A source names a carrier and an inclusive value range, and its set holds every position of that carrier
/// whose live value falls in the range — the value every other read of that cell answers at the lowering's time. The
/// three sources address different things — a family addresses its member rows, a board its topology cells, a zone
/// its pile positions — but all three answer with a set over <c>0..elements-1</c>, so the operators never know which
/// carrier they are over. Complement is relative to that
/// element count, which every source in one expression must agree on.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CellSetExpression.Board), "board")]
[JsonDerivedType(typeof(CellSetExpression.Family), "family")]
[JsonDerivedType(typeof(CellSetExpression.Zone), "zone")]
[JsonDerivedType(typeof(CellSetExpression.Everything), "all")]
[JsonDerivedType(typeof(CellSetExpression.Nothing), "none")]
[JsonDerivedType(typeof(CellSetExpression.Any), "any")]
[JsonDerivedType(typeof(CellSetExpression.Both), "both")]
[JsonDerivedType(typeof(CellSetExpression.Complement), "not")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[Union]
public abstract record CellSetExpression {
    private CellSetExpression() { }

    /// <summary>The topology cells of a lattice row whose value falls in an inclusive range.</summary>
    /// <param name="Row">The lattice row.</param>
    /// <param name="Low">The least value a member carries.</param>
    /// <param name="High">The greatest value a member carries.</param>
    public sealed record Board(CellName Row, long Low, long High) : CellSetExpression;
    /// <summary>The member rows of a family whose slot value falls in an inclusive range.</summary>
    /// <param name="Name">The family.</param>
    /// <param name="Low">The least value a member carries.</param>
    /// <param name="High">The greatest value a member carries.</param>
    public sealed record Family(CellName Name, long Low, long High) : CellSetExpression;
    /// <summary>The pile positions of an ordered or keyed row whose value falls in an inclusive range.</summary>
    /// <param name="Row">The row.</param>
    /// <param name="Low">The least value a member carries.</param>
    /// <param name="High">The greatest value a member carries.</param>
    public sealed record Zone(CellName Row, long Low, long High) : CellSetExpression;
    /// <summary>Every position of the carrier.</summary>
    public sealed record Everything : CellSetExpression;
    /// <summary>No position at all.</summary>
    public sealed record Nothing : CellSetExpression;
    /// <summary>The union of the items.</summary>
    /// <param name="Items">The items.</param>
    public sealed record Any(IReadOnlyList<CellSetExpression> Items) : CellSetExpression;
    /// <summary>The intersection of the items.</summary>
    /// <param name="Items">The items.</param>
    public sealed record Both(IReadOnlyList<CellSetExpression> Items) : CellSetExpression;
    /// <summary>Every position the item does not hold.</summary>
    /// <param name="Item">The item.</param>
    public sealed record Complement(CellSetExpression Item) : CellSetExpression;
}
/// <summary>Lowers a <see cref="CellSetExpression"/> against a <see cref="StateArena"/> to one
/// <see cref="CellSet"/>.</summary>
public static class CellSetLowering {
    /// <summary>The widest admitted carrier, matching the state row and topology cell ceiling.</summary>
    public const int MaxElements = TopologyCompilation.MaxCells;

    private static CellSet Combine(CellSet left, CellSet right, bool union) {
        var length = left.Length;
        var wordCount = CellSet.WordCount(length: length);
        ulong[]? tail = null;

        if (wordCount > 4) {
            tail = new ulong[(wordCount - 4)];
            for (var word = 4; (word < wordCount); word++) {
                tail[(word - 4)] = (union
                    ? left.Word(index: word) | right.Word(index: word)
                    : left.Word(index: word) & right.Word(index: word)
                );
            }
        }
        return new(
            length,
            (union ? left.Word(index: 0) | right.Word(index: 0) : left.Word(index: 0) & right.Word(index: 0)),
            (union ? left.Word(index: 1) | right.Word(index: 1) : left.Word(index: 1) & right.Word(index: 1)),
            (union ? left.Word(index: 2) | right.Word(index: 2) : left.Word(index: 2) & right.Word(index: 2)),
            (union ? left.Word(index: 3) | right.Word(index: 3) : left.Word(index: 3) & right.Word(index: 3)),
            tail
        );
    }
    private static CellSet Complement(CellSet set, int elements) {
        var wordCount = CellSet.WordCount(length: elements);
        ulong[]? tail = null;

        if (wordCount > 4) {
            tail = new ulong[(wordCount - 4)];
            for (var word = 4; (word < wordCount); word++) {
                tail[(word - 4)] = FullWord(elements: elements, word: word) & ~set.Word(index: word);
            }
        }
        return new(
            elements,
            FullWord(elements: elements, word: 0) & ~set.Word(index: 0),
            FullWord(elements: elements, word: 1) & ~set.Word(index: 1),
            FullWord(elements: elements, word: 2) & ~set.Word(index: 2),
            FullWord(elements: elements, word: 3) & ~set.Word(index: 3),
            tail
        );
    }
    private static ulong FullWord(int elements, int word) {
        var admitted = Math.Clamp(
            max: 64,
            min: 0,
            value: (elements - (word * 64))
        );

        return admitted.LowMask<ulong>();
    }
    private static CellSet Full(int elements) {
        var wordCount = CellSet.WordCount(length: elements);
        ulong[]? tail = null;

        if (wordCount > 4) {
            tail = new ulong[(wordCount - 4)];
            for (var word = 4; (word < wordCount); word++) {
                tail[(word - 4)] = FullWord(elements: elements, word: word);
            }
        }
        return new(
            elements,
            FullWord(elements: elements, word: 0),
            FullWord(elements: elements, word: 1),
            FullWord(elements: elements, word: 2),
            FullWord(elements: elements, word: 3),
            tail
        );
    }
    private static void SetBit(int position, ref ulong word0, ref ulong word1, ref ulong word2, ref ulong word3, ulong[]? tail) {
        var word = (position / 64);
        var bit = (1UL << (position % 64));

        switch (word) {
            case 0: word0 |= bit; break;
            case 1: word1 |= bit; break;
            case 2: word2 |= bit; break;
            case 3: word3 |= bit; break;
            default: tail![(word - 4)] |= bit; break;
        }
    }
    private static bool TryResolveRow(StateArena arena, CellName name, out int rowOrdinal, out string reason) {
        if (!arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: name
        )) {
            reason = $"cell set names row '{name.Value}', which the catalog does not declare";
            rowOrdinal = -1;

            return false;
        }

        reason = string.Empty;
        rowOrdinal = handle.Ordinal;

        return true;
    }
    // The token domain a family's members are ordered or keyed over, and its size. Null means the members are slot
    // rows, which carry one value each and are their own positions; a non-empty reason means the members disagree
    // about their shape or their domain, which has no one carrier.
    private static int? TryFamilyDomain(StateArena arena, RowFamily family, out int domainOrdinal, out string reason) {
        var domain = -1;
        var slots = 0;
        var tokens = 0;

        domainOrdinal = -1;
        reason = string.Empty;

        foreach (var rowOrdinal in family.Ordinals()) {
            ref readonly var layout = ref arena.Layout[rowOrdinal];

            if (layout.Shape == RowShape.Slot) {
                slots++;

                continue;
            }
            if (layout.Shape is not (RowShape.Ordered or RowShape.Keyed)) {
                reason = $"cell set reads family '{family.Name.Value}' member row {rowOrdinal}, which is neither a slot, an ordered, nor a keyed row";

                return null;
            }
            if ((domain >= 0) && (layout.DomainOrdinal != domain)) {
                reason = $"cell set reads family '{family.Name.Value}', whose members stand over different token domains";

                return null;
            }

            domain = layout.DomainOrdinal;
            tokens++;
        }

        if (tokens == 0) {
            return null;
        }
        if (slots > 0) {
            reason = $"cell set reads family '{family.Name.Value}', which mixes slot members with ordered or keyed ones";

            return null;
        }
        if (domain < 0) {
            reason = $"cell set reads family '{family.Name.Value}', whose members declare no token domain to read positions from";

            return null;
        }

        domainOrdinal = domain;

        return arena.Layout[domain].CellCapacity;
    }
    private static bool TrySourceElements(StateArena arena, CellSetExpression expression, out int elements, out string reason) {
        switch (expression) {
            case CellSetExpression.Board board: {
                    if (!TryResolveRow(
                        name: board.Row,
                        reason: out reason,
                        rowOrdinal: out var rowOrdinal,
                        arena: arena
                    )) {
                        elements = -1;

                        return false;
                    }

                    ref readonly var layout = ref arena.Layout[rowOrdinal];

                    if (layout.Shape != RowShape.Lattice) {
                        reason = $"cell set reads board '{board.Row.Value}', which is not a lattice row";
                        elements = -1;

                        return false;
                    }

                    elements = layout.CellCapacity;

                    break;
                }
            case CellSetExpression.Family family: {
                    if (!arena.Catalog.TryGetFamily(
                        family: out var range,
                        name: family.Name
                    )) {
                        reason = $"cell set names family '{family.Name.Value}', which the catalog does not declare";
                        elements = -1;

                        return false;
                    }

                    // A family of slot members is one value per member, so the members ARE the positions. A family
                    // whose members are ordered or keyed rows stands over a token domain instead: the positions are
                    // that domain's keys and the width is its size, so such a set combines with any other source
                    // over the same domain.
                    elements = ((TryFamilyDomain(
                        arena: arena,
                        domainOrdinal: out _,
                        family: range,
                        reason: out reason
                    ) is { } domain)
                        ? domain
                        : range.Count);

                    if (reason.Length > 0) {
                        elements = -1;

                        return false;
                    }

                    break;
                }
            case CellSetExpression.Zone zone: {
                    if (!TryResolveRow(
                        name: zone.Row,
                        reason: out reason,
                        rowOrdinal: out var rowOrdinal,
                        arena: arena
                    )) {
                        elements = -1;

                        return false;
                    }

                    ref readonly var layout = ref arena.Layout[rowOrdinal];

                    if (layout.Shape is not (RowShape.Ordered or RowShape.Keyed)) {
                        reason = $"cell set reads zone '{zone.Row.Value}', which is neither an ordered nor a keyed row";
                        elements = -1;

                        return false;
                    }

                    elements = layout.CellCapacity;

                    break;
                }
            default: {
                    reason = string.Empty;
                    elements = -1;

                    return true;
                }
        }

        reason = string.Empty;

        return true;
    }
    private static bool TryLowerSource(StateArena arena, CellSetExpression expression, int elements, in ArenaTime time, out CellSet set, out string reason) {
        set = default;
        var wordCount = CellSet.WordCount(length: elements);
        var word0 = 0UL;
        var word1 = 0UL;
        var word2 = 0UL;
        var word3 = 0UL;
        var tail = ((wordCount > 4)
            ? new ulong[(wordCount - 4)]
            : null
        );

        switch (expression) {
            case CellSetExpression.Board board: {
                    _ = TryResolveRow(
                        name: board.Row,
                        reason: out reason,
                        rowOrdinal: out var rowOrdinal,
                        arena: arena
                    );

                    for (var cell = 0; (cell < elements); cell++) {
                        if (
                            arena.TryReadBoardCell(
                            cell: cell,
                            rowOrdinal: rowOrdinal,
                            value: out var value
                        ) &&
                            (value >= board.Low) &&
                            (value <= board.High)
                        ) {
                            SetBit(position: cell, tail: tail, word0: ref word0, word1: ref word1, word2: ref word2, word3: ref word3);
                        }
                    }

                    break;
                }
            case CellSetExpression.Family family: {
                    _ = arena.Catalog.TryGetFamily(
                        family: out var range,
                        name: family.Name
                    );

                    if (TryFamilyDomain(
                        arena: arena,
                        domainOrdinal: out var domainOrdinal,
                        family: range,
                        reason: out reason
                    ) is not null) {
                        // A token stands in the set when SOME member row holds it with a value inside the band.
                        var cursor = 0;

                        while (arena.TryNextCell(cursor: ref cursor, key: out var token, rowOrdinal: domainOrdinal)) {
                            var position = (cursor - 1);

                            if (position >= elements) { break; }

                            foreach (var rowOrdinal in range.Ordinals()) {
                                if (
                                    arena.TryReadLiveNumber(
                                    key: token,
                                    rowOrdinal: rowOrdinal,
                                    time: in time,
                                    value: out var held
                                ) &&
                                    (held >= family.Low) &&
                                    (held <= family.High)
                                ) {
                                    SetBit(position: position, tail: tail, word0: ref word0, word1: ref word1, word2: ref word2, word3: ref word3);

                                    break;
                                }
                            }
                        }

                        break;
                    }
                    if (reason.Length > 0) {
                        return false;
                    }

                    var member = 0;

                    foreach (var rowOrdinal in range.Ordinals()) {
                        if (
                            arena.TryKeyAt(
                            key: out var slot,
                            position: 0,
                            rowOrdinal: rowOrdinal
                        ) &&
                            arena.TryReadLiveNumber(
                            key: slot,
                            rowOrdinal: rowOrdinal,
                            time: in time,
                            value: out var raw
                        ) &&
                            (raw >= family.Low) &&
                            (raw <= family.High)
                        ) {
                            SetBit(position: member, tail: tail, word0: ref word0, word1: ref word1, word2: ref word2, word3: ref word3);
                        }

                        member++;
                    }

                    break;
                }
            case CellSetExpression.Zone zone: {
                    _ = TryResolveRow(
                        name: zone.Row,
                        reason: out reason,
                        rowOrdinal: out var rowOrdinal,
                        arena: arena
                    );

                    var cursor = 0;

                    while (arena.TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: rowOrdinal)) {
                        var position = (cursor - 1);

                        if (position >= elements) { break; }
                        if (
                            arena.TryReadLiveNumber(key: key, rowOrdinal: rowOrdinal, time: in time, value: out var raw) &&
                            (raw >= zone.Low) &&
                            (raw <= zone.High)
                        ) {
                            SetBit(position: position, tail: tail, word0: ref word0, word1: ref word1, word2: ref word2, word3: ref word3);
                        }
                    }

                    break;
                }
            case CellSetExpression.Everything: {
                    set = Full(elements: elements);
                    reason = string.Empty;
                    return true;
                }
            default: {
                    break;
                }
        }

        set = new(
            length: elements,
            tail: tail,
            word0: word0,
            word1: word1,
            word2: word2,
            word3: word3
        );
        reason = string.Empty;

        return true;
    }

    /// <summary>Returns the element count every source of an expression agrees on.</summary>
    /// <param name="arena">The arena the sources read.</param>
    /// <param name="expression">The expression.</param>
    /// <param name="elements">The element count, on success.</param>
    /// <param name="reason">Why the expression has no one width, or empty on success.</param>
    /// <returns><see langword="true"/> when the expression names at least one source and every source agrees.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> or <paramref name="expression"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryElementCount(StateArena arena, CellSetExpression expression, out int elements, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: arena);
        ArgumentNullException.ThrowIfNull(argument: expression);

        elements = -1;

        if (!TryWiden(
            arena: arena,
            elements: ref elements,
            expression: expression,
            reason: out reason
        )) {
            return false;
        }
        if (elements < 0) {
            reason = "cell set names no family, board, or zone, so it has no width to complement against";

            return false;
        }

        return true;
    }
    /// <summary>Lowers an expression to the set of positions it holds, over the width its sources agree on.</summary>
    /// <param name="arena">The arena the sources read.</param>
    /// <param name="expression">The expression.</param>
    /// <param name="time">The clocks a source cell's value-over-time trait is evaluated against.</param>
    /// <param name="set">The lowered set, on success.</param>
    /// <param name="reason">Why the expression was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the expression lowered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> or <paramref name="expression"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryLower(StateArena arena, CellSetExpression expression, in ArenaTime time, out CellSet set, out string reason) {
        set = default;

        return (TryElementCount(
            arena: arena,
            elements: out var elements,
            expression: expression,
            reason: out reason
        ) && TryLower(
            arena: arena,
            elements: elements,
            expression: expression,
            reason: out reason,
            set: out set,
            time: in time
        ));
    }
    /// <summary>Lowers an expression to the set of positions it holds, over a declared width.</summary>
    /// <param name="arena">The arena the sources read.</param>
    /// <param name="expression">The expression.</param>
    /// <param name="elements">The carrier's element count.</param>
    /// <param name="time">The clocks a source cell's value-over-time trait is evaluated against.</param>
    /// <param name="set">The lowered set, on success.</param>
    /// <param name="reason">Why the expression was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the expression lowered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> or <paramref name="expression"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elements"/> is outside the carrier's
    /// bounds.</exception>
    public static bool TryLower(StateArena arena, CellSetExpression expression, int elements, in ArenaTime time, out CellSet set, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: arena);
        ArgumentNullException.ThrowIfNull(argument: expression);
        ArgumentOutOfRangeException.ThrowIfNegative(value: elements);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MaxElements,
            value: elements
        );
        set = CellSet.Empty(length: elements);

        switch (expression) {
            case CellSetExpression.Any any: {
                    var first = true;

                    // Indexed rather than enumerated: an interface enumerator is a heap object on every lowering.
                    for (var index = 0; (index < any.Items.Count); index++) {
                        var item = any.Items[index];

                        if (!TryLower(
                            arena: arena,
                            elements: elements,
                            expression: item,
                            reason: out reason,
                            set: out var member,
                            time: in time
                        )) {
                            return false;
                        }

                        if (first) {
                            set = member;
                            first = false;
                        } else {
                            set = Combine(
                                left: set,
                                right: member,
                                union: true
                            );
                        }
                    }

                    reason = string.Empty;

                    return true;
                }
            case CellSetExpression.Both both: {
                    var first = true;

                    for (var index = 0; (index < both.Items.Count); index++) {
                        var item = both.Items[index];

                        if (!TryLower(
                            arena: arena,
                            elements: elements,
                            expression: item,
                            reason: out reason,
                            set: out var member,
                            time: in time
                        )) {
                            set = CellSet.Empty(length: elements);

                            return false;
                        }

                        if (first) {
                            set = member;
                            first = false;
                        } else {
                            set = Combine(
                                left: set,
                                right: member,
                                union: false
                            );
                        }
                    }
                    if (first) { set = Full(elements: elements); }

                    reason = string.Empty;

                    return true;
                }
            case CellSetExpression.Complement complement: {
                    if (!TryLower(
                        arena: arena,
                        elements: elements,
                        expression: complement.Item,
                        reason: out reason,
                        set: out var item,
                        time: in time
                    )) {
                        return false;
                    }

                    set = Complement(
                        elements: elements,
                        set: item
                    );

                    return true;
                }
            default: {
                    return (TrySourceElements(
                        arena: arena,
                        elements: out var width,
                        expression: expression,
                        reason: out reason
                    ) && Agrees(
                        declared: elements,
                        expression: expression,
                        reason: ref reason,
                        width: width
                    ) && TryLowerSource(
                        arena: arena,
                        elements: elements,
                        expression: expression,
                        reason: out reason,
                        set: out set,
                        time: in time
                    ));
                }
        }
    }

    private static bool Agrees(CellSetExpression expression, int declared, int width, ref string reason) {
        if (
            (width < 0) ||
            (width == declared)
        ) {
            return true;
        }

        reason = $"cell set mixes carriers: {Describe(expression: expression)} addresses {width} positions where the set is over {declared}";

        return false;
    }
    private static string Describe(CellSetExpression expression) => expression switch {
        CellSetExpression.Board board => $"board '{board.Row.Value}'",
        CellSetExpression.Family family => $"family '{family.Name.Value}'",
        CellSetExpression.Zone zone => $"zone '{zone.Row.Value}'",
        _ => "the set",
    };
    private static bool TryWiden(StateArena arena, CellSetExpression expression, ref int elements, out string reason) {
        switch (expression) {
            case CellSetExpression.Any any: {
                    for (var index = 0; (index < any.Items.Count); index++) {
                        var item = any.Items[index];

                        if (!TryWiden(
                            arena: arena,
                            elements: ref elements,
                            expression: item,
                            reason: out reason
                        )) {
                            return false;
                        }
                    }

                    reason = string.Empty;

                    return true;
                }
            case CellSetExpression.Both both: {
                    for (var index = 0; (index < both.Items.Count); index++) {
                        var item = both.Items[index];

                        if (!TryWiden(
                            arena: arena,
                            elements: ref elements,
                            expression: item,
                            reason: out reason
                        )) {
                            return false;
                        }
                    }

                    reason = string.Empty;

                    return true;
                }
            case CellSetExpression.Complement complement: {
                    return TryWiden(
                        arena: arena,
                        elements: ref elements,
                        expression: complement.Item,
                        reason: out reason
                    );
                }
            default: {
                    if (!TrySourceElements(
                        arena: arena,
                        elements: out var width,
                        expression: expression,
                        reason: out reason
                    )) {
                        return false;
                    }
                    if (width < 0) {
                        return true;
                    }
                    if (elements < 0) {
                        elements = width;

                        return true;
                    }

                    return Agrees(
                        declared: elements,
                        expression: expression,
                        reason: ref reason,
                        width: width
                    );
                }
        }
    }
}
/// <summary>One declared, reusable cell set: a name a rule addresses and the expression it stands for.</summary>
/// <param name="Name">The set's stable name — unique within the document.</param>
/// <param name="Set">The expression the name stands for.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CellSetRow(CellName Name, CellSetExpression Set);
