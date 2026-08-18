using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial record ShaderSetManifest {
    /// <summary>Binds a document's <c>config</c> for this set, or throws with the id and the refusing field.</summary>
    /// <param name="config">The authored configuration, or <see langword="null"/> when the document supplied none.</param>
    /// <returns>The bound values, every absent field at its default.</returns>
    /// <exception cref="InvalidOperationException">The configuration is invalid.</exception>
    public ShaderConfigValues BindConfig(JsonElement? config) {
        return (TryBindConfig(config: config, reason: out var reason, values: out var values)
            ? values
            : throw new InvalidOperationException(message: $"'{Name}' config is invalid: {reason}")
        );
    }
    /// <summary>Emits this set's config schema as a JSON Schema object for a document's own schema to embed: one
    /// property per config field with its type, inclusive range, default, and description; every field without a
    /// default is required; no additional properties; <see langword="null"/> admitted (a set with every field
    /// defaulted takes an absent config).</summary>
    /// <returns>The schema node.</returns>
    public JsonObject ConfigJsonSchema() {
        var properties = new JsonObject();
        var required = new JsonArray();
        var quantizeFields = QuantizeRateConfigFields();

        if (Config is not null) {
            foreach (var (name, field) in Config) {
                properties[name] = FieldJsonSchema(field: field, quantizesTick: quantizeFields.Contains(item: name));

                if (field.Default is null) {
                    required.Add(item: ((JsonNode)JsonValue.Create(value: name)));
                }
            }
        }

        var schema = new JsonObject {
            ["type"] = new JsonArray("object", "null"),
        };

        if (Description is not null) {
            schema["description"] = Description;
        }

        schema["properties"] = properties;

        if (required.Count > 0) {
            schema["required"] = required;
        }

        schema["additionalProperties"] = false;

        return schema;
    }
    /// <summary>Validates a document's <c>config</c> against this set's config schema and resolves every absent field
    /// to its default: an unknown property, a value of the wrong shape or type, a component outside its inclusive
    /// range, a missing field without a default, or a tick-quantization rate that does not divide
    /// <see cref="EngineTicks.PerSecond"/> refuses, naming the field.</summary>
    /// <param name="config">The authored configuration, or <see langword="null"/> when the document supplied none.</param>
    /// <param name="values">The bound values, set only when this returns <see langword="true"/>.</param>
    /// <param name="reason">The refusal reason naming the field, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="config"/> is valid.</returns>
    public bool TryBindConfig(JsonElement? config, out ShaderConfigValues values, out string reason) {
        values = ShaderConfigValues.Empty;
        reason = "";

        var bound = new Dictionary<string, ShaderConfigValue>(comparer: StringComparer.Ordinal);
        var authored = (((config is { } supplied) && (supplied.ValueKind != JsonValueKind.Null)) ? supplied : ((JsonElement?)null));

        if (authored is { } present) {
            if (present.ValueKind != JsonValueKind.Object) {
                reason = "config must be an object.";

                return false;
            }

            foreach (var property in present.EnumerateObject()) {
                if ((Config is null) || !Config.ContainsKey(key: property.Name)) {
                    reason = $"'{property.Name}' is not a config field of '{Name}'.";

                    return false;
                }
            }
        }

        if (Config is not null) {
            foreach (var (name, field) in Config) {
                JsonElement? value = null;

                if ((authored is { } source) && source.TryGetProperty(propertyName: name, value: out var authoredValue)) {
                    value = authoredValue;
                } else if (field.Default is { } defaultValue) {
                    value = defaultValue;
                }

                if (value is not { } element) {
                    reason = $"'{name}' is required.";

                    return false;
                }
                if (!TryReadValue(bytes: out var bytes, element: element, field: field, name: name, reason: out reason)) {
                    return false;
                }

                bound[name] = new ShaderConfigValue(Type: field.Type, Bytes: bytes);
            }
        }

        foreach (var name in QuantizeRateConfigFields()) {
            var hz = bound[name].ComponentBits(index: 0);

            if (!ShaderPushConstantLayout.DividesTickRate(hz: hz)) {
                reason = $"'{name}' must be a positive integer that divides {EngineTicks.PerSecond} exactly.";

                return false;
            }
        }

        values = new ShaderConfigValues(values: bound);

        return true;
    }

    private static string Describe(ShaderConfigField field) {
        var shape = ((field.Type.ComponentCount() == 1)
            ? (field.Type.ScalarKind() switch {
                ShaderScalarKind.Float => "a number",
                ShaderScalarKind.Uint => "a non-negative integer",
                _ => "an integer",
            })
            : $"an array of {field.Type.ComponentCount()} {((field.Type.ScalarKind() == ShaderScalarKind.Float) ? "numbers" : "integers")}");

        return (field.Min, field.Max) switch {
            ( { } min, { } max) => $"{shape} in [{Format(value: min)}, {Format(value: max)}]",
            ( { } min, null) => $"{shape} greater than or equal to {Format(value: min)}",
            (null, { } max) => $"{shape} less than or equal to {Format(value: max)}",
            _ => shape,
        };
    }
    private static JsonObject FieldJsonSchema(ShaderConfigField field, bool quantizesTick) {
        var kind = field.Type.ScalarKind();
        var component = new JsonObject {
            ["type"] = ((kind == ShaderScalarKind.Float) ? "number" : "integer"),
        };

        if (quantizesTick) {
            var divisors = new JsonArray();

            for (var hz = 1u; (hz <= EngineTicks.PerSecond); hz++) {
                if (ShaderPushConstantLayout.DividesTickRate(hz: hz) && InRange(field: field, value: hz)) {
                    divisors.Add(item: ((JsonNode)JsonValue.Create(value: hz)));
                }
            }

            component["enum"] = divisors;
        } else {
            var (floor, ceiling) = kind switch {
                ShaderScalarKind.Uint => (((double?)uint.MinValue), ((double?)uint.MaxValue)),
                ShaderScalarKind.Int => (((double?)int.MinValue), ((double?)int.MaxValue)),
                _ => (((double?)null), ((double?)null)),
            };
            var minimum = ((field.Min, floor) switch { ( { } a, { } b) => Math.Max(val1: a, val2: b), ( { } a, null) => a, (null, var b) => b });
            var maximum = ((field.Max, ceiling) switch { ( { } a, { } b) => Math.Min(val1: a, val2: b), ( { } a, null) => a, (null, var b) => b });

            if (minimum is { } min) {
                component["minimum"] = min;
            }
            if (maximum is { } max) {
                component["maximum"] = max;
            }
        }

        JsonObject schema;

        if (field.Type.ComponentCount() == 1) {
            schema = component;
        } else {
            schema = new JsonObject {
                ["type"] = "array",
                ["items"] = component,
                ["minItems"] = field.Type.ComponentCount(),
                ["maxItems"] = field.Type.ComponentCount(),
            };
        }

        if (field.Default is { } defaultValue) {
            schema["default"] = JsonNode.Parse(json: defaultValue.GetRawText());
        }
        if (field.Description is not null) {
            schema["description"] = field.Description;
        }

        return schema;
    }
    private static string Format(double value) => value.ToString(provider: CultureInfo.InvariantCulture);
    private static bool InRange(ShaderConfigField field, double value) =>
        (((field.Min is not { } min) || (value >= min)) && ((field.Max is not { } max) || (value <= max)));
    private static bool TryReadComponent(ShaderConfigField field, JsonElement element, Span<byte> destination, out double numeric) {
        numeric = 0;

        if (element.ValueKind != JsonValueKind.Number) {
            return false;
        }

        switch (field.Type.ScalarKind()) {
            case ShaderScalarKind.Float:
                if (!element.TryGetSingle(value: out var single)) {
                    return false;
                }

                BinaryPrimitives.WriteSingleLittleEndian(destination: destination, value: single);
                numeric = single;

                return true;
            case ShaderScalarKind.Uint:
                if (!element.TryGetUInt32(value: out var unsigned)) {
                    return false;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(destination: destination, value: unsigned);
                numeric = unsigned;

                return true;
            default:
                if (!element.TryGetInt32(value: out var signed)) {
                    return false;
                }

                BinaryPrimitives.WriteInt32LittleEndian(destination: destination, value: signed);
                numeric = signed;

                return true;
        }
    }
    private static bool TryReadValue(ShaderConfigField field, JsonElement element, string name, out byte[] bytes, out string reason) {
        var count = ((int)field.Type.ComponentCount());

        bytes = new byte[field.Type.SizeBytes()];
        reason = $"'{name}' must be {Describe(field: field)}.";

        if (count == 1) {
            return (TryReadComponent(destination: bytes, element: element, field: field, numeric: out var numeric) && InRange(field: field, value: numeric));
        }
        if ((element.ValueKind != JsonValueKind.Array) || (element.GetArrayLength() != count)) {
            return false;
        }

        var index = 0;

        foreach (var component in element.EnumerateArray()) {
            var destination = bytes.AsSpan(length: ((int)ShaderValueTypes.ComponentBytes), start: (index * ((int)ShaderValueTypes.ComponentBytes)));

            if (!TryReadComponent(destination: destination, element: component, field: field, numeric: out var numeric) || !InRange(field: field, value: numeric)) {
                return false;
            }

            index++;
        }

        return true;
    }
    private HashSet<string> QuantizeRateConfigFields() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (PushConstantLayout is { } layout) {
            foreach (var slot in layout.Slots) {
                if (slot.QuantizeHzConfigField is { } name) {
                    names.Add(item: name);
                }
            }
        }

        return names;
    }
    private void ValidateConfigSchema() {
        if (Config is null) {
            return;
        }

        foreach (var (name, field) in Config) {
            if (string.IsNullOrEmpty(value: name)) {
                throw new InvalidDataException(message: $"'{Name}' manifest declares a config field with an empty name.");
            }
            if ((field.Min is { } min) && (field.Max is { } max) && (min > max)) {
                throw new InvalidDataException(message: $"'{Name}' manifest's config field '{name}' has min {Format(value: min)} above max {Format(value: max)}.");
            }
            if ((field.Default is { } defaultValue) && !TryReadValue(bytes: out _, element: defaultValue, field: field, name: name, reason: out var reason)) {
                throw new InvalidDataException(message: $"'{Name}' manifest's config field '{name}' has a default outside its own schema: {reason}");
            }
        }
    }
}
