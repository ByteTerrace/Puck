using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// A state row's federated import: the row holds another world's published value, the value of a row that world
/// discloses to this one, and only that world writes it. The world it follows is a global destination this document
/// declares; its authority observes that world as a session and adopts the delivered value at a tick boundary, as an
/// ordinary state write its tape records, whenever this row no longer reads what the followed row reads. One value
/// therefore has one writer, the authority that owns it, wherever it is read: the moon's day is the debug world's day,
/// a lever's pull in one world is a fact the other world's rules read, and neither world reaches into the other's
/// state.
/// </summary>
/// <remarks>
/// <para>Between adoptions the row moves by its own trait, so a followed row that advances (a day) authors the advance
/// its leader authors and the two read alike every tick; the authority re-adopts when they drift apart by more than one
/// tick's advance, and on every write the leader takes. A row the leader holds still is adopted exactly.</para>
/// <para>Every outside write of a followed row is refused by name (<see cref="RefuseWrite"/>): a console, a seat, an
/// addon or a submitted operation writes the leader instead. A rule writing it is not refused yet; the next adoption
/// restores the leader's value.</para>
/// <para>A followed row is a slot row of kind <c>Fixed</c>, <c>Int</c> or <c>Bool</c>; the leader's row must be the same
/// kind, and a delivered row of another kind, or one the leader does not disclose, is not adopted (the authority
/// narrates it once).</para>
/// </remarks>
/// <param name="Destination">The global destination whose world this row follows.</param>
/// <param name="Row">The row of that world this row holds. Absent is this row's own name.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateFollow(
    [property: DocumentText("a destination name")] string Destination,
    [property: DocumentText("a state row of the followed world")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Row = null
) {
    /// <summary>Returns the followed world's row a row holds: <see cref="Row"/>, or the row's own name.</summary>
    /// <param name="row">The following row's own name.</param>
    /// <returns>The leader's row name.</returns>
    public string LeaderRow(string row) => (Row ?? row);

    /// <summary>Returns the refusal every outside write of a followed row answers.</summary>
    /// <param name="row">The followed row.</param>
    /// <returns>The refusal text.</returns>
    public static string RefuseWrite(WorldStateRow row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var follow = row.Follows!;

        return $"state row '{row.Name.Value}' follows row '{follow.LeaderRow(row: row.Name.Value)}' of destination '{follow.Destination}' — write it there; its authority owns it";
    }
}
