using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>What an observer learns about a row's cells it may not read.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<HiddenCells>))]
public enum HiddenCells : byte {
    /// <summary>Hidden cells leave no trace: neither their count nor their positions.</summary>
    Omit,
    /// <summary>The observed row reports how many cells were hidden and nothing else about them.</summary>
    Count,
    /// <summary>Every hidden cell appears in pile order as an anonymous placeholder (a card back): no key, no
    /// value, no text, no observation stamp.</summary>
    Placeholder,
}

/// <summary>Opt-in observation policy. Null readers means public; an empty list means authority only.
/// Row and cell policies intersect. Replica-tier authorities remain fully trusted.</summary>
/// <param name="Readers">Canonical authenticated principal tokens; no seat or peer identity comes from the request payload.</param>
/// <param name="Hidden">What an observer who may read the row learns about the cells it may not.</param>
/// <param name="ReadersFrom">A keyed text row whose cell texts are principal tokens admitted beside
/// <paramref name="Readers"/>: the live audience a rule widens by writing a token (a showdown reveals a hand) or
/// narrows by clearing one. Either list alone, or both, keeps the row private; neither makes it public.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateVisibility(IReadOnlyList<string>? Readers = null, HiddenCells Hidden = HiddenCells.Omit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReadersFrom = null) {
    /// <summary>Gets a value indicating whether the policy admits the public observer: no reader list of either kind.</summary>
    public bool IsPublic => Readers is null && ReadersFrom is null;

    /// <summary>Whether this observation policy admits the recipient named by its canonical token, or the public
    /// observer when the token is null.</summary>
    public bool Allows(string? recipient) {
        if (IsPublic) {
            return true;
        }
        if (recipient is null || Readers is null) {
            return false;
        }
        for (var index = 0; index < Readers.Count; index++) {
            if (string.Equals(Readers[index], recipient, StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the policy admits the recipient through its static list or the live text row.</summary>
    /// <param name="recipient">The canonical token, or null for the public observer.</param>
    /// <param name="rows">The state rows the live reader row is read from.</param>
    public bool Allows(string? recipient, IReadOnlyList<StateRow>? rows) {
        if (Allows(recipient)) {
            return true;
        }
        if (recipient is null || ReadersFrom is null || StateRows.FindStateRow(rows, ReadersFrom) is not { Cells: { } cells }) {
            return false;
        }
        for (var index = 0; index < cells.Count; index++) {
            if (string.Equals(cells[index].Text, recipient, StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
    }
}

/// <summary>A persisted knowledge layer refreshed explicitly by the authority.</summary>
/// <param name="Source">The integer/boolean board observed.</param>
/// <param name="Mask">A boolean board over the same topology; true cells are currently observed.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateKnowledge(string Source, string Mask);

/// <summary>When a stored knowledge value was last seen and whether the latest observation still sees it.</summary>
/// <param name="Tick">The last observation tick.</param>
/// <param name="Visible">Whether the latest explicit refresh sees this cell.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateObservation(long Tick, bool Visible);
