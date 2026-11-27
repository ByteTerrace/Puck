namespace Puck.State.Rules;

/// <summary>A rule's <see cref="Rule.Zones"/> table as the evaluator indexes it: ordered zones over one token
/// domain, each entry's catalog ordinal in index order, with <c>-1</c> at every gap. A table whose entries are
/// families (<see cref="PerBinding"/>) holds, for each entry, the member the binding's own <c>$each</c> key selects:
/// <c>$zones[i]</c> is entry <c>i</c>'s member at the binding's index, so every binding has a table of its own.</summary>
public sealed class ZoneTable {
    private readonly int[] m_ordinals;
    private readonly RowFamily[]? m_families;
    private readonly List<LiveRow> m_references = [];
    private readonly HashSet<int> m_selectionLocals = [];

    /// <summary>Initializes the table.</summary>
    /// <param name="names">The authored entries, in index order; an empty entry is a gap.</param>
    /// <param name="ordinals">Each entry's catalog ordinal, or its family's first member's for a family entry;
    /// <c>-1</c> at a gap.</param>
    /// <param name="tokenDomain">The token domain every zone in the table is ordered over.</param>
    /// <param name="kind">The cell kind every zone in the table shares.</param>
    /// <param name="indices">The non-gap indices spelled as interned cell keys, for a <c>forEach</c> over the table.</param>
    /// <param name="families">Each entry's family, aligned with <paramref name="names"/>, when every entry is a
    /// family whose member the binding's key selects; otherwise <see langword="null"/>.</param>
    public ZoneTable(IReadOnlyList<string> names, int[] ordinals, string tokenDomain, CellKind kind, CellKey[] indices, RowFamily[]? families = null) {
        ArgumentNullException.ThrowIfNull(argument: names);
        ArgumentNullException.ThrowIfNull(argument: ordinals);
        ArgumentNullException.ThrowIfNull(argument: indices);

        Indices = indices;
        Kind = kind;
        Names = names;
        TokenDomain = tokenDomain;
        m_families = families;
        m_ordinals = ordinals;
    }

    /// <summary>Gets a value indicating whether every entry is a family whose member the binding's own <c>$each</c>
    /// key selects, so each binding reaches its own members and no other binding's.</summary>
    public bool PerBinding => (m_families is not null);

    /// <summary>Gets the non-gap indices spelled as interned cell keys, in index order.</summary>
    public CellKey[] Indices { get; }
    /// <summary>Gets the cell kind every zone in the table shares.</summary>
    public CellKind Kind { get; }
    /// <summary>Gets the authored entries, in index order; an empty entry is a gap. Read-back only.</summary>
    public IReadOnlyList<string> Names { get; }
    /// <summary>Gets the catalog ordinals of every entry, in index order; <c>-1</c> at a gap.</summary>
    public IReadOnlyList<int> Ordinals => m_ordinals;
    /// <summary>Gets every live reference the rule spells against this table, one per distinct spelling, in the
    /// order they were compiled. An evaluation applies only when every one of them selects a row.</summary>
    public IReadOnlyList<LiveRow> References => m_references;
    /// <summary>Gets the token domain every zone in the table is ordered over.</summary>
    public string TokenDomain { get; }

