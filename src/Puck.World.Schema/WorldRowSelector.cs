using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A per-body row selector inside a <see cref="WorldRowAssignment"/>: each body's own cell of a keyed
/// <c>state.world</c> row (the cell keyed by the body's 0-based index, the way <c>bodies.scaleRow</c> is) picks the
/// row the case for its value names. A body with no cell, or a value no case names, keeps the row the assignment's
/// sequence gives it. The cell is simulation state, so the choice follows it deterministically: a rule or a console
/// write moves the row, a replay moves it at the same tick, and a saved world restores it with the cell. The look
/// assignment's selector changes only what a body wears; the kit assignment's selector changes how it moves and what
/// it collides as, so a collider change is its own authored mapping and a look never changes a kit.</summary>
/// <param name="State">The keyed <c>state.world</c> row of kind Int or Bool whose cells are keyed by body index.</param>
/// <param name="Cases">The value-to-row mapping, each value at most once.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRowSelector(string State, IReadOnlyList<WorldRowSelectorCase> Cases);
/// <summary>One case of a <see cref="WorldRowSelector"/>: the row a body's cell value selects.</summary>
/// <param name="Value">The cell value: an Int cell's integer, or 0 and 1 for a Bool cell.</param>
/// <param name="Row">The kit or look row name the value selects.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRowSelectorCase(long Value, string Row);
