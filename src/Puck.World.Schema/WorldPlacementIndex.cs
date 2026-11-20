using Puck.Abstractions;

namespace Puck.World;

/// <summary>
/// The one derived index of a placement list, built once per list instance and shared by every reader that would
/// otherwise scan the whole list: the rows by id, the ordinals of the rows carrying each per-tick trait (a response, a
/// deal, a returning board, an engageable tabletop), each parent's children, and each board topology's anchor.
/// </summary>
/// <remarks>
/// <para>The duplicate rule is decided by <see cref="WorldRowKeys.Placements"/>: the first row of an id wins, as
/// <see cref="WorldDefinitionRows.FindPlacement"/> finds it. Whole-document validation refuses a second row of an id
/// (<c>RequireUniqueName</c> over <c>placements[].id</c>), so a duplicate is only ever read before that refusal is
/// reported, and a reader sees the row the validator names first. A null row or a null id is skipped, since
/// validation has not yet refused it when the validator itself reads the index.</para>
/// <para>The invariant is that a list instance is never changed in place once indexed: a document's rows are replaced
/// wholesale (a new list per edit), so the list instance is the index's whole identity, a list that grew is indexed
/// again, and a code update drops every held index (<see cref="CodeDerivedTable{TKey, TValue}"/>). Nothing enforces
/// that an arbitrary <see cref="IReadOnlyList{T}"/> stays unchanged: a caller that replaces a row of a list it has
/// already passed here reads a stale index.</para>
/// </remarks>
public sealed class WorldPlacementIndex {
    private static readonly CodeDerivedTable<IReadOnlyList<WorldPlacement>, WorldPlacementIndex> Indices = new();

    private readonly int[] m_responders;
    private readonly int[] m_dealers;
    private readonly int[] m_returnBoards;
    private readonly int[] m_tabletops;
    private readonly Dictionary<string, int[]> m_children;
    private readonly Dictionary<string, WorldPlacement> m_boards;

    private WorldPlacementIndex(IReadOnlyList<WorldPlacement> placements) {
        RowCount = placements.Count;
        Rows = WorldRowKeys.Placements.IndexOf(rows: placements);

        List<int>? responders = null, dealers = null, returnBoards = null, tabletops = null;
        Dictionary<string, List<int>>? children = null;

        m_boards = new Dictionary<string, WorldPlacement>(comparer: StringComparer.Ordinal);

        for (var ordinal = 0; (ordinal < placements.Count); ordinal++) {
            if (placements[ordinal] is not { } row) { continue; }
            if (row.Respond is { Count: > 0 }) { (responders ??= []).Add(item: ordinal); }
            if (row.Deal is not null) { (dealers ??= []).Add(item: ordinal); }
            if (row.Board is { } board) {
                if (board.Enforcement == WorldBoardEnforcement.Return) { (returnBoards ??= []).Add(item: ordinal); }
                if (board.Engage is not null) { (tabletops ??= []).Add(item: ordinal); }
                // Every real placement carries at most one board facet naming a topology (validated:
                // WorldDefinitionValidator.Board.cs refuses a second), so the first is the only one there can be.
                if (board.Topology is { } topology) { _ = m_boards.TryAdd(key: topology, value: row); }
            }
            if (row.Parent is { } parent) {
                children ??= new Dictionary<string, List<int>>(comparer: StringComparer.Ordinal);
                if (!children.TryGetValue(key: parent, value: out var siblings)) { children.Add(key: parent, value: siblings = []); }
                siblings.Add(item: ordinal);
            }
        }

        m_responders = (responders?.ToArray() ?? []);
        m_dealers = (dealers?.ToArray() ?? []);
        m_returnBoards = (returnBoards?.ToArray() ?? []);
        m_tabletops = (tabletops?.ToArray() ?? []);
        m_children = new Dictionary<string, int[]>(capacity: (children?.Count ?? 0), comparer: StringComparer.Ordinal);

        foreach (var (parent, ordinals) in (children ?? [])) { m_children.Add(key: parent, value: ordinals.ToArray()); }
    }

    /// <summary>Gets the rows by id, the first row of an id winning.</summary>
    public WorldRowIndex<WorldPlacement, string> Rows { get; }
    /// <summary>Gets the ordinals of the rows carrying a non-empty <see cref="WorldPlacement.Respond"/>, in list order.</summary>
    public ReadOnlySpan<int> Responders => m_responders;
    /// <summary>Gets the ordinals of the dealt templates (rows carrying a <see cref="WorldPlacement.Deal"/>), in list
    /// order.</summary>
    public ReadOnlySpan<int> Dealers => m_dealers;
    /// <summary>Gets the ordinals of the rows whose board facet returns a refused move
    /// (<see cref="WorldBoardEnforcement.Return"/>), in list order.</summary>
    public ReadOnlySpan<int> ReturnBoards => m_returnBoards;
    /// <summary>Gets the ordinals of the rows whose board facet a seat engages (<see cref="WorldPlacementBoard.Engage"/>),
    /// in list order.</summary>
    public ReadOnlySpan<int> Tabletops => m_tabletops;
    /// <summary>Gets the number of rows the list held when it was indexed.</summary>
    public int RowCount { get; }

    /// <summary>Gets a placement list's index, derived once per list instance and again should the list have
    /// grown.</summary>
    /// <param name="placements">The placement list.</param>
    /// <returns>The list's index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placements"/> is <see langword="null"/>.</exception>
    public static WorldPlacementIndex Of(IReadOnlyList<WorldPlacement> placements) {
        ArgumentNullException.ThrowIfNull(argument: placements);

        return Indices.GetValue(
            derive: static (list, _) => new WorldPlacementIndex(placements: list),
            holds: static (held, list, _) => (held.RowCount == list.Count),
            key: placements,
            state: 0
        );
    }
    /// <summary>Gets a placement list's rows by id, the first row of an id winning.</summary>
    /// <param name="placements">The placement list.</param>
    /// <returns>The rows by id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placements"/> is <see langword="null"/>.</exception>
    public static WorldRowIndex<WorldPlacement, string> ById(IReadOnlyList<WorldPlacement> placements) => WorldRowKeys.Placements.IndexOf(rows: placements);
    /// <summary>Gets the ordinals of the rows naming a parent, in list order.</summary>
    /// <param name="parentId">The parent's id.</param>
    /// <returns>The children's ordinals; empty when none names the parent.</returns>
    public ReadOnlySpan<int> ChildrenOf(string parentId) => (m_children.TryGetValue(key: parentId, value: out var ordinals) ? ordinals : []);
    /// <summary>Gets the row whose board facet names a topology.</summary>
    /// <param name="topology">The topology's name.</param>
    /// <returns>The anchoring row, or <see langword="null"/> when no row's board names the topology.</returns>
    public WorldPlacement? BoardOf(string topology) => m_boards.GetValueOrDefault(key: topology);
}
