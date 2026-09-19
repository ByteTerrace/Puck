using System.Collections;
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
    public bool IsPublic => ((Readers is null) && (ReadersFrom is null));

    /// <summary>Whether this observation policy admits the recipient named by its canonical token, or the public
    /// observer when the token is null.</summary>
    public bool Allows(string? recipient) {
        if (IsPublic) {
            return true;
        }
        if (
            (recipient is null) ||
            (Readers is null)
        ) {
            return false;
        }
        for (var index = 0; (index < Readers.Count); index++) {
            if (string.Equals(
                a: Readers[index],
                b: recipient,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }
        return false;
    }
    /// <summary>Whether the policy admits the recipient through its static list or the live text row.</summary>
    /// <param name="recipient">The canonical token, or null for the public observer.</param>
    /// <param name="rows">The state rows the live reader row is read from.</param>
    public bool Allows(string? recipient, IReadOnlyList<StateRow>? rows) {
        if (Allows(recipient: recipient)) {
            return true;
        }
        if (
            (recipient is null) ||
            (ReadersFrom is null) ||
            (StateRows.FindStateRow(
            rows,
            ReadersFrom
        ) is not { Cells: { } cells })
        ) {
            return false;
        }
        for (var index = 0; (index < cells.Count); index++) {
            // A reader row of another kind names no recipient, so it admits no one rather than faulting a disclosure.
            if (
                (cells[index].Value.Kind == CellKind.Text) &&
                string.Equals(
                    a: cells[index].Value.AsText,
                    b: recipient,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                return true;
            }
        }
        return false;
    }
}

// The arena owns the collection it retains. Recognizing this marker makes a visibility already admitted by an
// arena reusable without another copy, while arbitrary IReadOnlyList implementations are copied at the boundary.
internal sealed class ArenaVisibilityReaders(string[] items) : IReadOnlyList<string> {
    public int Count => items.Length;
    public string this[int index] => items[index];
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
}

internal static class StateVisibilityStorage {
    private const long ObjectBytes = 32L;
    private const long VisibilityBytes = 64L;

    internal static bool TryMeasure(StateVisibility? value, out long bytes, out string reason) => TryNormalize(
        bytes: out bytes,
        copy: false,
        normalized: out _,
        reason: out reason,
        value: value
    );
    internal static bool TryNormalize(StateVisibility? value, out StateVisibility? normalized, out long bytes, out string reason) => TryNormalize(
        bytes: out bytes,
        copy: true,
        normalized: out normalized,
        reason: out reason,
        value: value
    );
    private static bool TryNormalize(StateVisibility? value, bool copy, out StateVisibility? normalized, out long bytes, out string reason) {
        normalized = value;
        bytes = 0L;
        reason = string.Empty;

        if (value is null) {
            return true;
        }
        if ((value.ReadersFrom?.Length ?? 0) > SafeName.MaxLength) {
            reason = $"visibility readersFrom is {value.ReadersFrom!.Length} characters, past the {SafeName.MaxLength}-character limit";
            return false;
        }

        var readers = value.Readers;

        if (readers is not null) {
            if (readers.Count > StateCapacity.MaxVisibilityReaders) {
                reason = $"visibility carries {readers.Count} readers, past the {StateCapacity.MaxVisibilityReaders}-reader limit";
                return false;
            }

            for (var index = 0; index < readers.Count; index++) {
                var reader = readers[index];

                if (reader is null) {
                    reason = $"visibility reader {index} is null";
                    return false;
                }
                if (reader.Length > StateCapacity.MaxVisibilityReaderLength) {
                    reason = $"visibility reader {index} is {reader.Length} characters, past the {StateCapacity.MaxVisibilityReaderLength}-character limit";
                    return false;
                }
            }

            if (copy && (readers is not ArenaVisibilityReaders)) {
                var copied = new string[readers.Count];

                for (var index = 0; index < copied.Length; index++) {
                    copied[index] = readers[index];
                }

                normalized = value with { Readers = new ArenaVisibilityReaders(items: copied) };
            }
        }

        bytes = RetainedBytes(value: normalized!);
        return true;
    }

    internal static long RetainedBytes(StateVisibility? value) {
        if (value is null) {
            return 0L;
        }

        var bytes = VisibilityBytes;

        if (value.ReadersFrom is { } readersFrom) {
            bytes += StringBytes(text: readersFrom);
        }
        if (value.Readers is { } readers) {
            // The normalized representation owns one wrapper and one tightly sized reference array.
            bytes += (2L * ObjectBytes) + (((long)readers.Count) * sizeof(long));

            for (var index = 0; index < readers.Count; index++) {
                bytes += StringBytes(text: readers[index]);
            }
        }

        return bytes;
    }

    private static long StringBytes(string text) => (ObjectBytes + (2L * text.Length));
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
