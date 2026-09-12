using System.Text.Json;

namespace Puck.Abstractions.Machines;

/// <summary>Validates provider data against the same field tree consumed by schema and authoring tools.</summary>
public static class MachineConfigurationValidation {
    /// <summary>Validates an object, including unknown/duplicate fields, exact integers, and required members.</summary>
    /// <param name="descriptor">The registered versioned object shape.</param>
    /// <param name="value">The configuration or operation payload.</param>
    /// <param name="schemaTag">Whether the object must carry its schema identifier as a schema member.</param>
    /// <param name="errors">Receives authored field paths and explanations.</param>
    /// <returns>Whether validation added no errors.</returns>
    public static bool TryValidate(MachineObjectDescriptor descriptor, JsonElement value, bool schemaTag, ICollection<string> errors) {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(errors);
        var before = errors.Count;
        if (value.ValueKind != JsonValueKind.Object) {
            errors.Add("configuration: expected an object.");
            return false;
        }
        if (schemaTag && (!value.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != descriptor.Id)) {
            errors.Add($"configuration.schema: expected '{descriptor.Id}'.");
        }
        ValidateObject(value, descriptor.Fields, "configuration", schemaTag, errors);
        return errors.Count == before;
    }

    /// <summary>Refuses invalid provider configuration before a concrete engine constructs resources.</summary>
    /// <param name="descriptor">The selected configuration descriptor.</param>
    /// <param name="value">The authored configuration.</param>
    /// <exception cref="ArgumentException">The configuration does not satisfy the descriptor.</exception>
    public static void Validate(MachineObjectDescriptor descriptor, JsonElement value) {
        var errors = new List<string>();
        if (!TryValidate(descriptor, value, schemaTag: true, errors)) {
            throw new ArgumentException(string.Join(" ", errors), nameof(value));
        }
    }

    private static void ValidateObject(JsonElement value, IReadOnlyList<MachineFieldDescriptor> fields, string path,
        bool schemaTag, ICollection<string> errors) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) {
            var fieldPath = $"{path}.{property.Name}";
            if (!seen.Add(property.Name)) {
                errors.Add($"{fieldPath}: duplicate field.");
                continue;
            }
            if (schemaTag && property.Name == "schema") {
                continue;
            }
            var field = fields.FirstOrDefault(candidate => candidate.Name == property.Name);
            if (field is null) {
                errors.Add($"{fieldPath}: unknown field.");
                continue;
            }
            ValidateField(property.Value, field, fieldPath, errors);
        }
        foreach (var field in fields) {
            if (field.Required && !seen.Contains(field.Name)) {
                errors.Add($"{path}.{field.Name}: required field is missing.");
            }
        }
    }

    private static void ValidateField(JsonElement value, MachineFieldDescriptor field, string path, ICollection<string> errors) {
        switch (field.Kind) {
            case MachineFieldKind.String when value.ValueKind == JsonValueKind.String:
                var text = value.GetString()!;
                if (field.Role != MachineFieldRole.Value && string.IsNullOrWhiteSpace(text)) {
                    errors.Add($"{path}: expected a non-empty path or name.");
                }
                if (field.Choices is { } choices && !choices.Contains(text, StringComparer.Ordinal)) {
                    errors.Add($"{path}: expected one of {string.Join(", ", choices)}.");
                }
                return;
            case MachineFieldKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return;
            case MachineFieldKind.Integer when value.ValueKind == JsonValueKind.Number:
                decimal integer;
                if (value.TryGetInt64(out var signed)) {
                    integer = signed;
                } else if (value.TryGetUInt64(out var unsigned)) {
                    integer = unsigned;
                } else {
                    errors.Add($"{path}: expected an exact 64-bit integer.");
                    return;
                }
                if (field.Minimum is { } minimum && integer < minimum) {
                    errors.Add($"{path}: value is below {minimum}.");
                }
                if (field.Maximum is { } maximum && integer > maximum) {
                    errors.Add($"{path}: value exceeds {maximum}.");
                }
                return;
            case MachineFieldKind.Object when value.ValueKind == JsonValueKind.Object:
                ValidateObject(value, field.Fields ?? [], path, schemaTag: false, errors);
                return;
            case MachineFieldKind.Array when value.ValueKind == JsonValueKind.Array:
                if (field.Item is not { } item) {
                    errors.Add($"{path}: the installed provider did not describe its array elements.");
                    return;
                }
                var index = 0;
                foreach (var element in value.EnumerateArray()) {
                    ValidateField(element, item, $"{path}[{index++}]", errors);
                }
                return;
            default:
                errors.Add($"{path}: expected {field.Kind.ToString().ToLowerInvariant()}.");
                return;
        }
    }
}
