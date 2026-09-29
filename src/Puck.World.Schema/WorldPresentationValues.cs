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
    public static WorldPresentationValues Of(WorldDefinition definition) => Cache.GetValue(definition, Compile);

    internal static bool IsBindable(Type type) => BindableValueShape.KeysType(type) is not null;

    private static WorldPresentationValues Compile(WorldDefinition definition) {
        var render = definition.Render;
        if (render.Keys is null && render.Lighting?.Keys is null && render.Sky?.Keys is null
            && render.Environment?.Keys is null && definition.ThemeRaw?.Keys is null) {
            return new(definition, []);
        }
        var errors = new List<string>();
        var compiler = new Compiler(definition, errors);
        try {
            var renderNode = JsonSerializer.SerializeToNode(render, WorldJsonContext.Default.WorldRenderDefaults)!;
            compiler.Visit(renderNode, typeof(WorldRenderDefaults), "render");
            var themeType = (JsonTypeInfo<WorldThemeSection>)WorldJsonContext.Default.Options.GetTypeInfo(typeof(WorldThemeSection));
            var themeNode = definition.ThemeRaw is { } theme ? JsonSerializer.SerializeToNode(theme, themeType) : null;
            if (themeNode is not null) { compiler.Visit(themeNode, typeof(WorldThemeSection), "theme"); }
            if (errors.Count != 0) { return new(definition, errors.ToArray()); }
            var prepared = definition with {
                RenderRaw = renderNode.Deserialize(WorldJsonContext.Default.WorldRenderDefaults),
                ThemeRaw = themeNode?.Deserialize(themeType),
            };
            return new(prepared, []);
        } catch (JsonException error) {
            errors.Add($"presentation keys: {error.Message}");
            return new(definition, errors.ToArray());
        }
    }

    private sealed class Leaf(JsonObject owner, string member, int count) {
        public JsonObject Owner { get; } = owner;
        public string Member { get; } = member;
        public JsonNode?[] Values { get; } = new JsonNode?[count];
    }

    private sealed class Compiler(WorldDefinition definition, List<string> errors) {
        private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;
        private static WorldModelType? Shape(Type type, JsonNode? node) {
            var shape = WorldModelShape.Of(Unwrap(type));
            if (node is JsonObject obj && obj["$type"] is JsonValue tag && tag.TryGetValue<string>(out var kind)
                && shape?.Arms.FirstOrDefault(arm => arm.Discriminator == kind) is { } arm) {
                return WorldModelShape.Of(arm.Type);
            }
            return shape;
        }

        public void Visit(JsonNode node, Type type, string path) {
            if (IsBindable(type)) { return; }
            var shape = Shape(type, node);
            if (node is JsonObject obj && shape?.Kind == JsonTypeInfoKind.Object) {
                if (obj["keys"] is JsonObject keys) {
                    Apply(obj, type, keys, path);
                    obj.Remove("keys");
                }
                foreach (var property in shape.Members) {
                    if (property.Name != "keys" && obj[property.Name] is { } child) { Visit(child, property.Type, path + "." + property.Name); }
                }
            } else if (node is JsonArray array && shape?.ElementType is { } element) {
                for (var index = 0; index < array.Count; index++) {
                    if (array[index] is { } child) { Visit(child, element, $"{path}[{index}]"); }
                }
            }
        }

        private void Apply(JsonObject target, Type type, JsonObject keyed, string path) {
            if (keyed["clock"] is not JsonValue clockValue || !clockValue.TryGetValue<string>(out var clock) || keyed["keys"] is not JsonArray rows) {
                errors.Add($"{path}.keys requires a clock and an ordered array of partial records.");
                return;
            }
            var positions = new List<WorldKey<int>>();
            foreach (var node in rows) {
                if (node is not JsonObject row || row["at"] is not JsonValue at || !at.TryGetValue<double>(out var position)) {
                    errors.Add($"{path}.keys requires a finite 'at' on every partial record.");
                    return;
                }
                var ease = row["ease"]?.Deserialize((JsonTypeInfo<WorldKeyEase>)WorldJsonContext.Default.Options.GetTypeInfo(typeof(WorldKeyEase))) ?? WorldKeyEase.Linear;
                positions.Add(new(position, 0, ease));
            }
            if (!WorldValueValidation.TryValidate(new WorldKeys<int>(clock, positions), definition, out var reason)) {
                errors.Add($"{path}.keys {reason}.");
                return;
            }
            var leaves = new Dictionary<string, Leaf>(StringComparer.Ordinal);
            for (var index = 0; index < rows.Count; index++) {
                Collect(rows[index]!.AsObject(), target, type, path, index, rows.Count, leaves, keyRoot: true);
            }
            foreach (var (name, leaf) in leaves) {
                if (leaf.Owner[leaf.Member] is JsonObject existing && existing.ContainsKey("clock")) {
                    errors.Add($"{name} already has individual keys; section keys cannot nest a keyed value.");
                    continue;
                }
                var carried = leaf.Values.Last(static value => value is not null)!;
                var values = new JsonArray();
                for (var index = 0; index < rows.Count; index++) {
                    carried = leaf.Values[index] ?? carried;
                    values.Add(new JsonObject { ["at"] = rows[index]!["at"]!.DeepClone(), ["ease"] = positions[index].Ease.ToString(), ["value"] = carried.DeepClone() });
                }
                leaf.Owner[leaf.Member] = new JsonObject { ["clock"] = clock, ["keys"] = values };
            }
        }

        private void Collect(JsonObject partial, JsonObject target, Type type, string path, int key, int count, Dictionary<string, Leaf> leaves, bool keyRoot = false) {
            var shape = Shape(type, target);
            foreach (var (name, value) in partial) {
                if (keyRoot && name is "at" or "ease") { continue; }
                var member = shape?.Members.FirstOrDefault(member => member.Name == name);
                var named = path + "." + name;
                if (member is null || name == "keys") { errors.Add($"{named} is not a keyable presentation field."); continue; }
                if (IsBindable(member.Type)) {
                    if (value is null || ContainsKeys(value)) { errors.Add($"{named} key {key} requires an ordinary literal or state binding, not null or nested keys."); continue; }
                    if (!leaves.TryGetValue(named, out var leaf)) { leaves.Add(named, leaf = new Leaf(target, name, count)); }
                    leaf.Values[key] = value;
                    continue;
                }
                var childShape = Shape(member.Type, target[name]);
                if (value is not JsonObject child) { errors.Add($"{named} is structure and cannot be keyed."); continue; }
                if (childShape?.Kind == JsonTypeInfoKind.Enumerable && childShape.ElementType is { } element) {
                    if (target[name] is not JsonArray existing) { errors.Add($"{named} keys cannot add or remove rows; declare named rows in the section first."); continue; }
                    foreach (var (rowName, rowValue) in child) {
                        var matches = existing.OfType<JsonObject>().Where(row => row["name"]?.GetValue<string>() == rowName).ToArray();
                        if (matches.Length != 1 || rowValue is not JsonObject fields) { errors.Add($"{named}.{rowName} must name exactly one existing authored row."); continue; }
                        Collect(fields, matches[0], element, named + "." + rowName, key, count, leaves);
                    }
                } else if (childShape?.Kind == JsonTypeInfoKind.Object) {
                    if (target[name] is not JsonObject destination) { target[name] = destination = new JsonObject(); }
                    Collect(child, destination, member.Type, named, key, count, leaves);
                } else { errors.Add($"{named} is structure and cannot be keyed."); }
            }
        }

        private static bool ContainsKeys(JsonNode node) => node switch {
            JsonObject obj => obj.ContainsKey("clock") || obj.ContainsKey("keys") || obj.Any(static member => member.Value is { } child && ContainsKeys(child)),
            JsonArray array => array.Any(static child => child is not null && ContainsKeys(child)),
            _ => false,
        };
    }
}
