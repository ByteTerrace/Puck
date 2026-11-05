using System.Numerics;
using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.Transpiler.Diagnostics;
using Puck.World.Authoring;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler;

// This is a compile contract, independent of optional semantic engine validation. Walk the emitted shape so
// members supplied by a template, a collection builtin or a bound object take the same refusal and the same
// canonical form as literal ones.
internal static class WorldDocumentMembers {
    // The positions whose numbers the model reads as single-precision floats, one by one or as a vector's components.
    private static readonly HashSet<Type> FloatPositions = [
        typeof(float), typeof(DocumentVector2), typeof(DocumentVector3), typeof(DocumentQuaternion), typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Quaternion),
    ];

    // Writes the canonical form of every value whose position the model types (a float's one spelling, a rotation's
    // one sign, a convex profile's one start vertex, a shape member whose zero is the member omitted), and, given
    // diagnostics, refuses a member no document member consumes.
    internal static void Canonicalize(JsonObject? document, SourceMap sourceMap, DiagnosticBag? diagnostics) {
        if ((document is null) || ((document["schema"]?.ToString() is { } schema) && (schema != "puck.world.definition.v1"))) { return; }
        _ = Visit(node: document, pointer: "", refuse: true, type: typeof(WorldDefinition));

        // Returns the node to keep in the visited one's place: itself, rewritten in place, or a canonical scalar. Below
        // a converter-backed type (a creation document) the walk reads the properties the converter reads, for their
        // canonical form only: the converter, not this walk, owns what such a type admits.
        JsonNode? Visit(JsonNode? node, Type type, string pointer, bool refuse) {
            type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);
            var list = (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)));

            if (list) { type = type.GetGenericArguments()[0]; }
            if (!list && FloatPositions.Contains(item: type)) {
                if (node is JsonArray components) {
                    for (var index = 0; (index < components.Count); ++index) {
                        if ((WorldDocumentRowDefaults.CanonicalFloat(value: components[index]) is var component) && !ReferenceEquals(objA: component, objB: components[index])) {
                            components[index] = component;
                        }
                    }
                    if (type == typeof(DocumentQuaternion)) {
                        WorldDocumentRowDefaults.CanonicalizeRotation(rotation: components);
                    }
                    return node;
                }
                return WorldDocumentRowDefaults.CanonicalFloat(value: node);
            }
            if (node is JsonArray array) {
                for (var index = 0; (index < array.Count); ++index) {
                    if ((Visit(node: array[index], type: type, pointer: DocumentJsonPointer.Child(index: index, pointer: pointer), refuse: refuse) is var element) && !ReferenceEquals(objA: element, objB: array[index])) {
                        array[index] = element;
                    }
                }
                return node;
            }
            if ((node is not JsonObject obj) || (WorldModelShape.Of(type: type) is not { } shape) ||
                ((shape.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.None) && (shape.Properties.Count == 0))) { return node; }
            // These rows name canonical documents. Their compiled identity is the canonicalizer's digest;
            // source pins still use the same sha256/ spelling as file-backed assets, including through bindings.
            if (((type == typeof(WorldPrototype)) || (type == typeof(WorldPatch)) || (type == typeof(WorldTune)) || (type == typeof(WorldMusicRow)) || (type == typeof(Puck.State.TableRow))) &&
                Puck.Assets.ContentPin.TryParse(text: obj["hash"]?.ToString(), pin: out var pin)) {
                obj["hash"] = pin.Hex;
            }
            if (type == typeof(ShapeDocument)) {
                WorldDocumentRowDefaults.OmitAbsentShapeMembers(shape: obj);
            }
            var polymorphic = (shape.Arms.Count > 0);

            if ((obj["$type"] is JsonValue discriminator) && discriminator.TryGetValue<string>(value: out var word) &&
                (shape.Arms.FirstOrDefault(predicate: arm => (arm.Discriminator == word)) is { } arm)) {
                shape = WorldModelShape.Of(type: arm.Type)!;
                if ((arm.Type == typeof(Puck.State.ActionEffect.SetState)) || (arm.Type == typeof(Puck.State.ActionEffect.AddState))) {
                    WorldDocumentEmitter.CanonicalizeEffectSource(effect: obj);
                }
            }
            var members = shape.Members;

            if (shape.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.None) {
                (members, refuse) = (shape.Properties, false);
            }
            List<(string Key, JsonNode? Value)>? replaced = null;

            foreach (var (key, value) in obj) {
                var child = DocumentJsonPointer.Child(name: key, pointer: pointer);
                Type? memberType = null;

                if ((pointer.Length == 0) && (WorldConstructs.Table.RootArmWriting(documentKey: key) is WorldRootArm.Shapes)) {
                    memberType = typeof(IReadOnlyList<ShapeDocument>);
                } else if ((shape.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Dictionary) && (shape.ElementType is { } element)) {
                    memberType = element;
                } else if (members.FirstOrDefault(predicate: member => ((member.Name == key) && ((member.Access & (WorldModelAccess.Read | WorldModelAccess.ExtensionData | WorldModelAccess.Ignored)) == WorldModelAccess.Read))) is { } member) {
                    memberType = member.Type;
                } else if (refuse && (diagnostics is not null) && !((polymorphic && (key == "$type")) ||
                    ((pointer.Length > 0) && (key is "$replace" or "$drop" or "$before")) ||
                    ((pointer.Length == 0) && (WorldConstructs.Table.RootArmWriting(documentKey: key) is WorldRootArm.Materials or WorldRootArm.Cartridge)) ||
                    ((shape.Members.Any(predicate: static member => ((member.Access & WorldModelAccess.ExtensionData) != 0))) && DocumentExtensionsPolicy.IsReservedKey(key: key)))) {
                    var finding = Diagnostic.Error(code: PuckDiagnosticCodes.SemanticValidation, message: $"Unknown member '{key}'; no document member consumes it{DocumentNearestNames.Suggestion(candidates: members.Where(predicate: static member => ((member.Access & (WorldModelAccess.Read | WorldModelAccess.ExtensionData | WorldModelAccess.Ignored)) == WorldModelAccess.Read)).Select(selector: static member => member.Name), name: key)}.", span: SourceSpan.None);

                    diagnostics.Report(diagnostic: sourceMap.Place(diagnostic: finding, jsonPointer: child));
                }
                if ((memberType is not null) && (Visit(node: value, pointer: child, refuse: refuse, type: memberType) is var kept) && !ReferenceEquals(objA: kept, objB: value)) {
                    (replaced ??= []).Add(item: (key, kept));
                }
            }
            foreach (var (key, value) in (replaced ?? [])) {
                obj[key] = value;
            }
            if (type == typeof(SdfPrismProfile)) {
                WorldDocumentRowDefaults.CanonicalizeProfile(profile: obj);
            }
            if (shape.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Dictionary) {
                OrderMembers(members: members, obj: obj);
            }
            return node;
        }
    }

    // An object's members in another order are the same object, so a typed object writes its discriminator first, then
    // its members in the model's declared order, then anything else (an extension key, a basis merge's $replace) as it
    // came. A dictionary's keys are data and keep their order.
    private static void OrderMembers(JsonObject obj, IReadOnlyList<WorldModelMember> members) {
        if (obj.Count < 2) { return; }
        var rank = Ranks.GetValue(key: members, createValueCallback: static list => {
            var ranks = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < list.Count); index++) { _ = ranks.TryAdd(key: list[index].Name, value: index); }
            return ranks;
        });
        var entries = obj.ToArray();
        var ordered = entries.OrderBy(keySelector: entry => ((entry.Key == "$type") ? -1 : (rank.TryGetValue(key: entry.Key, value: out var at) ? at : int.MaxValue))).ToArray();

        if (entries.Select(selector: static entry => entry.Key).SequenceEqual(second: ordered.Select(selector: static entry => entry.Key), comparer: StringComparer.Ordinal)) {
            return;
        }
        obj.Clear();
        foreach (var (key, value) in ordered) {
            obj[key] = value;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<WorldModelMember>, Dictionary<string, int>> Ranks = new();
}
