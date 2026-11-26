using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>What a render-lane name refusal is about.</summary>
public enum WorldRenderLaneRefusal : byte {
    /// <summary>Two <c>render.lanes</c> rows of one document carry the same name.</summary>
    Duplicate,
    /// <summary>A render-lane member names a lane no <c>render.lanes</c> row declares.</summary>
    Unknown,
    /// <summary>The document declares more lanes than the world's four.</summary>
    TooMany,
}

/// <summary>Resolves the world render lanes a document's creations write by name. Imports compose at load, so only the
/// composed document knows its whole lane list: each <c>render.lanes</c> row takes render lane 4 plus its position in
/// that list, and every render-lane member (<see cref="DocumentRenderLaneAttribute"/>) that writes a lane's name is
/// rewritten to that index before the strict read. The read model, the canonical creation documents and the bake's
/// cache identity therefore hold only indices, and a baked mesh never depends on a name.</summary>
public static class WorldRenderLaneNames {
    private const string RenderMember = "render";
    private const string LanesMember = "lanes";
    private const string NameMember = "name";

    // Every JSON name a render-lane member carries, read off the model once.
    private static readonly Lazy<byte[][]> MemberNames = new(valueFactory: static () => [
        .. WorldModelShape.Types
            .SelectMany(selector: static type => type.Members.Concat(second: type.Properties))
            .Where(predicate: static member => member.RenderLane)
            .Select(selector: static member => member.Name)
            .Distinct(comparer: StringComparer.Ordinal)
            .Select(selector: static name => Encoding.UTF8.GetBytes(s: $"\"{name}\"")),
    ]);
    private static readonly Puck.Abstractions.CodeDerivedTable<Type, object> Reach = new();
    private static readonly object Holds = new();
    private static readonly object Lacks = new();

    /// <summary>Resolves a document's lane names in its UTF-8 JSON. A document no render-lane member of which holds a
    /// string comes back as it is, after one scan of its bytes; otherwise only the root members that hold such a name are
    /// parsed and rewritten.</summary>
    /// <param name="utf8">The document's UTF-8 JSON, one object.</param>
    /// <param name="resolved">The document with every lane name replaced by its index; <paramref name="utf8"/> itself
    /// when nothing names a lane.</param>
    /// <param name="reason">The refusal, naming the lane and where it is written; empty on success.</param>
    /// <returns>Whether every name resolved.</returns>
    public static bool TryResolve(ReadOnlyMemory<byte> utf8, out ReadOnlyMemory<byte> resolved, out string reason) {
        resolved = utf8;
        reason = string.Empty;
        if (!NamesALane(json: utf8.Span) || (WorldDefinitionFileSource.MembersOf(json: utf8.Span) is not { } members)) { return true; }

        var failure = string.Empty;
        var lanes = Declared(render: members.FirstOrDefault(predicate: static member => member.Name == RenderMember) is { Name: not null } render
            ? JsonNode.Parse(utf8Json: utf8.Span.Slice(start: render.Start, length: render.Length))
            : null, refuse: Refuse);

        if (failure.Length > 0) {
            reason = failure;
            return false;
        }

        var rootShape = WorldModelShape.Of(type: typeof(WorldDefinition))!;
        var output = new ArrayBufferWriter<byte>(initialCapacity: (utf8.Length + 64));
        var changed = false;

        output.Write(value: "{"u8);
        for (var index = 0; (index < members.Length); index++) {
            var member = members[index];
            var value = utf8.Span.Slice(start: member.Start, length: member.Length);

            if (index > 0) { output.Write(value: ","u8); }
            output.Write(value: utf8.Span.Slice(start: member.KeyStart, length: (member.Start - member.KeyStart)));
            if (NamesALane(json: value) &&
                (rootShape.Members.FirstOrDefault(predicate: candidate => (candidate.Name == member.Name)) is { } model) &&
                Reaches(type: model.Type) && (JsonNode.Parse(utf8Json: value) is { } tree)) {
                var replaced = Walk(lanes: lanes, node: tree, pointer: DocumentJsonPointer.Child(name: member.Name, pointer: string.Empty), refuse: Refuse, rewrite: true, type: model.Type);

                if (failure.Length > 0) {
                    reason = failure;
                    return false;
                }
                output.Write(value: WorldDefinitionFileSource.Compact(node: replaced));
                changed = true;
            } else {
                output.Write(value: value);
            }
        }
        output.Write(value: "}"u8);
        if (changed) { resolved = output.WrittenMemory.ToArray(); }

        return true;

        void Refuse(WorldRenderLaneRefusal kind, string message, string pointer) {
            if (failure.Length == 0) { failure = $"{message} (at {pointer})"; }
        }
    }

