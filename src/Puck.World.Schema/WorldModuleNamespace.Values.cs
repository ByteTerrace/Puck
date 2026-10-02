using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

public static partial class WorldModuleNamespace {
    /// <summary>Replaces a visited value in its original object member or array component.</summary>
    /// <param name="holder">The holding object or array supplied to a name visitor.</param>
    /// <param name="jsonName">The object's member name or the array's invariant decimal index.</param>
    /// <param name="value">The replacement value.</param>
    /// <exception cref="ArgumentException">The holder is neither an object nor an array.</exception>
    public static void SetSiteValue(JsonNode holder, string jsonName, JsonNode? value) {
        if (holder is JsonObject obj) { obj[jsonName] = value; } else if (holder is JsonArray array) { array[int.Parse(jsonName, CultureInfo.InvariantCulture)] = value; } else { throw new ArgumentException(message: "A name site requires an object or array holder.", paramName: nameof(holder)); }
    }

    private static bool VisitBindable(JsonNode node, Type type, WorldNameVisitor visitor) {
        if (BindableValueShape.KeysType(type: type) is not { } keysType) { return false; }
        if (node is JsonObject) {
            Visit(node: node, type: keysType, visitor: visitor);
        } else if ((node is JsonArray components) && (BindableValueShape.Components(type: type) != 0)) {
            for (var index = 0; (index < components.Count); index++) {
                if (components[index] is not { } component) { continue; }
                var member = index switch { 0 => "X", 1 => "Y", _ => "Z" };

                if (WorldNameRegistry.TryResolve(declaringType: type, field: out var field, member: member, propertyType: typeof(BindableScalar))) {
                    visitor(components, index.ToString(provider: CultureInfo.InvariantCulture), component, field, typeof(BindableScalar));
                }
                Visit(node: component, type: typeof(BindableScalar), visitor: visitor);
            }
        }
        return true;
    }
    private static void VisitSectionKeys(JsonObject section, Type sectionType, JsonObject keys, WorldNameVisitor visitor) {
        VisitMember(keys, "clock", typeof(WorldSectionKeys), nameof(WorldSectionKeys.Clock), typeof(string), visitor);
        if (keys["keys"] is not JsonArray rows) { return; }
        foreach (var row in rows) {
            if (row is JsonObject partial) { VisitPartial(basis: section, partial: partial, type: sectionType, visitor: visitor); }
        }
    }
    // Partial section keys address existing rows by their local identity; use those rows only to recover the model
    // type and discriminator. The visitor still receives the original partial record's holder and values.
    private static void VisitPartial(JsonNode partial, JsonNode? basis, Type type, WorldNameVisitor visitor) {
        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);
        var shape = WorldModelShape.Of(type: type);

        if ((shape?.Kind == JsonTypeInfoKind.Enumerable) && (partial is JsonObject named) && (basis is JsonArray rows)) {
            foreach (var (name, value) in named) {
                var row = rows.FirstOrDefault(predicate: candidate => ((candidate is JsonObject obj) && (obj["name"] is JsonValue id)
                    && id.TryGetValue<string>(value: out var authoredName) && (authoredName == name)));

                if ((value is not null) && (row is not null)) { VisitPartial(value, row, shape.ElementType!, visitor); }
            }
            return;
        }
        if ((basis is JsonObject original) && (original["$type"] is JsonValue tag) && tag.TryGetValue<string>(value: out var kind)
            && (shape?.Arms.FirstOrDefault(predicate: arm => (arm.Discriminator == kind)) is { } arm)) {
            shape = WorldModelShape.Of(type: arm.Type);
        }
        if ((partial is not JsonObject fields) || (shape?.Kind != JsonTypeInfoKind.Object)) { return; }
        foreach (var member in VisitMembers.GetValue(createValueCallback: BuildVisitMembers, key: shape)) {
            if ((member.Name == "keys") || (fields[member.Name] is not { } value)) { continue; }
            if (member.Field is { } field) { visitor(fields, member.Name, value, field, member.Type); }
            if (WorldPresentationValues.IsBindable(type: member.Type)) { Visit(node: value, type: member.Type, visitor: visitor); } else { VisitPartial(value, (basis as JsonObject)?[member.Name], member.Type, visitor); }
        }
    }
}
