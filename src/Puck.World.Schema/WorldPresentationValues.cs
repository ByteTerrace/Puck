using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>The load-time preparation of named section keys into ordinary typed bindable curves. Authored records
/// remain on the original definition for saving; consumers share this cached prepared view, with no frame-time
/// JSON walk, reflection, or second interpolation mechanism.</summary>
public sealed class WorldPresentationValues {
    private static readonly ConditionalWeakTable<WorldDefinition, WorldPresentationValues> Cache = new();

    private WorldPresentationValues(WorldDefinition definition, IReadOnlyList<string> errors) { Definition = definition; Errors = errors; }

    /// <summary>The original definition with only its keyed presentation sections prepared.</summary>
    public WorldDefinition Definition { get; }
    /// <summary>Named preparation refusals; an admitted world has none.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Prepares a definition once, retaining neither the original nor the result after their consumers leave.</summary>
    /// <param name="definition">The immutable authored definition.</param>
    /// <returns>The shared prepared view and any structural refusal.</returns>
    public static WorldPresentationValues Of(WorldDefinition definition) => Cache.GetValue(createValueCallback: Compile, key: definition);

    internal static bool IsBindable(Type type) => (BindableValueShape.KeysType(type: type) is not null);

    private static WorldPresentationValues Compile(WorldDefinition definition) {
        var render = definition.Render;

        if ((render.Keys is null) && (render.Lighting?.Keys is null) && (render.Sky?.Keys is null)
            && (render.Environment?.Keys is null) && (definition.ThemeRaw?.Keys is null)) {
            return new(definition: definition, errors: []);
        }
        var errors = new List<string>();
        var compiler = new Compiler(definition: definition, errors: errors);

        try {
            var renderNode = JsonSerializer.SerializeToNode(render, WorldJsonContext.Default.WorldRenderDefaults)!;

            compiler.Visit(node: renderNode, path: "render", type: typeof(WorldRenderDefaults));
            var themeType = ((JsonTypeInfo<WorldThemeSection>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(WorldThemeSection)));
            var themeNode = ((definition.ThemeRaw is { } theme) ? JsonSerializer.SerializeToNode(jsonTypeInfo: themeType, value: theme) : null);

            if (themeNode is not null) { compiler.Visit(node: themeNode, path: "theme", type: typeof(WorldThemeSection)); }
            if (errors.Count != 0) { return new(definition: definition, errors: errors.ToArray()); }
            var prepared = definition with {
                RenderRaw = renderNode.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldRenderDefaults),
                ThemeRaw = themeNode?.Deserialize(jsonTypeInfo: themeType),
            };

            return new(definition: prepared, errors: []);
        } catch (JsonException error) {
            errors.Add(item: $"presentation keys: {error.Message}");
            return new(definition: definition, errors: errors.ToArray());
        }
    }

    private sealed class Leaf(JsonObject owner, string member, int count) {
        public JsonObject Owner { get; } = owner;
        public string Member { get; } = member;
        public JsonNode?[] Values { get; } = new JsonNode?[count];
    }
    private sealed class Compiler(WorldDefinition definition, List<string> errors) {
        private static Type Unwrap(Type type) => (Nullable.GetUnderlyingType(nullableType: type) ?? type);
        private static WorldModelType? Shape(Type type, JsonNode? node) {
            var shape = WorldModelShape.Of(type: Unwrap(type: type));

            if ((node is JsonObject obj) && (obj["$type"] is JsonValue tag) && tag.TryGetValue<string>(value: out var kind)
                && (shape?.Arms.FirstOrDefault(predicate: arm => (arm.Discriminator == kind)) is { } arm)) {
                return WorldModelShape.Of(type: arm.Type);
            }
            return shape;
        }

        public void Visit(JsonNode node, Type type, string path) {
            if (IsBindable(type: type)) { return; }
            var shape = Shape(node: node, type: type);

            if ((node is JsonObject obj) && (shape?.Kind == JsonTypeInfoKind.Object)) {
                if (obj["keys"] is JsonObject keys) {
                    Apply(keyed: keys, path: path, target: obj, type: type);
                    obj.Remove(propertyName: "keys");
                }
                foreach (var property in shape.Members) {
                    if ((property.Name != "keys") && (obj[property.Name] is { } child)) { Visit(child, property.Type, ((path + ".") + property.Name)); }
                }
            } else if ((node is JsonArray array) && (shape?.ElementType is { } element)) {
                for (var index = 0; (index < array.Count); index++) {
                    if (array[index] is { } child) { Visit(node: child, path: $"{path}[{index}]", type: element); }
                }
            }
        }

        private void Apply(JsonObject target, Type type, JsonObject keyed, string path) {
            if ((keyed["clock"] is not JsonValue clockValue) || !clockValue.TryGetValue<string>(value: out var clock) || (keyed["keys"] is not JsonArray rows)) {
                errors.Add(item: $"{path}.keys requires a clock and an ordered array of partial records.");
                return;
            }
            var positions = new List<WorldKey<int>>();

            foreach (var node in rows) {
                if ((node is not JsonObject row) || (row["at"] is not JsonValue at) || !at.TryGetValue<double>(value: out var position)) {
                    errors.Add(item: $"{path}.keys requires a finite 'at' on every partial record.");
                    return;
                }
                var ease = (row["ease"]?.Deserialize(jsonTypeInfo: ((JsonTypeInfo<WorldKeyEase>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(WorldKeyEase)))) ?? WorldKeyEase.Linear);

                positions.Add(item: new(At: position, Ease: ease, Value: 0));
            }
            if (!WorldValueValidation.TryValidate(curve: new WorldKeys<int>(Clock: clock, Keys: positions), definition: definition, reason: out var reason)) {
                errors.Add(item: $"{path}.keys {reason}.");
                return;
            }
            var leaves = new Dictionary<string, Leaf>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < rows.Count); index++) {
                Collect(rows[index]!.AsObject(), target, type, path, index, rows.Count, leaves, keyRoot: true);
            }
            foreach (var (name, leaf) in leaves) {
                if ((leaf.Owner[leaf.Member] is JsonObject existing) && existing.ContainsKey(propertyName: "clock")) {
                    errors.Add(item: $"{name} already has individual keys; section keys cannot nest a keyed value.");
                    continue;
                }
                var carried = leaf.Values.Last(predicate: static value => (value is not null))!;
                var values = new JsonArray();

                for (var index = 0; (index < rows.Count); index++) {
                    carried = (leaf.Values[index] ?? carried);
                    values.Add(value: new JsonObject { ["at"] = rows[index]!["at"]!.DeepClone(), ["ease"] = positions[index].Ease.ToString(), ["value"] = carried.DeepClone() });
                }
                leaf.Owner[leaf.Member] = new JsonObject { ["clock"] = clock, ["keys"] = values };
            }
        }
        private void Collect(JsonObject partial, JsonObject target, Type type, string path, int key, int count, Dictionary<string, Leaf> leaves, bool keyRoot = false) {
            var shape = Shape(node: target, type: type);

            foreach (var (name, value) in partial) {
                if (keyRoot && (name is "at" or "ease")) { continue; }
                var member = shape?.Members.FirstOrDefault(predicate: member => (member.Name == name));
                var named = ((path + ".") + name);

                if ((member is null) || (name == "keys")) { errors.Add(item: $"{named} is not a keyable presentation field."); continue; }
                if (IsBindable(type: member.Type)) {
                    if ((value is null) || ContainsKeys(node: value)) { errors.Add(item: $"{named} key {key} requires an ordinary literal or state binding, not null or nested keys."); continue; }
                    if (!leaves.TryGetValue(key: named, value: out var leaf)) { leaves.Add(key: named, value: leaf = new Leaf(count: count, member: name, owner: target)); }
                    leaf.Values[key] = value;
                    continue;
                }
                var childShape = Shape(member.Type, target[name]);

                if (value is not JsonObject child) { errors.Add(item: $"{named} is structure and cannot be keyed."); continue; }
                if ((childShape?.Kind == JsonTypeInfoKind.Enumerable) && (childShape.ElementType is { } element)) {
                    if (target[name] is not JsonArray existing) { errors.Add(item: $"{named} keys cannot add or remove rows; declare named rows in the section first."); continue; }
                    foreach (var (rowName, rowValue) in child) {
                        var matches = existing.OfType<JsonObject>().Where(predicate: row => (row["name"]?.GetValue<string>() == rowName)).ToArray();

                        if ((matches.Length != 1) || (rowValue is not JsonObject fields)) { errors.Add(item: $"{named}.{rowName} must name exactly one existing authored row."); continue; }
                        Collect(fields, matches[0], element, ((named + ".") + rowName), key, count, leaves);
                    }
                } else if (childShape?.Kind == JsonTypeInfoKind.Object) {
                    if (target[name] is not JsonObject destination) { target[name] = destination = new JsonObject(); }
                    Collect(child, destination, member.Type, named, key, count, leaves);
                } else { errors.Add(item: $"{named} is structure and cannot be keyed."); }
            }
        }
        private static bool ContainsKeys(JsonNode node) => node switch {
            JsonObject obj => (obj.ContainsKey(propertyName: "clock") || obj.ContainsKey(propertyName: "keys") || obj.Any(predicate: static member => ((member.Value is { } child) && ContainsKeys(node: child)))),
            JsonArray array => array.Any(predicate: static child => ((child is not null) && ContainsKeys(node: child))),
            _ => false,
        };
    }
}
