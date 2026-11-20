using Puck.Abstractions;

namespace Puck.World;

/// <summary>
/// The one by-id index of a placement list, derived once per list instance and shared by every reader that resolves a
/// placement by its id (frame compilation, spatial compilation, stamp compilation, the delivery comparison, the
/// recipient's disclosure and whole-document validation), instead of each rebuilding a dictionary the size of the world
/// per call.
/// </summary>
/// <remarks>
/// <para>The duplicate rule is decided here and nowhere else: the first row of an id wins, exactly as
/// <see cref="WorldDefinitionRows.FindPlacement"/>'s linear scan does. Whole-document validation refuses a second row
/// of an id (<c>RequireUniqueName</c> over <c>placements[].id</c>), so a duplicate is only ever read before that refusal
/// is reported, and a reader sees the row the validator names first. A null row or a null id is skipped, since
/// validation has not yet refused it when the validator itself reads the index.</para>
/// <para>The invariant is that a list instance is never mutated once indexed: a document's rows are replaced
/// wholesale (a new list per edit), never changed in place, so the list instance is the index's whole identity and a
/// code update drops every held index (<see cref="CodeDerivedTable{TKey, TValue}"/>). Nothing enforces immutability of
/// an arbitrary <see cref="IReadOnlyList{T}"/>: a caller that edits a list it has already passed here reads a stale
/// index.</para>
/// </remarks>
public static class WorldPlacementIndex {
    private static readonly CodeDerivedTable<IReadOnlyList<WorldPlacement>, Dictionary<string, WorldPlacement>> ByIdTable = new();

    /// <summary>Gets a placement list's rows by id, derived once per list instance.</summary>
    /// <param name="placements">The placement list.</param>
    /// <returns>The rows by id, the first row of an id winning.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placements"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<string, WorldPlacement> ById(IReadOnlyList<WorldPlacement> placements) =>
        ByIdTable.GetValue(derive: static rows => {
            var byId = new Dictionary<string, WorldPlacement>(capacity: rows.Count, comparer: StringComparer.Ordinal);

            for (var index = 0; (index < rows.Count); index++) {
                if (rows[index] is { Id: not null } row) { _ = byId.TryAdd(key: row.Id, value: row); }
            }
            return byId;
        }, key: placements);
}