    /// <summary>Appends every zone the table can select, as a whole-row access.</summary>
    /// <param name="into">The access set being collected.</param>
    /// <param name="isSet">Whether the access is a write.</param>
    public void CollectRows(List<CellAccess> into, bool isSet) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (m_families is { } families) {
            foreach (var family in families) {
                if (family.Count > 0) {
                    LiveRow.CollectMembers(each: true, family: family, into: into, isSet: isSet);
                }
            }

            return;
        }
        foreach (var ordinal in m_ordinals) {
            if (ordinal >= 0) {
                into.Add(item: new CellAccess(
                    IsSet: isSet,
                    Key: default,
                    RowOrdinal: ordinal
                ));
            }
        }
    }
    /// <summary>Gets the ordinals of every rule local an index of <see cref="References"/> reads. The references
    /// resolve before the gate (an evaluation applies only when each selects a row), so these locals are judged with
    /// the gate rather than deferred to the firing (<see cref="CompiledRule.Deferred"/>).</summary>
    public IReadOnlySet<int> SelectionLocals => m_selectionLocals;

    /// <summary>Returns the live reference a spelling names, minting it on first use — the same spelling in two
    /// places is one reference, resolved once per evaluation.</summary>
    /// <param name="index">The compiled index indirection.</param>
    /// <param name="locals">The ordinals of the rule locals <paramref name="index"/> reads.</param>
    /// <param name="spelling">The authored spelling.</param>
    /// <returns>The reference.</returns>
    public LiveRow Reference(CompiledCellRef index, IEnumerable<int> locals, string spelling) {
        ArgumentNullException.ThrowIfNull(argument: locals);

        m_selectionLocals.UnionWith(other: locals);
        foreach (var existing in m_references) {
            if (string.Equals(
                a: existing.Spelling,
                b: spelling,
                comparisonType: StringComparison.Ordinal
            )) {
                return existing;
            }
        }

        var minted = LiveRow.OverZones(
            index: index,
            spelling: spelling,
            table: this
        );

        m_references.Add(item: minted);

        return minted;
    }
    /// <summary>Resolves an index to its zone's catalog ordinal; an index outside the table or at a gap answers
    /// none.</summary>
    /// <param name="index">The live index.</param>
    /// <param name="each">The integer the binding's <c>$each</c> key spells, which selects a family entry's member;
    /// <see langword="null"/> when the evaluation binds none.</param>
    /// <param name="rowOrdinal">The selected zone's catalog ordinal, on success.</param>
    /// <returns><see langword="true"/> when the index selects a zone.</returns>
    public bool TryOrdinal(long index, long? each, out int rowOrdinal) {
        if (m_families is { } families) {
            if ((((ulong)index) < ((ulong)families.Length)) && (each is { } member) && (((ulong)member) <= int.MaxValue) &&
                families[index].TryGetOrdinal(index: (int)member, ordinal: out rowOrdinal)) {
                return true;
            }
        } else if (
            (((ulong)index) < ((ulong)m_ordinals.Length)) &&
            (m_ordinals[index] >= 0)
        ) {
            rowOrdinal = m_ordinals[index];

            return true;
        }

        rowOrdinal = -1;

        return false;
    }
}
/// <summary>A row position chosen live: <c>$zones[&lt;index&gt;]</c> against the enclosing rule's
/// <see cref="ZoneTable"/>, or <c>&lt;family&gt;[&lt;index&gt;]</c> against a declared <see cref="RowFamily"/>. The
/// bracketed index resolves before each read or firing to an integer that selects a row; an index outside the table
/// or family selects none, and the rule's evaluation is not for it.</summary>
public sealed class LiveRow {
    private readonly RowFamily m_family;

    private LiveRow(ZoneTable? table, RowFamily family, CompiledCellRef index, string spelling, bool ownMember = false) {
        Index = index;
        Spelling = spelling;
        Table = table;
        m_family = family;
        SelectsOwnMember = ((table is null) && ownMember);
    }

    /// <summary>Gets the family the index selects a member of, or <see langword="null"/> for a zone table.</summary>
    public RowFamily? Family => ((Table is null)
        ? m_family
        : null
    );
    /// <summary>Gets the live index indirection.</summary>
    public CompiledCellRef Index { get; }

    /// <summary>Gets the widest row this reference can select, for pricing a live read.</summary>
    /// <param name="context">The admitted row reservations.</param>
    public long SelectionCapacity(IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var capacity = 0L;
        var rows = new List<CellAccess>();

        CollectRows(into: rows, isSet: false);
        foreach (var row in rows) {
            capacity = Math.Max(val1: capacity, val2: context.RowCapacity(rowOrdinal: row.RowOrdinal));
        }

        return capacity;
    }

    /// <summary>Gets the authored spelling.</summary>
    public string Spelling { get; }
    /// <summary>Gets the rule's zone table, or <see langword="null"/> for a family selection.</summary>
    public ZoneTable? Table { get; }

