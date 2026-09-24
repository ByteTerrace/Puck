namespace Puck.State.Rules;

/// <summary>One key of an <see cref="ArenaTransform.SortZone"/>: a numeric attribute row over the zone's token
/// domain, addressed by catalog ordinal.</summary>
/// <param name="RowOrdinal">The attribute row's catalog ordinal.</param>
/// <param name="Descending">Whether the greatest value comes first under this key.</param>
public readonly record struct ArenaSortKey(int RowOrdinal, bool Descending);
/// <summary>One weighted term of an <see cref="ArenaTransform.Mix"/>.</summary>
/// <param name="Source">The term's vector source.</param>
/// <param name="Weight">The term's weight, in [-1000, 1000] and never zero.</param>
public readonly record struct ArenaMixTerm(VectorSource Source, int Weight);
/// <summary>A <see cref="StateTransform"/> with every row, cell key, board cell, topology direction, pattern, and
/// admitted value already resolved against a catalog: the form the transform kernels apply and the only form that
/// reaches a <see cref="StateArena"/>.</summary>
/// <remarks>Resolution happens once, through
/// <see cref="RuleCompiler.TryResolveTransform(StateTransform, RuleCompileContext, out ArenaTransform?, out string)"/>,
/// so no row or key name is read on the firing path, except the sources of a declared cell set, which
/// <see cref="CellSetLowering"/> resolves against the arena's catalog each time it lowers the set. The parts a rule
/// resolves fresh every firing — a dynamic key and a live zone end — travel beside the transform in an
/// <see cref="ArenaTransformBinding"/> rather than in a rebuilt record.</remarks>
[Union]
public abstract record ArenaTransform {
    private protected ArenaTransform() { }

