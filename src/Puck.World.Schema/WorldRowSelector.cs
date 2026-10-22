using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A per-body row selector inside a <see cref="WorldRowAssignment"/>: each body's own value of a
/// <c>state.body</c> counter picks the row the case for that value names. A body whose register holds a value no case
/// names keeps the row the assignment's sequence gives it. The register is body state, so the choice is the body's and
/// goes where the body goes: a kit action or a rule's <c>setBodyState</c> moves the row, a replay moves it at the same
/// tick, a checkpoint restores it with the register, and a crossing to another world carries the register with the body
/// (a destination declaring the same register under the same kind selects by the carried value). The look
/// assignment's selector changes only what a body wears; the kit assignment's selector changes how it moves and what it
/// collides as, so a collider change is its own authored mapping and a look never changes a kit.</summary>
/// <param name="BodyState">The <c>state.body</c> counter whose value selects.</param>
/// <param name="Cases">The value-to-row mapping, each value at most once.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRowSelector(string BodyState, IReadOnlyList<WorldRowSelectorCase> Cases);
/// <summary>One case of a <see cref="WorldRowSelector"/>: the row a body's register value selects.</summary>
/// <param name="Value">The register value, a whole number; a counter holding a fraction matches no case.</param>
/// <param name="Row">The kit or look row name the value selects.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRowSelectorCase(long Value, string Row);