    /// <summary>Checks or resolves a document's lane names in its tree.</summary>
    /// <param name="document">The document's root object; rewritten in place when <paramref name="rewrite"/> is set.</param>
    /// <param name="whole">Whether the document composes nothing, so its own <c>render.lanes</c> is the whole list and a
    /// name it does not declare is refused; a document that imports or names a basis checks only its own rows.</param>
    /// <param name="rewrite">Whether each resolved name is replaced by its index.</param>
    /// <param name="refuse">Receives each refusal: its kind, its message and the JSON pointer of the value it is about.</param>
    public static void Resolve(JsonObject document, bool whole, bool rewrite, Action<WorldRenderLaneRefusal, string, string> refuse) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: refuse);

        var lanes = Declared(render: document[RenderMember], refuse: refuse);

        if (!whole) { return; }
        foreach (var model in WorldModelShape.Of(type: typeof(WorldDefinition))!.Members) {
            if (Reaches(type: model.Type) && document.TryGetPropertyValue(propertyName: model.Name, jsonNode: out var value) && (value is not null)) {
                var replaced = Walk(lanes: lanes, node: value, pointer: DocumentJsonPointer.Child(name: model.Name, pointer: string.Empty), refuse: refuse, rewrite: rewrite, type: model.Type);

                if (!ReferenceEquals(objA: replaced, objB: value)) { document[model.Name] = replaced; }
            }
        }
    }

    // The lanes a render member declares, by name, each at render lane 4 plus its position; a duplicate or a fifth lane is
    // refused by name.
    private static Dictionary<string, int> Declared(JsonNode? render, Action<WorldRenderLaneRefusal, string, string> refuse) {
        var lanes = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        if ((render as JsonObject)?[LanesMember] is not JsonArray rows) { return lanes; }
        var pointer = DocumentJsonPointer.Child(name: LanesMember, pointer: DocumentJsonPointer.Child(name: RenderMember, pointer: string.Empty));

        for (var index = 0; (index < rows.Count); index++) {
            if ((rows[index] as JsonObject)?[NameMember] is not JsonValue spelled || !spelled.TryGetValue<string>(value: out var name)) { continue; }
            if (!lanes.TryAdd(key: name, value: (SdfLanes.FirstWorld + index))) {
                refuse(WorldRenderLaneRefusal.Duplicate, $"render.lanes declares '{name}' twice; each lane's name is its own.", DocumentJsonPointer.Child(name: NameMember, pointer: DocumentJsonPointer.Child(index: index, pointer: pointer)));
            }
        }
        if (rows.Count > WorldRenderDefaults.MaxLanes) {
            var names = string.Join(separator: ", ", values: rows.Select(selector: static row => ((row as JsonObject)?[NameMember]?.ToString() ?? "?")));

            refuse(WorldRenderLaneRefusal.TooMany, $"render.lanes declares {rows.Count.ToString(provider: CultureInfo.InvariantCulture)} lanes ({names}); a world holds at most {WorldRenderDefaults.MaxLanes.ToString(provider: CultureInfo.InvariantCulture)}, render lanes {SdfLanes.FirstWorld.ToString(provider: CultureInfo.InvariantCulture)} to {(SdfLanes.Count - 1).ToString(provider: CultureInfo.InvariantCulture)}.", pointer);
        }
        return lanes;
    }

    // Walks a value along its model type, replacing (when rewriting) each render-lane member's name by its index.
    // Returns the node to keep in the value's place.
    private static JsonNode Walk(JsonNode node, Type type, Dictionary<string, int> lanes, string pointer, bool rewrite, Action<WorldRenderLaneRefusal, string, string> refuse) {
        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))) { type = type.GetGenericArguments()[0]; }
        if (node is JsonArray array) {
            for (var index = 0; (index < array.Count); index++) {
                if ((array[index] is { } element) && Walk(lanes: lanes, node: element, pointer: DocumentJsonPointer.Child(index: index, pointer: pointer), refuse: refuse, rewrite: rewrite, type: type) is var kept && !ReferenceEquals(objA: kept, objB: element)) {
                    array[index] = kept;
                }
            }
            return node;
        }
        if ((node is not JsonObject obj) || (WorldModelShape.Of(type: type) is not { } shape)) { return node; }
        if ((obj["$type"] is JsonValue discriminator) && discriminator.TryGetValue<string>(value: out var word) &&
            (shape.Arms.FirstOrDefault(predicate: arm => (arm.Discriminator == word)) is { } arm) && (WorldModelShape.Of(type: arm.Type) is { } armShape)) {
            shape = armShape;
        }
        if ((shape.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Dictionary) && (shape.ElementType is { } entry)) {
            if (!Reaches(type: entry)) { return node; }
            foreach (var (key, value) in obj.ToArray()) {
                if ((value is not null) && Walk(lanes: lanes, node: value, pointer: DocumentJsonPointer.Child(name: key, pointer: pointer), refuse: refuse, rewrite: rewrite, type: entry) is var kept && !ReferenceEquals(objA: kept, objB: value)) {
                    obj[key] = kept;
                }
            }
            return node;
        }
        var members = ((shape.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.None) ? shape.Properties : shape.Members);

        foreach (var member in members) {
            if (!obj.TryGetPropertyValue(propertyName: member.Name, jsonNode: out var value) || (value is null)) { continue; }
            var at = DocumentJsonPointer.Child(name: member.Name, pointer: pointer);

            if (member.RenderLane) {
                if ((value is JsonValue spelled) && spelled.TryGetValue<string>(value: out var name)) {
                    if (lanes.TryGetValue(key: name, value: out var lane)) {
                        if (rewrite) { obj[member.Name] = lane; }
                    } else {
                        refuse(WorldRenderLaneRefusal.Unknown, $"'{name}' names no render lane; render.lanes declares {((lanes.Count == 0) ? "none" : string.Join(separator: ", ", values: lanes.Keys))}.", at);
                    }
                }
                continue;
            }
            if (Reaches(type: member.Type) && Walk(lanes: lanes, node: value, pointer: at, refuse: refuse, rewrite: rewrite, type: member.Type) is var kept && !ReferenceEquals(objA: kept, objB: value)) {
                obj[member.Name] = kept;
            }
        }
        return node;
    }

    // Whether a value of the type can hold a render-lane member, so a walk skips every subtree that cannot.
    private static bool Reaches(Type type) => ReferenceEquals(objA: Holds, objB: Reach.GetValue(key: (Nullable.GetUnderlyingType(nullableType: type) ?? type), derive: static type => (ReachesFrom(type: type, visiting: []) ? Holds : Lacks)));
    private static bool ReachesFrom(Type type, HashSet<Type> visiting) {
        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))) { return ReachesFrom(type: type.GetGenericArguments()[0], visiting: visiting); }
        if ((WorldModelShape.Of(type: type) is not { } shape) || !visiting.Add(item: type)) { return false; }
        if ((shape.ElementType is { } element) && ReachesFrom(type: element, visiting: visiting)) { return true; }
        foreach (var member in shape.Members.Concat(second: shape.Properties)) {
            if (member.RenderLane || ReachesFrom(type: member.Type, visiting: visiting)) { return true; }
        }
        return shape.Arms.Any(predicate: arm => ReachesFrom(type: arm.Type, visiting: visiting));
    }

    // Whether a render-lane member in these bytes holds a string: its quoted name, then a colon, then a quote, across any
    // whitespace. A false match only costs a parse.
    private static bool NamesALane(ReadOnlySpan<byte> json) {
        foreach (var name in MemberNames.Value) {
            var rest = json;

            for (var at = rest.IndexOf(value: name); (at >= 0); at = rest.IndexOf(value: name)) {
                rest = rest[(at + name.Length)..].TrimStart(trimElements: " \t\r\n"u8);
                if (rest.IsEmpty || (rest[0] != (byte)':')) { continue; }
                rest = rest[1..].TrimStart(trimElements: " \t\r\n"u8);
                if (!rest.IsEmpty && (rest[0] == (byte)'"')) { return true; }
            }
        }
        return false;
    }
}