    /// <summary>Reorders an ordered zone into the arrangement at a Lehmer rank read from an integer cell.</summary>
    /// <param name="RowOrdinal">The ordered zone's catalog ordinal.</param>
    /// <param name="DomainRowOrdinal">The zone's token-domain row's catalog ordinal.</param>
    /// <param name="FromRowOrdinal">The integer row the rank is read from.</param>
    /// <param name="FromKey">The cell of that row.</param>
    public sealed record Arrange(int RowOrdinal, int DomainRowOrdinal, int FromRowOrdinal, CellKey FromKey) : ArenaTransform;
    /// <summary>Rewrites a board from one or two sources over the same topology, cell by cell. A source is a board
    /// row or a declared cell set.</summary>
    /// <param name="RowOrdinal">The board written.</param>
    /// <param name="Operation">What is written.</param>
    /// <param name="LeftRowOrdinal">The first source board's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="RightRowOrdinal">The second source board's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="Direction">The validated direction ordinal for a shift, or <c>-1</c>.</param>
    /// <param name="Element">The validated point-group element for an image, or <c>-1</c>.</param>
    /// <param name="Value">The admitted value written to every member.</param>
    /// <param name="LeftSet">The first source when it is a declared cell set, read in place of
    /// <paramref name="LeftRowOrdinal"/>; <see langword="null"/> otherwise.</param>
    /// <param name="RightSet">The second source when it is a declared cell set, read in place of
    /// <paramref name="RightRowOrdinal"/>; <see langword="null"/> otherwise.</param>
    public sealed record BoardCombine(int RowOrdinal, BoardCombineOp Operation, int LeftRowOrdinal, int RightRowOrdinal, int Direction, int Element, long Value,
        CellSetRow? LeftSet = null, CellSetRow? RightSet = null) : ArenaTransform;
    /// <summary>Clears every enclosed group beside one board cell, writing the board's empty value over its
    /// members.</summary>
    /// <param name="RowOrdinal">The board's catalog ordinal.</param>
    /// <param name="From">The placed value's cell key; a dynamic key arrives in the binding instead.</param>
    /// <param name="Origin">The placed value's topology cell ordinal, or <c>-1</c> when a dynamic key supplies
    /// it.</param>
    /// <param name="Lower">The enclosed range's inclusive low end.</param>
    /// <param name="Upper">The enclosed range's inclusive high end.</param>
    public sealed record ClearEnclosed(int RowOrdinal, CellKey From, int Origin, long Lower, long Upper) : ArenaTransform;
    /// <summary>Writes the normalized mean of a vector table's admitted rows.</summary>
    /// <param name="IntoRowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="IntoKey">The destination cell key.</param>
    /// <param name="FromRowOrdinal">The source table's catalog ordinal.</param>
    /// <param name="WhereRowOrdinal">The keyed Bool filter row's catalog ordinal, or <c>-1</c>.</param>
    public sealed record Mean(int IntoRowOrdinal, CellKey IntoKey, int FromRowOrdinal, int WhereRowOrdinal) : ArenaTransform;
    /// <summary>Writes one vector into a cell with its components unchanged.</summary>
    /// <param name="IntoRowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="IntoKey">The destination cell key.</param>
    /// <param name="From">The vector to write.</param>
    public sealed record Copy(int IntoRowOrdinal, CellKey IntoKey, VectorSource From) : ArenaTransform;
    /// <summary>Writes the normalized sum of one to eight weighted vector terms.</summary>
    /// <param name="IntoRowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="IntoKey">The destination cell key.</param>
    /// <param name="Terms">The weighted terms, in authored order.</param>
    public sealed record Mix(int IntoRowOrdinal, CellKey IntoKey, IReadOnlyList<ArenaMixTerm> Terms) : ArenaTransform;
    /// <summary>Ranks a vector table against a query and writes the closest keys, scores, or values.</summary>
    /// <param name="IntoRowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="FromRowOrdinal">The source table's catalog ordinal.</param>
    /// <param name="Query">The query vector.</param>
    /// <param name="K">How many results the ranking writes.</param>
    /// <param name="Threshold">The inclusive score cutoff, or <see langword="null"/>.</param>
    /// <param name="WhereRowOrdinal">The keyed Bool filter row's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="Exclude">The key excluded from the candidates, or the invalid default.</param>
    /// <param name="Farthest">Whether the ranking takes the farthest rather than the nearest.</param>
    public sealed record Nearest(int IntoRowOrdinal, int FromRowOrdinal, VectorSource Query, int K, long? Threshold, int WhereRowOrdinal, CellKey Exclude, bool Farthest) : ArenaTransform;
    /// <summary>Refreshes a knowledge row from its declared source, positions, and visibility mask.</summary>
    /// <param name="RowOrdinal">The knowledge row's catalog ordinal.</param>
    /// <param name="SourceRowOrdinal">The token property row, or the board used by a direct projection.</param>
    /// <param name="MaskRowOrdinal">The board whose non-zero cells are visible.</param>
    /// <param name="PositionsRowOrdinal">The token-keyed row mapping each token to its current board cell, or -1
    /// for a direct board projection.</param>
    public sealed record Observe(int RowOrdinal, int SourceRowOrdinal, int MaskRowOrdinal, int PositionsRowOrdinal) : ArenaTransform;
    /// <summary>Moves one bound live pool token and the matching selectively pushable outward run one topology cell.</summary>
    public sealed record PushRay(int PoolOrdinal, int CellFieldOrdinal, int ValueFieldOrdinal, int OriginBindingSlot, CompiledTopology Topology, int Direction, CompiledPattern Pattern, CompiledPattern PushPattern, CompiledPattern StopPattern, long Empty) : ArenaTransform;
    /// <summary>Stores a vector into a table unless a near duplicate is already there.</summary>
    /// <param name="IntoRowOrdinal">The destination table's catalog ordinal.</param>
    /// <param name="Key">The key the stored vector takes.</param>
    /// <param name="From">The source vector.</param>
    /// <param name="UnlessWithinQ16">The cosine ceiling in Q48.16, in [0, 1].</param>
    public sealed record Remember(int IntoRowOrdinal, CellName Key, VectorSource From, long UnlessWithinQ16) : ArenaTransform;
    /// <summary>Writes one value over the longest run a pattern accepts, walked outward from an origin.</summary>
    /// <param name="RowOrdinal">The board row's catalog ordinal.</param>
    /// <param name="Origin">The origin's topology cell ordinal, excluded from the read word and the write, or
    /// <c>-1</c> when a dynamic key supplies it.</param>
    /// <param name="Direction">The direction ordinal the ray steps in.</param>
    /// <param name="Pattern">The compiled pattern over the board's own raw values.</param>
    /// <param name="Value">The admitted value written to every cell of the accepted prefix.</param>
    public sealed record SetRay(int RowOrdinal, int Origin, int Direction, CompiledPattern Pattern, long Value) : ArenaTransform;
    /// <summary>Reorders a keyed row or zone by one Fisher-Yates pass over a stream-draw site.</summary>
    /// <param name="RowOrdinal">The reordered row's catalog ordinal.</param>
    /// <param name="DrawRowOrdinal">The integer stream-draw site supplying the samples.</param>
    public sealed record Shuffle(int RowOrdinal, int DrawRowOrdinal) : ArenaTransform;
    /// <summary>Reorders a keyed or ordered numeric row by its own cell values, stably.</summary>
    /// <param name="RowOrdinal">The keyed or ordered numeric row's catalog ordinal.</param>
    /// <param name="Descending">Whether the greatest value comes first.</param>
    public sealed record SortKeyed(int RowOrdinal, bool Descending) : ArenaTransform;
    /// <summary>Reorders an ordered zone by attribute rows over its token domain, stably.</summary>
    /// <param name="RowOrdinal">The ordered zone's catalog ordinal.</param>
    /// <param name="By">The attribute keys, in precedence order.</param>
    public sealed record SortZone(int RowOrdinal, IReadOnlyList<ArenaSortKey> By) : ArenaTransform;
    /// <summary>Moves selected tokens between ordered zones, preserving identity.</summary>
    /// <param name="FromRowOrdinal">The source zone's catalog ordinal, or <c>-1</c> when a live end supplies
    /// it.</param>
    /// <param name="ToRowOrdinal">The destination zone's catalog ordinal, or <c>-1</c> when a live end supplies
    /// it.</param>
    /// <param name="Selector">The source selector.</param>
    /// <param name="Key">The token key for key or slice selection; a dynamic key arrives in the binding
    /// instead.</param>
    /// <param name="InsertFirst">Whether the tokens enter the destination at its head.</param>
    /// <param name="DrawRowOrdinal">The stream-draw site for random selection, or <c>-1</c>.</param>
    /// <param name="Count">How many tokens move, each selected afresh from what remains.</param>
    public sealed record Transfer(int FromRowOrdinal, int ToRowOrdinal, ZoneSelector Selector, CellKey Key, bool InsertFirst, int DrawRowOrdinal, int Count) : ArenaTransform;
    /// <summary>Writes one value into every board cell of a cell set: the bits of a mask read from a cell, or the
    /// members of a declared cell set.</summary>
    /// <param name="RowOrdinal">The board row's catalog ordinal; over a topology of at most 64 cells when the set is a
    /// mask.</param>
    /// <param name="SetRowOrdinal">The integer row the mask is read from, or <c>-1</c> for a declared set.</param>
    /// <param name="SetKey">The cell of that row; a dynamic key arrives in the binding instead.</param>
    /// <param name="Value">The admitted value written to every member.</param>
    /// <param name="Set">The declared cell set read in place of a mask, or <see langword="null"/>.</param>
    public sealed record WriteSet(int RowOrdinal, int SetRowOrdinal, CellKey SetKey, long Value, CellSetRow? Set = null) : ArenaTransform;
}
/// <summary>The parts of an <see cref="ArenaTransform"/> a rule firing resolves fresh: the one dynamic key a
/// transform may carry, the two live zone ends a transfer may carry, and the live pool mover a pushRay
/// carries.</summary>
/// <remarks>A binding is a value, so a firing substitutes without rebuilding the transform. Each row travels as
/// its ordinal plus one, so the default carrier is <see cref="None"/> and substitutes nothing rather than
/// addressing row zero.</remarks>
public readonly record struct ArenaTransformBinding {
    private readonly int m_from;
    private readonly int m_to;

    /// <summary>Initializes a binding.</summary>
    /// <param name="bindsKey">Whether <paramref name="key"/> replaces the transform's own key.</param>
    /// <param name="key">The resolved key; the invalid default names no cell and refuses.</param>
    /// <param name="fromRowOrdinal">The resolved source row, or <c>-1</c> to keep the transform's own.</param>
    /// <param name="toRowOrdinal">The resolved destination row, or <c>-1</c> to keep the transform's own.</param>
    /// <param name="bindsInstance">Whether <paramref name="instance"/> supplies a live pool mover.</param>
    /// <param name="instance">The live pool mover.</param>
    public ArenaTransformBinding(bool bindsKey = false, CellKey key = default, int fromRowOrdinal = -1, int toRowOrdinal = -1, bool bindsInstance = false, StateInstanceHandle instance = default) {
        BindsKey = bindsKey;
        BindsInstance = bindsInstance;
        Instance = instance;
        Key = key;
        m_from = Math.Max(
            val1: 0,
            val2: (fromRowOrdinal + 1)
        );
        m_to = Math.Max(
            val1: 0,
            val2: (toRowOrdinal + 1)
        );
    }

    /// <summary>Gets the binding that substitutes nothing.</summary>
    public static ArenaTransformBinding None => default;
    /// <summary>Gets a value indicating whether <see cref="Key"/> replaces the transform's own key.</summary>
    public bool BindsKey { get; }
    /// <summary>Gets a value indicating whether <see cref="Instance"/> supplies a live pool mover.</summary>
    public bool BindsInstance { get; }
    /// <summary>Gets the live pool mover.</summary>
    public StateInstanceHandle Instance { get; }
    /// <summary>Gets the resolved source row's catalog ordinal, or <c>-1</c>.</summary>
    public int FromRowOrdinal => (m_from - 1);
    /// <summary>Gets the resolved key; the invalid default names no cell.</summary>
    public CellKey Key { get; }
    /// <summary>Gets the resolved destination row's catalog ordinal, or <c>-1</c>.</summary>
    public int ToRowOrdinal => (m_to - 1);

    /// <summary>Returns the key this binding substitutes, or the transform's own.</summary>
    /// <param name="own">The transform's own key.</param>
    /// <returns>The key to address with.</returns>
    public CellKey KeyOr(CellKey own) => (BindsKey
        ? Key
        : own
    );
    /// <summary>Returns the source row this binding substitutes, or the transform's own.</summary>
    /// <param name="own">The transform's own source row ordinal.</param>
    /// <returns>The row ordinal to read.</returns>
    public int FromOr(int own) => ((m_from > 0)
        ? (m_from - 1)
        : own
    );
    /// <summary>Returns the destination row this binding substitutes, or the transform's own.</summary>
    /// <param name="own">The transform's own destination row ordinal.</param>
    /// <returns>The row ordinal to write.</returns>
    public int ToOr(int own) => ((m_to > 0)
        ? (m_to - 1)
        : own
    );
}
