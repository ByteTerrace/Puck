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
        if (holder is JsonObject obj) { obj[jsonName] = value; }
        else if (holder is JsonArray array) { array[int.Parse(jsonName, CultureInfo.InvariantCulture)] = value; }
        else { throw new ArgumentException("A name site requires an object or array holder.", nameof(holder)); }
    }

    private static bool VisitBindable(JsonNode node, Type type, WorldNameVisitor visitor) {
        if (BindableValueShape.KeysType(type) is not { } keysType) { return false; }
        if (node is JsonObject) {
            Visit(node, keysType, visitor);
        } else if (node is JsonArray components && BindableValueShape.Components(type) != 0) {
            for (var index = 0; index < components.Count; index++) {
                if (components[index] is not { } component) { continue; }
                var member = index switch { 0 => "X", 1 => "Y", _ => "Z" };
                if (WorldNameRegistry.TryResolve(type, member, typeof(BindableScalar), out var field)) {
                    visitor(components, index.ToString(CultureInfo.InvariantCulture), component, field, typeof(BindableScalar));
                }
                Visit(component, typeof(BindableScalar), visitor);
            }
        }
        return true;
    }

    private static void VisitSectionKeys(JsonObject section, Type sectionType, JsonObject keys, WorldNameVisitor visitor) {
        VisitMember(keys, "clock", typeof(WorldSectionKeys), nameof(WorldSectionKeys.Clock), typeof(string), visitor);
        if (keys["keys"] is not JsonArray rows) { return; }
        foreach (var row in rows) {
            if (row is JsonObject partial) { VisitPartial(partial, section, sectionType, visitor); }
        }
    }

    // Partial section keys address existing rows by their local identity; use those rows only to recover the model
    // type and discriminator. The visitor still receives the original partial record's holder and values.
    private static void VisitPartial(JsonNode partial, JsonNode? basis, Type type, WorldNameVisitor visitor) {
        type = Nullable.GetUnderlyingType(type) ?? type;
        var shape = WorldModelShape.Of(type);
        if (shape?.Kind == JsonTypeInfoKind.Enumerable && partial is JsonObject named && basis is JsonArray rows) {
            foreach (var (name, value) in named) {
                var row = rows.FirstOrDefault(candidate => candidate is JsonObject obj && obj["name"] is JsonValue id
                    && id.TryGetValue<string>(out var authoredName) && authoredName == name);
                if (value is not null && row is not null) { VisitPartial(value, row, shape.ElementType!, visitor); }
            }
            return;
        }
        if (basis is JsonObject original && original["$type"] is JsonValue tag && tag.TryGetValue<string>(out var kind)
            && shape?.Arms.FirstOrDefault(arm => arm.Discriminator == kind) is { } arm) {
            shape = WorldModelShape.Of(arm.Type);
        }
        if (partial is not JsonObject fields || shape?.Kind != JsonTypeInfoKind.Object) { return; }
        foreach (var member in VisitMembers.GetValue(shape, BuildVisitMembers)) {
            if (member.Name == "keys" || fields[member.Name] is not { } value) { continue; }
            if (member.Field is { } field) { visitor(fields, member.Name, value, field, member.Type); }
            if (WorldPresentationValues.IsBindable(member.Type)) { Visit(value, member.Type, visitor); }
            else { VisitPartial(value, (basis as JsonObject)?[member.Name], member.Type, visitor); }
        }
    }
}
