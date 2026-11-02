using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World.Server;

/// <summary>One door at which a recipient was told something different in two worlds that differ only in hidden
/// state: a leak.</summary>
/// <param name="Principal">The recipient's label.</param>
/// <param name="Door">The door that answered differently: a console verb, a delivery kind, a query or an outcome.</param>
/// <param name="Tick">The first tick at which the door's answers part.</param>
/// <param name="Index">The ordinal of the first differing answer among the door's answers to this recipient.</param>
/// <param name="Left">What the first world answered there, or <c>(nothing)</c> when it answered nothing more.</param>
/// <param name="Right">What the second world answered there, or <c>(nothing)</c>.</param>
/// <param name="Where">Where the two answers first differ: a character offset, or a JSON path for a document.</param>
public sealed record WorldDisclosureLeak(string Principal, string Door, ulong Tick, int Index, string Left, string Right, string Where) {
    /// <summary>Returns the leak as one report line naming the door, the tick and the two differing answers.</summary>
    /// <returns>The line.</returns>
    public string Describe() => string.Create(provider: CultureInfo.InvariantCulture,
        handler: $"{Door} to {Principal} at tick {Tick} (answer {Index}, {Where}): {Left} | against | {Right}");
}

/// <summary>
/// Compares what one recipient received in two worlds identical but for hidden state. Each door is compared on its own
/// (the answers one principal received through one verb, delivery kind, query or outcome, in order), so the report
/// names every door that differs and the first tick at which it does, rather than only the first difference overall.
/// A door that answered more often in one world, or at another tick, differs as surely as one that answered other
/// bytes: count, timing and size per tick are part of what a recipient receives.
/// </summary>
public static class WorldDisclosureComparison {
    private const string Nothing = "(nothing)";
    private const int Context = 48;

    /// <summary>Compares two transcripts door by door.</summary>
    /// <param name="left">The first world's transcript.</param>
    /// <param name="right">The second world's transcript.</param>
    /// <returns>One leak per door whose answers differ, in the order the doors first answered; empty when the
    /// recipient cannot tell the worlds apart.</returns>
    public static IReadOnlyList<WorldDisclosureLeak> Compare(WorldDisclosureTranscript left, WorldDisclosureTranscript right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);
        var leftDoors = Doors(entries: left.Entries);
        var rightDoors = Doors(entries: right.Entries);
        var leftBlobs = left.Blobs;
        var rightBlobs = right.Blobs;
        var leaks = new List<WorldDisclosureLeak>();

        foreach (var key in leftDoors.Keys.Concat(second: rightDoors.Keys).Distinct()) {
            var first = (leftDoors.TryGetValue(key: key, value: out var a) ? a : []);
            var second = (rightDoors.TryGetValue(key: key, value: out var b) ? b : []);

            for (var index = 0; (index < Math.Max(val1: first.Count, val2: second.Count)); index++) {
                var one = ((index < first.Count) ? first[index] : null);
                var other = ((index < second.Count) ? second[index] : null);

                if ((one is not null) && (other is not null) && (one.Tick == other.Tick) && (one.Bytes == other.Bytes)) {
                    continue;
                }
                leaks.Add(item: Leak(index: index, key: key, left: one, leftBlobs: leftBlobs, right: other, rightBlobs: rightBlobs));
                break;
            }
        }
        return leaks;
    }

    // Each (principal, door)'s answers in order, the doors in the order they first answered.
    private static Dictionary<(string Principal, string Door), List<WorldDisclosureEntry>> Doors(IReadOnlyList<WorldDisclosureEntry> entries) {
        var doors = new Dictionary<(string Principal, string Door), List<WorldDisclosureEntry>>();

        foreach (var entry in entries) {
            if (!doors.TryGetValue(key: (entry.Principal, entry.Door), value: out var answers)) {
                doors[(entry.Principal, entry.Door)] = answers = [];
            }
            answers.Add(item: entry);
        }
        return doors;
    }
    private static WorldDisclosureLeak Leak((string Principal, string Door) key, int index, WorldDisclosureEntry? left, WorldDisclosureEntry? right,
        IReadOnlyDictionary<string, string> leftBlobs, IReadOnlyDictionary<string, string> rightBlobs) {
        var tick = Math.Min(val1: (left?.Tick ?? ulong.MaxValue), val2: (right?.Tick ?? ulong.MaxValue));

        if ((left is null) || (right is null)) {
            return new WorldDisclosureLeak(Door: key.Door, Index: index, Left: Excerpt(entry: left, offset: 0), Principal: key.Principal,
                Right: Excerpt(entry: right, offset: 0), Tick: tick, Where: "one world answered and the other did not");
        }
        if (left.Tick != right.Tick) {
            return new WorldDisclosureLeak(Door: key.Door, Index: index, Left: $"tick {left.Tick}: {Excerpt(entry: left, offset: 0)}", Principal: key.Principal,
                Right: $"tick {right.Tick}: {Excerpt(entry: right, offset: 0)}", Tick: tick, Where: "answered at different ticks");
        }
        var offset = 0;

        while ((offset < left.Bytes.Length) && (offset < right.Bytes.Length) && (left.Bytes[offset] == right.Bytes[offset])) { offset++; }
        var where = string.Create(provider: CultureInfo.InvariantCulture, handler: $"byte {offset} of {left.Bytes.Length} against {right.Bytes.Length}");

        if ((Reference(bytes: left.Bytes, offset: offset) is { } leftReference) && (Reference(bytes: right.Bytes, offset: offset) is { } rightReference) &&
            leftBlobs.TryGetValue(key: leftReference, value: out var leftBlob) && rightBlobs.TryGetValue(key: rightReference, value: out var rightBlob)) {
            where += $"; document {Document(left: leftBlob, right: rightBlob)}";
        }
        return new WorldDisclosureLeak(Door: key.Door, Index: index, Left: Excerpt(entry: left, offset: offset), Principal: key.Principal,
            Right: Excerpt(entry: right, offset: offset), Tick: tick, Where: where);
    }
    // The blob reference the differing offset falls inside, if any.
    private static string? Reference(string bytes, int offset) {
        var start = bytes.LastIndexOf(value: WorldDisclosureTranscript.BlobPrefix, startIndex: Math.Min(val1: offset, val2: (bytes.Length - 1)), comparisonType: StringComparison.Ordinal);

        if (start < 0) { return null; }
        var end = bytes.IndexOf(value: ' ', startIndex: start);

        end = ((end < 0) ? bytes.Length : bytes.IndexOf(value: ' ', startIndex: (end + 1)));
        return bytes[start..((end < 0) ? bytes.Length : end)];
    }
    private static string Document(string left, string right) {
        try {
            return WorldJsonDifference.First(left: JsonNode.Parse(json: left), right: JsonNode.Parse(json: right));
        } catch (JsonException) {
            return "differs (not JSON)";
        }
    }
    private static string Excerpt(WorldDisclosureEntry? entry, int offset) {
        if (entry is null) { return Nothing; }
        var text = entry.Bytes;
        var start = Math.Max(val1: 0, val2: (offset - Context));
        var end = Math.Min(val1: text.Length, val2: (offset + (Context * 2)));

        return $"\"{((start > 0) ? "…" : string.Empty)}{text[start..end]}{((end < text.Length) ? "…" : string.Empty)}\"";
    }
}