    /// <summary>Creates a family-member selection.</summary>
    /// <param name="family">The declared family.</param>
    /// <param name="index">The live index indirection.</param>
    /// <param name="spelling">The authored spelling.</param>
    /// <param name="sweepsFamily">Whether the enclosing rule sweeps a row family, whose binding's own member is the one
    /// <c>$member</c> names; in any other sweep it is the one <c>$each</c> names.</param>
    /// <returns>The reference.</returns>
    public static LiveRow OverFamily(RowFamily family, CompiledCellRef index, string spelling, bool sweepsFamily = false) => new(
        family: family,
        index: index,
        ownMember: (sweepsFamily ? index.IsMember : index.IsEach),
        spelling: spelling,
        table: null
    );
    /// <summary>Creates a zone-table selection.</summary>
    /// <param name="table">The rule's zone table.</param>
    /// <param name="index">The live index indirection.</param>
    /// <param name="spelling">The authored spelling.</param>
    /// <returns>The reference.</returns>
    public static LiveRow OverZones(ZoneTable table, CompiledCellRef index, string spelling) {
        ArgumentNullException.ThrowIfNull(argument: table);

        return new LiveRow(
            family: default,
            index: index,
            spelling: spelling,
            table: table
        );
    }
    /// <summary>Appends the cells the index resolves through.</summary>
    /// <param name="into">The read set being collected.</param>
    public void CollectIndexReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: Index
    );
    /// <summary>Appends every row a read through this reference can touch, plus the cells the index resolves
    /// through.</summary>
    /// <param name="into">The read set being collected.</param>
    public void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (Table is { } table) {
            table.CollectRows(
                into: into,
                isSet: false
            );
        } else {
            CollectMembers(into: into, isSet: false);
        }

        CollectIndexReads(into: into);
    }
    /// <summary>Appends the whole row an operand reads: every row <paramref name="live"/> can select, plus the cells
    /// its index resolves through, or the fixed row when <paramref name="live"/> is <see langword="null"/>.</summary>
    /// <param name="live">The live row, or <see langword="null"/> for a fixed row.</param>
    /// <param name="rowOrdinal">The fixed row's catalog ordinal, read when <paramref name="live"/> is
    /// <see langword="null"/>.</param>
    /// <param name="into">The read set being collected.</param>
    public static void CollectWholeRowReads(LiveRow? live, int rowOrdinal, List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (live is { } selected) {
            selected.CollectReads(into: into);
        } else {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: rowOrdinal
            ));
        }
    }
    /// <summary>Gets a value indicating whether the selection is the family member the binding's own key spells
    /// (<see cref="RuleReads.BoundOwnIndex"/>: <c>$member</c> in a sweep over a row family, <c>$each</c> in any other):
    /// each binding of a sweep then reaches its own member row and no other.</summary>
    public bool SelectsOwnMember { get; }
    /// <summary>Appends every row this reference can select, without the cells its index resolves through.</summary>
    /// <param name="into">The access set being collected.</param>
    /// <param name="isSet">Whether the access replaces the row's contents.</param>
    public void CollectRows(List<CellAccess> into, bool isSet) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (Table is { } table) {
            table.CollectRows(
                into: into,
                isSet: isSet
            );

            return;
        }

        CollectMembers(into: into, isSet: isSet);
    }
    private void CollectMembers(List<CellAccess> into, bool isSet) => CollectMembers(each: SelectsOwnMember, family: m_family, into: into, isSet: isSet);
    /// <summary>Appends every member row of a family, as any cell of it. A member the binding's own key selects is
    /// tagged with the family index that selects it (<see cref="CellReach.Member"/>), so a due list wakes the bindings
    /// whose own key spells it.</summary>
    /// <param name="family">The family.</param>
    /// <param name="each">Whether the binding's own key selects the member.</param>
    /// <param name="into">The access set being collected.</param>
    /// <param name="isSet">Whether the access replaces the row's contents.</param>
    internal static void CollectMembers(RowFamily family, bool each, List<CellAccess> into, bool isSet) {
        var slots = family.Slots;
        var count = ((slots is null) ? family.Count : slots.Length);

        for (var index = 0; (index < count); index++) {
            var ordinal = ((slots is null) ? (family.FirstOrdinal + index) : slots[index]);

            if (ordinal < 0) {
                continue;
            }
            into.Add(item: new CellAccess(
                IsSet: isSet,
                Key: default,
                Member: (each ? index : -1),
                Reach: (each ? CellReach.Member : CellReach.Key),
                RowOrdinal: ordinal
            ));
        }
    }
    /// <summary>Appends every fact the index carries, so a rule's needs fold the whole reference.</summary>
    /// <param name="into">The needs being built.</param>
    public void CollectFacts(RuleNeedsBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.AddFact(fact: Index.Custom);
    }
    /// <summary>Resolves the row for the evaluation in flight, telling an index whose computation faulted apart from
    /// one that selects no row.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <returns>The selected row's catalog ordinal when the index selects a row, <see cref="FaultCase.Faulted"/> when
    /// computing it faulted, and <see cref="FaultCase.Absent"/> otherwise.</returns>
    public Resolved<int> Resolve(IStateReader reader) {
        var reference = Index;
        var resolved = RuleReads.ResolveIndex(
            reader: reader,
            reference: in reference
        );

        return Select(reader: reader, resolved: resolved);
    }
    /// <summary>Resolves the row once the program its index resolves through has left its value.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="feed">What the index's program left; ignored when the index carries none.</param>
    /// <returns>As <see cref="Resolve(IStateReader)"/> answers.</returns>
    public Resolved<int> Resolve(IStateReader reader, in AddressFeed feed) {
        var reference = Index;

        return Select(reader: reader, resolved: RuleReads.ResolveIndex(
            feed: in feed,
            reader: reader,
            reference: in reference
        ));
    }
    /// <summary>Selects the row an index the interpreter already resolved names: an operand reads its live row through
    /// this, since the index is one of its declared addresses.</summary>
    /// <param name="index">What the index address named.</param>
    /// <param name="each">The integer the binding's own key spells (<see cref="RuleReads.BoundOwnIndex"/>),
    /// which selects a family entry's member when <see cref="SelectsByBinding"/>; otherwise ignored.</param>
    /// <returns>The selected row's catalog ordinal when the index selects a row, and
    /// <see cref="FaultCase.Absent"/> otherwise.</returns>
    public Resolved<int> Select(in ResolvedKey index, long? each) => (index.Named
        ? Select(each: each, resolved: Resolved<long>.Named(value: index.Index))
        : Resolved<int>.Nothing);
    /// <summary>Gets a value indicating whether the row selected also depends on the binding's own <c>$each</c> key:
    /// a zone table whose entries are families.</summary>
    public bool SelectsByBinding => (Table is { PerBinding: true });

    /// <summary>Gets the program the index resolves through, or <see langword="null"/>.</summary>
    public CompiledExpressionToken[]? FeedProgram => Index.FeedProgram;

    private Resolved<int> Select(IStateReader reader, Resolved<long> resolved) => Select(
        each: (SelectsByBinding ? RuleReads.BoundOwnIndex(reader: reader) : null),
        resolved: resolved
    );
    private Resolved<int> Select(long? each, Resolved<long> resolved) {
        if (!resolved.TryNamed(value: out var index)) {
            return ((resolved.Case == FaultCase.Faulted)
                ? Resolved<int>.Faulted
                : Resolved<int>.Nothing);
        }
        if (Table is { } table) {
            return (table.TryOrdinal(
                each: each,
                index: index,
                rowOrdinal: out var tableOrdinal
            )
                ? Resolved<int>.Named(value: tableOrdinal)
                : Resolved<int>.Nothing);
        }

        return (m_family.TryGetOrdinal(
            index: ((int)Math.Clamp(
                max: int.MaxValue,
                min: int.MinValue,
                value: index
            )),
            ordinal: out var familyOrdinal
        )
            ? Resolved<int>.Named(value: familyOrdinal)
            : Resolved<int>.Nothing);
    }
}
/// <summary>The key a declared local computed: the cell whose name is the local's integer.</summary>
public sealed class LocalKeyFact : RuleKeyFact {
    /// <summary>Initializes the fact.</summary>
    /// <param name="ordinal">The local's slot in the rule.</param>
    /// <param name="source">The local's compiled expression, whose reads this key rests on.</param>
    public LocalKeyFact(int ordinal, CompiledExpressionToken[]? source) {
        Ordinal = ordinal;
        Source = source;
    }

    /// <summary>Gets the local's slot in the rule.</summary>
    public int Ordinal { get; }
    /// <summary>Gets the local's compiled expression.</summary>
    public CompiledExpressionToken[]? Source { get; }

    /// <summary>Appends what the local's expression reads: a read addressed by this key moves whenever they do, so a
    /// schedule or a memo built over the read rests on them too.</summary>
    /// <param name="into">The read set being collected.</param>
    public override void CollectReads(List<CellAccess> into) => RuleDataflow.CollectExpression(
        into: into,
        tokens: Source
    );
    /// <inheritdoc/>
    public override Resolved<CellKey> Resolve(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        // A binding spells an integer, so it always names a cell; one no key table interns is a cell no row holds.
        return Resolved<CellKey>.Named(value: (reader.Arena.Keys.TryResolveIndex(
            index: reader.LocalValue(ordinal: Ordinal),
            key: out var key
        )
            ? key
            : default
        ));
    }
    /// <inheritdoc/>
    public override Resolved<long> ResolveIndex(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return Resolved<long>.Named(value: reader.LocalValue(ordinal: Ordinal));
    }
}
