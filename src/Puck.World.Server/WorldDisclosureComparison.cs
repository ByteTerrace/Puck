using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One door whose own answers to a recipient differ between two worlds: a reporting aid naming where a
/// recipient's streams part.</summary>
/// <param name="Door">The door.</param>
/// <param name="Index">The ordinal of the first differing answer among the door's answers to the recipient.</param>
/// <param name="Tick">The first tick at which the door's answers part.</param>
public sealed record WorldDisclosureDoorDifference(string Door, int Index, ulong Tick);

/// <summary>A recipient told two worlds that differ only in hidden state apart: the first place its ordered streams of
/// received events part, and every door whose own answers differ.</summary>
/// <param name="Principal">The recipient's label.</param>
/// <param name="Index">The ordinal of the first differing event in the recipient's stream.</param>
/// <param name="Tick">The tick of that event (the earlier of the two).</param>
/// <param name="Left">The first world's event there, rendered, or <c>(nothing)</c> when its stream had ended.</param>
/// <param name="Right">The second world's event there, rendered, or <c>(nothing)</c>.</param>
/// <param name="Where">How the two events differ: the door, the tick, or the first differing byte (and a JSON path for
/// a document).</param>
/// <param name="Doors">Each door whose own answers differ, in the order its first difference falls; empty when every
/// door answered alike and only the order across doors differs.</param>
public sealed record WorldDisclosureLeak(string Principal, int Index, ulong Tick, string Left, string Right, string Where, IReadOnlyList<WorldDisclosureDoorDifference> Doors) {
    /// <summary>Returns the leak as report lines: the first divergence, then each differing door.</summary>
    /// <returns>The lines.</returns>
    public IEnumerable<string> Describe() {
        yield return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Principal}: event {Index} at tick {Tick} ({Where}): {Left} | against | {Right}");
        if (Doors.Count == 0) {
            yield return "  every door answered alike; the order across doors differs";
            yield break;
        }
        foreach (var door in Doors) {
            yield return string.Create(provider: CultureInfo.InvariantCulture, handler: $"  door {door.Door}: answer {door.Index} at tick {door.Tick}");
        }
    }
}

/// <summary>
/// Compares what each recipient received in two worlds identical but for hidden state. A recipient's events are one
/// ordered stream across every door, and the streams are compared whole: an event received at another tick, in
/// another order relative to any other door, with another byte, or not at all, differs as surely as another answer.
/// Grouping by door is only the report's aid, naming every door whose own answers part once the streams have.
/// </summary>
public static class WorldDisclosureComparison {
    private const string Nothing = "(nothing)";
    private const int Context = 48;

    /// <summary>Compares two transcripts recipient by recipient.</summary>
    /// <param name="left">The first world's transcript.</param>
    /// <param name="right">The second world's transcript.</param>
    /// <returns>One leak per recipient whose streams differ, in the order the recipients first received anything;
    /// empty when no recipient can tell the worlds apart.</returns>
    public static IReadOnlyList<WorldDisclosureLeak> Compare(WorldDisclosureTranscript left, WorldDisclosureTranscript right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);
        var leftStreams = Streams(events: left.Events);
        var rightStreams = Streams(events: right.Events);
        var leaks = new List<WorldDisclosureLeak>();

