using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The <c>kits</c> section — the dealt row family's one shape: the declared rows (the deck) and the
/// assignment policy that deals them to entities (the deal). A row family with a deal is always spelled this way;
/// a family without one simply omits <see cref="Assignment"/>.</summary>
/// <param name="Rows">The declared kits, in order.</param>
/// <param name="Assignment">The kit→entity assignment policy — ABSENT resolves to
/// <see cref="WorldRowAssignment.Default"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldKitsSection(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldKit>? Rows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRowAssignment? Assignment = null
);
/// <summary>The <c>looks</c> section — the appearance deck and its deal, the same dealt-row shape as
/// <see cref="WorldKitsSection"/>.</summary>
/// <param name="Rows">The declared looks, in order.</param>
/// <param name="Assignment">The look→entity assignment policy — ABSENT resolves to
/// <see cref="WorldRowAssignment.Default"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldLooksSection(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldLook>? Rows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRowAssignment? Assignment = null
);
/// <summary>The <c>placements</c> section — the placed prototype instances and the policy governing how they may
/// grow and move live (the budget the render envelope reserves, the scale envelope, candidate picking, preview
/// deadline, and the derived face-screen reservation).</summary>
/// <param name="Rows">The placements, in order.</param>
/// <param name="Policy">The live-placement policy — ABSENT resolves to
/// <see cref="WorldAuthoringDefaults.Absent"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementsSection(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPlacement>? Rows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldAuthoringDefaults? Policy = null
);
