using Puck.Abstractions.Documents;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>Builds the values the write door's laws submit, from the code: for a model type a mutation member carries,
/// every state name position the name registry reaches in it (<see cref="WorldNameRegistry.SitesUnder"/>), each made
/// into a value of that type spelling one probe name at that one position. Every other member is the least value its
/// type's serializer admits (a required member is present, nothing else is): the door answers before composition or
/// validation reads the rest.</summary>
internal static partial class WriteDoorProbes {
    /// <summary>One position a value of a model type carries a state name at.</summary>
    /// <param name="Path">The registry's path, rooted at the type.</param>
    /// <param name="Field">The registration.</param>
    public sealed record Position(string Path, WorldNameField Field) {
        public override string ToString() => $"{Field.Owner.Name}.{Field.Member} at {Path}";
    }

    /// <summary>Gets the state name positions a value of <paramref name="type"/> carries, re-entrant paths aside (each
    /// re-entry repeats a type the walk already listed).</summary>
    public static IReadOnlyList<Position> StatePositions(Type type) => [..
        WorldNameRegistry.SitesUnder(type)
            .Where(site => (site.Field.Member.Length > 0) && !site.Path.EndsWith('…') && WorldSpelledNames.NamesState(site.Field.Kind) &&
                (site.Field.Role != WorldNameRole.Declares))
            .Select(site => new Position(site.Path, site.Field))];

