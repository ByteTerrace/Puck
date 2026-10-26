using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler;

// This is a compile contract, independent of optional semantic engine validation. Walk the emitted shape so
// members supplied by a template, a collection builtin or a bound object take the same refusal as literal ones.
internal static class WorldDocumentMembers {
    internal static void Validate(JsonObject? document, SourceMap sourceMap, DiagnosticBag diagnostics) {
        if ((document is null) || ((document["schema"]?.ToString() is { } schema) && (schema != "puck.world.definition.v1"))) { return; }
        Visit(node: document, pointer: "", type: typeof(WorldDefinition));

        void Visit(JsonNode? node, Type type, string pointer) {
            type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))) { type = type.GetGenericArguments()[0]; }
            if (node is JsonArray array) {
                for (var index = 0; (index < array.Count); ++index) {
                    Visit(node: array[index], type: type, pointer: $"{pointer}/{index}");
                }
                return;
            }
            if ((node is not JsonObject obj) || (WorldModelShape.Of(type: type) is not { } shape) ||
                (shape.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.None)) { return; }
            // These rows name canonical documents. Their compiled identity is the canonicalizer's digest;
            // source pins still use the same sha256/ spelling as file-backed assets, including through bindings.
            if (((type == typeof(WorldPrototype)) || (type == typeof(WorldPatch)) || (type == typeof(WorldTune)) || (type == typeof(WorldMusicRow)) || (type == typeof(Puck.State.TableRow))) &&
                Puck.Assets.ContentPin.TryParse(text: obj["hash"]?.ToString(), pin: out var pin)) {
                obj["hash"] = pin.Hex;
            }
            var polymorphic = (shape.Arms.Count > 0);

            if ((obj["$type"] is JsonValue discriminator) && discriminator.TryGetValue<string>(value: out var word) &&
                (shape.Arms.FirstOrDefault(predicate: arm => (arm.Discriminator == word)) is { } arm)) {
                shape = WorldModelShape.Of(type: arm.Type)!;
            }
            foreach (var (key, value) in obj) {
                var child = $"{pointer}/{key}";

                if ((shape.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Dictionary) && (shape.ElementType is { } element)) {
                    Visit(node: value, pointer: child, type: element);
                } else if (shape.Members.FirstOrDefault(predicate: member => ((member.Name == key) && ((member.Access & (WorldModelAccess.Read | WorldModelAccess.ExtensionData | WorldModelAccess.Ignored)) == WorldModelAccess.Read))) is { } member) {
                    Visit(node: value, type: member.Type, pointer: child);
                } else if (!((polymorphic && (key == "$type")) ||
                    ((pointer.Length > 0) && (key is "$replace" or "$drop")) ||
                    ((pointer.Length == 0) && (WorldConstructs.Table.RootArmWriting(documentKey: key) is WorldRootArm.Materials or WorldRootArm.Shapes or WorldRootArm.Cartridge)) ||
                    ((shape.Members.Any(predicate: static member => ((member.Access & WorldModelAccess.ExtensionData) != 0))) && DocumentExtensionsPolicy.IsReservedKey(key: key)))) {
                    var start = diagnostics.Count;

                    sourceMap.TryGetOrigin(jsonPointer: child, origin: out var origin);
                    diagnostics.ReportError(code: PuckDiagnosticCodes.SemanticValidation,
                        message: $"Unknown member '{key}' at '{child}'; no document member consumes it.", span: origin.Span);
                    diagnostics.AttributeFrom(sourcePath: origin.SourcePath, startIndex: start);
                }
            }
        }
    }
}
