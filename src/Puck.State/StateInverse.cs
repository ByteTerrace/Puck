using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>
/// A <see cref="StateDomain.CellsOf"/> row's declared inverse: the board's cells are not authored directly but
/// derived from a keyed <see cref="Tokens"/> row naming cells of the same topology and a <see cref="Codes"/> row
/// keyed the same way, giving each token's code. Legitimate only on a <see cref="CellKind.Int"/> row whose
/// <see cref="StateRow.EffectiveDomain"/> is <see cref="StateDomain.CellsOf"/> and that carries no
/// <c>field</c> trait — a document project's validator enforces both.
/// </summary>
/// <remarks>
/// <para>The derived value of a board cell is the code of the LAST token — in <see cref="Tokens"/>'s own cell
/// order — whose value names that cell: two tokens naming the same cell is not an authoring error, it is the
/// invariant. A token whose value names no cell of the topology (outside <c>0..cellCount-1</c>) contributes
/// nothing, and a cell no token names reads the board's own declared empty value.</para>
/// <para>The board itself is never written directly: a document project's mutation door refuses an
/// <c>UpsertStateCell</c>/<c>RemoveStateCell</c>/<c>UpsertStateRow</c> that targets it by name, and its
/// whole-document validator refuses authored cells that do not match the derivation. <see cref="StateArena"/>
/// recomputes the board — from <see cref="Tokens"/>/<see cref="Codes"/>'s CURRENT values — on every keyed write to
/// either row and on every load, so the exported document already carries the derived cells and a hand-authored or
/// stale board never survives past the next tick.</para>
/// <para>Validation and arena construction also require the effective domain of <see cref="Codes"/> — its envelope
/// intersected with its optional enum — to be contained by the board's effective domain. The board's declared empty
/// value must be admitted by both its envelope and enum, so changing either row's bounds or symbols can refuse the
/// inverse declaration before any state is loaded.</para>
/// </remarks>
/// <param name="Tokens">The keyed <see cref="CellKind.Int"/> row whose cell values name cells of the board's
/// topology — a value naming no cell means that token is off the board.</param>
/// <param name="Codes">The row giving each token's code, keyed the same as <see cref="Tokens"/> — the same cell
/// keys, in the same order, so index <c>i</c> of one names the same token as index <c>i</c> of the other.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateInverse(CellName Tokens, CellName Codes);