        foreach (var principal in leftStreams.Keys.Concat(second: rightStreams.Keys).Distinct(comparer: StringComparer.Ordinal)) {
            var first = (leftStreams.TryGetValue(key: principal, value: out var a) ? a : []);
            var second = (rightStreams.TryGetValue(key: principal, value: out var b) ? b : []);

            if (FirstDifference(left: first, right: second) is not { } index) { continue; }
            var one = ((index < first.Count) ? first[index] : null);
            var other = ((index < second.Count) ? second[index] : null);

            leaks.Add(item: new WorldDisclosureLeak(Doors: Doors(left: first, right: second), Index: index, Left: Excerpt(entry: one, offset: Offset(left: one, right: other)),
                Principal: principal, Right: Excerpt(entry: other, offset: Offset(left: one, right: other)),
                Tick: Math.Min(val1: (one?.Tick ?? ulong.MaxValue), val2: (other?.Tick ?? ulong.MaxValue)), Where: Where(left: one, right: other)));
        }
        return leaks;
    }
    /// <summary>Renders one event's payload for a report: text as received, a snapshot or an outcome decoded field by
    /// field, any other record in hexadecimal.</summary>
    /// <param name="entry">The event.</param>
    /// <returns>The rendering.</returns>
    public static string Render(WorldDisclosureEvent entry) {
        ArgumentNullException.ThrowIfNull(argument: entry);
        if (entry.Text) { return Encoding.UTF8.GetString(bytes: entry.Payload); }
        if ((entry.Door == WorldDisclosureSession.SnapshotDoor) && WorldFederationCodec.TryDecodeSnapshot(body: entry.Payload, failure: out _, snapshot: out var snapshot)) {
            try {
                return RenderSnapshot(snapshot: in snapshot);
            } catch (InvalidOperationException) {
                return Convert.ToHexStringLower(inArray: entry.Payload);
            }
        }
        if (entry.Door.StartsWith(value: WorldDisclosureSession.OutcomeDoor, comparisonType: StringComparison.Ordinal) && (entry.Payload.Length > 0) &&
            WorldPeerWireFormat.TryReadResult(body: entry.Payload.AsSpan(start: 1), kind: (WorldPeerWireFormat.DownstreamKind)entry.Payload[0], reason: out _, result: out var result)) {
            return RenderResult(result: result);
        }
        return Convert.ToHexStringLower(inArray: entry.Payload);
    }

    // Each recipient's events in the order received, the recipients in the order they first received anything.
    private static Dictionary<string, List<WorldDisclosureEvent>> Streams(IReadOnlyList<WorldDisclosureEvent> events) {
        var streams = new Dictionary<string, List<WorldDisclosureEvent>>(comparer: StringComparer.Ordinal);

        foreach (var entry in events.OrderBy(keySelector: static entry => entry.Sequence)) {
            if (!streams.TryGetValue(key: entry.Principal, value: out var stream)) { streams[entry.Principal] = stream = []; }
            stream.Add(item: entry);
        }
        return streams;
    }
    private static int? FirstDifference(List<WorldDisclosureEvent> left, List<WorldDisclosureEvent> right) {
        for (var index = 0; (index < Math.Max(val1: left.Count, val2: right.Count)); index++) {
            if ((index >= left.Count) || (index >= right.Count) || !left[index].ReceivedAs(other: right[index])) { return index; }
        }
        return null;
    }
    // The report's aid: each door's own answers compared, in the order their first differences fall.
    private static List<WorldDisclosureDoorDifference> Doors(List<WorldDisclosureEvent> left, List<WorldDisclosureEvent> right) {
        var doors = new List<WorldDisclosureDoorDifference>();

        foreach (var door in left.Concat(second: right).Select(selector: static entry => entry.Door).Distinct(comparer: StringComparer.Ordinal)) {
            var first = left.Where(predicate: entry => (entry.Door == door)).ToList();
            var second = right.Where(predicate: entry => (entry.Door == door)).ToList();

            if (FirstDifference(left: first, right: second) is { } index) {
                doors.Add(item: new WorldDisclosureDoorDifference(Door: door, Index: index,
                    Tick: Math.Min(val1: ((index < first.Count) ? first[index].Tick : ulong.MaxValue), val2: ((index < second.Count) ? second[index].Tick : ulong.MaxValue))));
            }
        }
        return [.. doors.OrderBy(keySelector: static door => door.Tick)];
    }
    private static int Offset(WorldDisclosureEvent? left, WorldDisclosureEvent? right) {
        if ((left is null) || (right is null)) { return 0; }
        var (one, other) = (Render(entry: left), Render(entry: right));
        var offset = 0;

        while ((offset < one.Length) && (offset < other.Length) && (one[offset] == other[offset])) { offset++; }
        return offset;
    }
    private static string Where(WorldDisclosureEvent? left, WorldDisclosureEvent? right) {
        if ((left is null) || (right is null)) { return "one world's stream ended and the other's went on"; }
        if (left.Door != right.Door) { return $"door {left.Door} against door {right.Door}"; }
        if (left.Tick != right.Tick) { return string.Create(provider: CultureInfo.InvariantCulture, handler: $"door {left.Door} answered at tick {left.Tick} against {right.Tick}"); }
        var offset = 0;

        while ((offset < left.Payload.Length) && (offset < right.Payload.Length) && (left.Payload[offset] == right.Payload[offset])) { offset++; }
        var where = string.Create(provider: CultureInfo.InvariantCulture, handler: $"door {left.Door}, byte {offset} of {left.Payload.Length} against {right.Payload.Length}");

        return (((Document(entry: left) is { } one) && (Document(entry: right) is { } other)) ? $"{where}; document {Difference(left: one, right: other)}" : where);
    }
    // The JSON document a delivery's text carries on its last line, if any.
    private static string? Document(WorldDisclosureEvent entry) {
        if (!entry.Text) { return null; }
        var text = Encoding.UTF8.GetString(bytes: entry.Payload);
        var start = (text.LastIndexOf(value: '\n') + 1);

        return (((start < text.Length) && (text[start] == '{')) ? text[start..] : null);
    }
    private static string Difference(string left, string right) {
        try {
            return WorldJsonDifference.First(left: JsonNode.Parse(json: left), right: JsonNode.Parse(json: right));
        } catch (JsonException) {
            return "differs (not JSON)";
        }
    }
    private static string Excerpt(WorldDisclosureEvent? entry, int offset) {
        if (entry is null) { return Nothing; }
        var text = Render(entry: entry).ReplaceLineEndings(replacementText: "\\n");
        var start = Math.Clamp(value: (offset - Context), min: 0, max: text.Length);
        var end = Math.Min(val1: text.Length, val2: (start + (Context * 3)));

        return string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"{entry.Door}@{entry.Tick} \"{((start > 0) ? "…" : string.Empty)}{text[start..end]}{((end < text.Length) ? "…" : string.Empty)}\"");
    }
    private static string RenderResult(WorldSubmissionResult? result) => result switch {
        WorldSubmissionResult.Mutation { Outcome: var outcome } => string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"operation={outcome.OperationId} actor={outcome.Actor.Describe()} digest={outcome.PayloadDigest} decision={outcome.Decision} code={outcome.Code} detail={outcome.Detail} revision={outcome.AffectedGroupRevision} persistence={outcome.PersistenceStatus} watermark={outcome.DurableWatermark} version={outcome.Version.Sequence}"),
        null => "(no result)",
        _ => result.ToString(),
    };
    private static string RenderSnapshot(in WorldSnapshot snapshot) {
        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        text.Append(provider: invariant, handler: $"tick={snapshot.Tick} revision={snapshot.Revision} step={snapshot.StepTicks} engineTick={snapshot.EngineTick} authority={snapshot.Authority} full={snapshot.FieldsFull}");
        foreach (var entry in snapshot.Entries.Span) {
            text.Append(provider: invariant, handler: $" | body {entry.Index}.{entry.Generation} active={entry.Active} at=({entry.Position.X:R},{entry.Position.Y:R},{entry.Position.Z:R}) q=({entry.Orientation.X:R},{entry.Orientation.Y:R},{entry.Orientation.Z:R},{entry.Orientation.W:R}) heading={entry.Heading:R} color=({entry.BodyColor.X:R},{entry.BodyColor.Y:R},{entry.BodyColor.Z:R}) kit={entry.Kit} look={entry.Look} rig={entry.CatalogRig} facts={entry.Facts} continuity={entry.Continuity} placement={entry.PlacementId} state=[{string.Join(separator: ",", values: entry.State.ToArray())}]");
        }
        foreach (var cell in snapshot.FieldCells.Span) {
            var value = cell.Image.Value;
            var spelled = (!value.HasValue ? "(none)"
                : (value.Kind == Puck.State.CellKind.Vector) ? $"[{string.Join(separator: ",", values: value.AsVector.ToArray())}]"
                : value.ToString());

            text.Append(provider: invariant, handler: $" | cell row={cell.Row} cell={cell.Cell} field={cell.Field} raw={cell.Raw} region={cell.Region} name={cell.FieldName} image={cell.Image.Key}={spelled} {cell.Image.Behavior} {cell.Image.Clock}");
        }
        foreach (var input in snapshot.Inputs.Span) {
            text.Append(provider: invariant, handler: $" | input {input.Owner.Describe()} body={input.Body}.{input.Generation} revision={input.Revision} epoch={input.Epoch} mapping={input.Mapping} sources={input.Sources.Length} seats={input.SeatStates.Length}");
        }
        return text.ToString();
    }
}