    /// <summary>Gets every model type a mutation outside a program section carries as a member (or as a list of
    /// them), with the mutation kind and member: the values a writer submits whole.</summary>
    public static IReadOnlyList<(Type Kind, PropertyInfo Member, Type Model, bool List)> SubmittedModels() {
        var found = new List<(Type, PropertyInfo, Type, bool)>();
        foreach (var kind in typeof(WorldMutation).GetNestedTypes().Where(kind => typeof(WorldMutation).IsAssignableFrom(kind) && !kind.IsAbstract)) {
            if (SectionOf(kind).IsProgram()) { continue; }
            // A mutation's principal is the ingress-stamped actor, never a value it submits.
            foreach (var member in kind.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(member => member.PropertyType != typeof(Commands.Principal))) {
                var type = Nullable.GetUnderlyingType(member.PropertyType) ?? member.PropertyType;
                var element = ElementOf(type);
                if ((type != typeof(string)) && (WorldModelShape.Of(type) is not null) && (element is null || WorldModelShape.Of(element) is null)) { found.Add((kind, member, type, false)); }
                else if ((element is not null) && (WorldModelShape.Of(element) is not null)) { found.Add((kind, member, element, true)); }
            }
        }
        return found;
    }
    /// <summary>Returns the section a mutation kind targets.</summary>
    public static WorldSection SectionOf(Type kind) =>
        WorldMutationKindCatalog.SectionOf((WorldMutation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(kind));

    /// <summary>Creates a mutation of <paramref name="kind"/> stamped with <paramref name="principal"/>, its
    /// <paramref name="member"/> holding <paramref name="value"/> (or a one-element list of it), every other member its
    /// default.</summary>
    public static WorldMutation Submit(Type kind, PropertyInfo member, object value, bool list, Commands.Principal principal) {
        var mutation = (WorldMutation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(kind);
        typeof(WorldMutation).GetProperty(nameof(WorldMutation.Principal))!.SetValue(mutation, principal);
        return SetMember(mutation, member, value, list);
    }
    /// <summary>Sets one member of a mutation to <paramref name="value"/>, or to a one-element list of it.</summary>
    public static WorldMutation SetMember(WorldMutation mutation, PropertyInfo member, object value, bool list) {
        if (list) {
            var items = Array.CreateInstance(value.GetType(), 1);
            items.SetValue(value, 0);
            member.SetValue(mutation, items);
        } else {
            member.SetValue(mutation, value);
        }
        return mutation;
    }

    /// <summary>Builds a value of <paramref name="type"/> that spells <paramref name="name"/> at one position.</summary>
    /// <returns>The value, or <see langword="null"/> with the reason when the position cannot be spelled.</returns>
    public static object? Build(Type type, Position position, string name, out string reason) {
        var containers = new List<(JsonObject Node, Type Type)>();
        var root = new JsonObject();
        if (!TrySpell(root, type, position, name, containers, out reason)) { return null; }
        var choices = new List<(JsonObject Node, string Member, Type Enum)>();
        foreach (var (node, owner) in containers) { FillRequired(node, owner, choices); }
        if (TryDeserialize(root, type, out var value, out reason)) { return value; }
        // A filled enum member its owner refuses (a predicate's comparison) takes each other value in turn.
        foreach (var (node, member, enumType) in choices) {
            var held = node[member];
            foreach (var candidate in Enum.GetValues(enumType)) {
                node[member] = JsonSerializer.SerializeToNode(candidate, Info(enumType));
                if (TryDeserialize(root, type, out value, out _)) { return value; }
            }
            node[member] = held;
        }
        return null;
    }
    private static bool TryDeserialize(JsonObject root, Type type, out object? value, out string reason) {
        reason = string.Empty;
        try {
            value = root.DeepClone().Deserialize(Info(type));
            return value is not null;
        } catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException) {
            reason = $"does not deserialize: {error.Message}";
            value = null;
            return false;
        }
    }

    [GeneratedRegex(@"\.?[A-Za-z0-9_$]+|\[\]|\[[^\]]+\]|\{[^}]+\}")]
    private static partial Regex Segments();
    private static JsonTypeInfo Info(Type type) => WorldJsonContext.Default.Options.GetTypeInfo(type);

    // Walks the path from the root, creating each object, list element, union arm and dictionary entry on the way, and
    // writes the leaf in the spelling its role reads. Each object created is recorded with its type, whose required
    // members are filled afterwards.
    private static bool TrySpell(JsonObject root, Type rootType, Position position, string name, List<(JsonObject, Type)> containers, out string reason) {
        reason = string.Empty;
        var segments = Segments().Matches(position.Path).Select(match => match.Value).ToList();
        var member = position.Field.Owner.GetProperty(position.Field.Member);
        var leafType = (member is null) ? typeof(string) : (Nullable.GetUnderlyingType(member.PropertyType) ?? member.PropertyType);
        // An expression program's instructions are written by its converter: spell the program and stop at it.
        var program = segments.FindIndex(segment => segment is "{instructions}" or "{subprograms}");
        if (program >= 0) {
            segments = segments[..program];
            // A list of programs holds the one spelled program as its element.
            leafType = ((segments.Count > 0) && (segments[^1] == "[]")) ? typeof(IReadOnlyList<ExpressionProgram>) : typeof(ExpressionProgram);
            if (segments[^1] == "[]") { segments = segments[..^1]; }
        }
        if ((segments.Count > 0) && (segments[^1] == "{state}")) {
            segments = segments[..^1];
            leafType = typeof(Puck.World.Authoring.CreationDocument);
        }
        if ((segments.Count > 0) && (segments[^1] == "{row}")) {
            segments = segments[..^1];
            leafType = typeof(WorldLatticeScalar);
        }
        var current = root;
        var type = rootType;
        containers.Add((root, rootType));
        for (var index = 0; index < segments.Count; index++) {
            var segment = segments[index];
            if (segment.StartsWith('[') && (segment != "[]")) {
                var info = Info(type);
                var arm = segment[1..^1];
                if (info.PolymorphismOptions?.DerivedTypes.FirstOrDefault(derived => derived.TypeDiscriminator?.ToString() == arm) is not { } derivedType) {
                    reason = $"no arm {arm} on {type.Name}"; return false;
                }
                current[info.PolymorphismOptions.TypeDiscriminatorPropertyName] = arm;
                type = derivedType.DerivedType;
                containers[^1] = (current, type);
                continue;
            }
            if (segment.StartsWith('{')) {
                if (segment != "{*}") { reason = $"an unspellable segment {segment}"; return false; }
                continue;
            }
            if (segment == "[]") { continue; }
            var property = segment.TrimStart('.');
            if (index == (segments.Count - 1)) {
                current[property] = Leaf(position.Field, leafType, name);
                return true;
            }
            var memberType = MemberType(type, property);
            if (memberType is null) { reason = $"no member {property} on {type.Name}"; return false; }
            var child = new JsonObject();
            var childType = memberType;
            var next = segments[index + 1];
            if (next == "[]") {
                childType = ElementOf(memberType) ?? memberType;
                current[property] = new JsonArray(child);
            } else if (next == "{*}") {
                childType = DictionaryValue(memberType) ?? memberType;
                current[property] = new JsonObject { ["k"] = child };
            } else {
                current[property] = child;
            }
            current = child;
            type = Nullable.GetUnderlyingType(childType) ?? childType;
            containers.Add((current, type));
        }
        reason = $"the path ends without a member: {position.Path}";
        return false;
    }
    private static Type? MemberType(Type owner, string jsonName) {
        if (Info(owner).Properties.FirstOrDefault(property => property.Name == jsonName) is { } property) { return property.PropertyType; }
        // A converter-backed type (a state row) has no serializer members; its C# members carry the JSON names.
        return owner.GetProperties().FirstOrDefault(candidate => JsonNamingPolicy.CamelCase.ConvertName(candidate.Name) == jsonName)?.PropertyType;
    }
    private static Type? DictionaryValue(Type type) => type.GetInterfaces().Prepend(type)
        .FirstOrDefault(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
        ?.GetGenericArguments()[1];

    private static JsonNode Leaf(WorldNameField field, Type leafType, string name) {
        if (DictionaryValue(leafType) is { } valueType) { return new JsonObject { ["k"] = Leaf(field, valueType, name) }; }
        var element = ElementOf(leafType);
        var scalarType = Nullable.GetUnderlyingType(element ?? leafType) ?? element ?? leafType;
        var spelled = field.Role switch {
            WorldNameRole.Binding => $"state.{name}",
            WorldNameRole.Template => $"{{state.{name}}}",
            WorldNameRole.Key => $"$cell:{name}:a",
            WorldNameRole.BodyReference => $"cell:{name}:a",
            _ => name,
        };
        var one = (scalarType == typeof(ExpressionProgram))
            ? (ExpressionSpelling.TryParse(name, out var program, out var error) ? ExpressionProgramJsonConverter.ToNode(program) : throw new InvalidOperationException(error))
            : (scalarType == typeof(WorldLatticeScalar)) ? new JsonObject { ["row"] = name }
            : (scalarType == typeof(Puck.World.Authoring.CreationDocument)) ? BoundCreation(name)
            : (scalarType == typeof(StateChannelRef)) ? StateChannelRefJsonConverter.ToNode(StateChannelRef.Parse(spelled.StartsWith('$') ? spelled : $"$cell:{name}:a"))
            : JsonValue.Create(spelled)!;
        return (element is not null) ? new JsonArray(one) : one;
    }

    // Fills every required member an object's type declares and the path left out with the least value the member's
    // type admits.
    private static void FillRequired(JsonObject node, Type type, List<(JsonObject, string, Type)> choices) {
        if (typeof(StateRow).IsAssignableFrom(type)) {
            node.TryAdd("name", Filler);
            node.TryAdd("kind", "int");
            return;
        }
        if (type == typeof(StateCell)) {
            node.TryAdd("key", "a");
            node.TryAdd("value", 0);
            return;
        }
        var info = Info(type);
        // A member declared on an abstract union's base is spelled in its first arm.
        if (type.IsAbstract && (info.PolymorphismOptions is { } union) && !node.ContainsKey(union.TypeDiscriminatorPropertyName) &&
            (union.DerivedTypes.FirstOrDefault() is { TypeDiscriminator: { } discriminator } first)) {
            node.Insert(0, union.TypeDiscriminatorPropertyName, discriminator.ToString());
            type = first.DerivedType;
        }
        foreach (var property in Info(type).Properties.Where(property => property.IsRequired && !node.ContainsKey(property.Name))) {
            node[property.Name] = Least(property.PropertyType, depth: 0, AllowsNull(property));
            if ((Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) is { IsEnum: true } enumType) { choices.Add((node, property.Name, enumType)); }
        }
    }
    /// <summary>The least text a required member takes: a name the laws' documents declare public, so a required name
    /// position spells a row every writer reads and only the probe position refuses.</summary>
    public const string Filler = "pub";
    private static JsonNode? Least(Type type, int depth, bool allowsNull) {
        var underlying = Nullable.GetUnderlyingType(type);
        if ((underlying is not null) || (allowsNull && !type.IsValueType)) {
            if (Admits(type, null)) { return null; }
        }
        type = underlying ?? type;
        var candidates = new List<Func<JsonNode?>> {
            () => type.IsValueType ? JsonSerializer.SerializeToNode(Activator.CreateInstance(type), Info(type)) : null,
            () => JsonValue.Create(Filler),
            () => JsonValue.Create(0),
            () => JsonValue.Create(false),
            () => new JsonArray(),
            () => new JsonArray(0, 0, 0),
            () => new JsonArray(0, 0),
            () => new JsonArray(0, 0, 0, 1),
            () => (depth < 4) && (Info(type).Properties.Count > 0) ? LeastObject(type, depth) : null,
        };
        foreach (var candidate in candidates) {
            JsonNode? node;
            try { node = candidate(); } catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException or ArgumentException or MissingMethodException) { continue; }
            if ((node is not null) && Admits(type, node)) { return node; }
        }
        return null;
    }
    private static JsonObject LeastObject(Type type, int depth) {
        var node = new JsonObject();
        var info = Info(type);
        if (info.PolymorphismOptions?.DerivedTypes.FirstOrDefault() is { } derived && derived.TypeDiscriminator is { } discriminator) {
            node[info.PolymorphismOptions.TypeDiscriminatorPropertyName] = discriminator.ToString();
            type = derived.DerivedType;
        }
        foreach (var property in Info(type).Properties.Where(property => property.IsRequired)) {
            node[property.Name] = Least(property.PropertyType, depth + 1, AllowsNull(property));
        }
        return node;
    }
    // Whether a member's declaration admits null: a required member its type annotates non-null takes a value.
    private static bool AllowsNull(JsonPropertyInfo property) => property.AttributeProvider switch {
        PropertyInfo member => Nullability.Create(member).ReadState != NullabilityState.NotNull,
        FieldInfo member => Nullability.Create(member).ReadState != NullabilityState.NotNull,
        _ => false,
    };
    private static readonly NullabilityInfoContext Nullability = new();
    // A creation document spelling one binding: a sample creation with its first value a state cell may stand in for
    // (a number, or a vector's list of numbers) bound to the probe.
    private static JsonNode BoundCreation(string name) {
        var info = Info(typeof(Puck.World.Authoring.CreationDocument));
        var sample = JsonSerializer.SerializeToNode(CreationFixtures.Sphere(id: "probe", scale: 0.2f).Document, info)!;
        foreach (var node in Descendants(sample).Where(node => (node is JsonValue value && value.TryGetValue<double>(out _)) ||
            (node is JsonArray list && list.All(element => element is JsonValue number && number.TryGetValue<double>(out _)))).ToList()) {
            var parent = node.Parent;
            var held = node.DeepClone();
            if (!Replace(parent, node, JsonValue.Create($"state.{name}"), out var bound)) { continue; }
            if (Admits(typeof(Puck.World.Authoring.CreationDocument), sample)) { return sample; }
            _ = Replace(parent, bound, held, out _);
        }
        throw new InvalidOperationException("no value of the sample creation binds");
    }
    private static bool Replace(JsonNode? parent, JsonNode node, JsonNode replacement, out JsonNode placed) {
        placed = replacement;
        switch (parent) {
            case JsonObject holder: holder[node.GetPropertyName()] = replacement; return true;
            case JsonArray list: list[node.GetElementIndex()] = replacement; return true;
            default: return false;
        }
    }
    private static IEnumerable<JsonNode> Descendants(JsonNode? node) => node switch {
        JsonObject obj => obj.SelectMany(entry => (entry.Value is null) ? [] : Descendants(entry.Value).Prepend(entry.Value)),
        JsonArray list => list.SelectMany(element => (element is null) ? [] : Descendants(element).Prepend(element)),
        _ => [],
    };
    private static bool Admits(Type type, JsonNode? node) {
        try {
            _ = (node?.DeepClone()).Deserialize(Info(type));
            return true;
        } catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException) {
            return false;
        }
    }
    private static Type? ElementOf(Type type) => (type == typeof(string)) ? null : type.GetInterfaces().Prepend(type)
        .FirstOrDefault(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        ?.GetGenericArguments()[0];
}
