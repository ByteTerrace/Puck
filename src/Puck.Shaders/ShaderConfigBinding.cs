using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;


namespace Puck.Shaders;

/// <summary>
/// The config-schema binder every <c>puck.*.v1</c> manifest with a name → <see cref="ShaderConfigField"/>
/// <c>config</c> block shares: validates a document's authored configuration, resolves every absent field to its
/// default, and emits the schema as JSON Schema. A render graph package's config (<see cref="RenderGraphPackage.Config"/>),
/// a document pass's and a <see cref="ProbeKindManifest"/>'s all bind through these rules, never a second copy.
/// </summary>
public static class ShaderConfigBinding {
    private static string Describe(ShaderConfigField field) {
        var shape = ((field.Type.ComponentCount() == 1)
            ? (field.Type.ScalarKind() switch {
                ShaderScalarKind.Float => "a number",
                ShaderScalarKind.Uint => "a non-negative integer",
                _ => "an integer",
            })
            : $"an array of {field.Type.ComponentCount()} {((field.Type.ScalarKind() == ShaderScalarKind.Float)
                ? "numbers"
                : "integers")}"
        );

        return (field.Min, field.Max) switch {
            ( { } min, { } max) => $"{shape} in [{Format(value: min)}, {Format(value: max)}]",
            ( { } min, null) => $"{shape} greater than or equal to {Format(value: min)}",
            (null, { } max) => $"{shape} less than or equal to {Format(value: max)}",
            _ => shape,
        };
    }
    private static JsonObject FieldJsonSchema(ShaderConfigField field) {
        var kind = field.Type.ScalarKind();
        var component = new JsonObject {
            ["type"] = ((kind == ShaderScalarKind.Float)
            ? "number"
            : "integer"),
        };

        var (floor, ceiling) = kind switch {
            ShaderScalarKind.Uint => (((double?)uint.MinValue), ((double?)uint.MaxValue)),
            ShaderScalarKind.Int => (((double?)int.MinValue), ((double?)int.MaxValue)),
            _ => (((double?)null), ((double?)null)),
        };
        var minimum = ((field.Min, floor) switch {
            ( { } a, { } b) => Math.Max(
            val1: a,
            val2: b
        ),
            ( { } a, null) => a,
            (null, var b) => b
        });
        var maximum = ((field.Max, ceiling) switch {
            ( { } a, { } b) => Math.Min(
            val1: a,
            val2: b
        ),
            ( { } a, null) => a,
            (null, var b) => b
        });

        if (minimum is { } min) {
            component["minimum"] = min;
        }
        if (maximum is { } max) {
            component["maximum"] = max;
        }

        JsonObject fieldSchema;

        if (field.Type.ComponentCount() == 1) {
            fieldSchema = component;
        } else {
            fieldSchema = new JsonObject {
                ["type"] = "array",
                ["items"] = component,
                ["minItems"] = field.Type.ComponentCount(),
                ["maxItems"] = field.Type.ComponentCount(),
            };
        }

        if (field.Default is { } defaultValue) {
            fieldSchema["default"] = JsonNode.Parse(json: defaultValue.GetRawText());
        }
        if (field.Description is not null) {
            fieldSchema["description"] = field.Description;
        }

        return fieldSchema;
    }
    private static string Format(double value) => value.ToString(provider: CultureInfo.InvariantCulture);
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

                BinaryPrimitives.WriteSingleLittleEndian(
                    destination: destination,
                    value: single
                );
                numeric = single;

                return true;
            case ShaderScalarKind.Uint:
                if (!element.TryGetUInt32(value: out var unsigned)) {
                    return false;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination: destination,
                    value: unsigned
                );
                numeric = unsigned;

                return true;
            default:
                if (!element.TryGetInt32(value: out var signed)) {
                    return false;
                }

                BinaryPrimitives.WriteInt32LittleEndian(
                    destination: destination,
                    value: signed
                );
                numeric = signed;

                return true;
        }
    }
    private static bool TryReadValue(ShaderConfigField field, JsonElement element, string name, out byte[] bytes, out string reason) {
        var count = ((int)field.Type.ComponentCount());

        bytes = new byte[field.Type.SizeBytes()];
        reason = $"'{name}' must be {Describe(field: field)}.";

        if (count == 1) {
            return (
                TryReadComponent(
                destination: bytes,
                element: element,
                field: field,
                numeric: out var numeric
            ) &&
                InRange(
                field: field,
                value: numeric
            )
            );
        }
        if (
            (element.ValueKind != JsonValueKind.Array) ||
            (element.GetArrayLength() != count)
        ) {
            return false;
        }

        var index = 0;

        foreach (var component in element.EnumerateArray()) {
            var destination = bytes.AsSpan(
                length: ((int)ShaderValueTypes.ComponentBytes),
                start: (index * ((int)ShaderValueTypes.ComponentBytes))
            );

            if (
                !TryReadComponent(
                destination: destination,
                element: component,
                field: field,
                numeric: out var numeric
            ) ||
                !InRange(
                field: field,
                value: numeric
            )
            ) {
                return false;
            }

            index++;
        }

        return true;
    }

    /// <summary>Determines whether a component value lies inside a field's inclusive declared range; a field with
    /// no declared bound on a side admits any value on that side.</summary>
    /// <param name="field">The config field.</param>
    /// <param name="value">The component value.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is admitted.</returns>
    public static bool InRange(ShaderConfigField field, double value) {
        ArgumentNullException.ThrowIfNull(argument: field);

        return (
            ((field.Min is not { } min) || (value >= min)) &&
            ((field.Max is not { } max) || (value <= max))
        );
    }
    /// <summary>Emits a config schema as a JSON Schema object: one property per field with its type, inclusive
    /// range, default, and description; every field without a default is required; no additional properties;
    /// <see langword="null"/> admitted (a schema with every field defaulted takes an absent config).</summary>
    /// <param name="schema">The config schema, or <see langword="null"/> for none.</param>
    /// <param name="description">The owner's description, carried onto the schema object.</param>
    /// <returns>The schema node.</returns>
    public static JsonObject JsonSchema(IReadOnlyDictionary<string, ShaderConfigField>? schema, string? description) {
        var properties = new JsonObject();
        var required = new JsonArray();

        if (schema is not null) {
            foreach (var (name, field) in schema) {
                properties[name] = FieldJsonSchema(field: field);

                if (field.Default is null) {
                    required.Add(item: ((JsonNode)JsonValue.Create(value: name)));
                }
            }
        }

        var node = new JsonObject {
            ["type"] = new JsonArray(
            "object",
            "null"
        ),
        };

        if (description is not null) {
            node["description"] = description;
        }

        node["properties"] = properties;

        if (required.Count > 0) {
            node["required"] = required;
        }

        node["additionalProperties"] = false;

        return node;
    }
    /// <summary>Validates a document's <c>config</c> against a schema and resolves every absent field to its
    /// default: an unknown property, a value of the wrong shape or type, a component outside its inclusive range, or a
    /// missing field without a default refuses, naming the field.</summary>
    /// <param name="schema">The config schema, or <see langword="null"/> for none.</param>
    /// <param name="config">The authored configuration, or <see langword="null"/> when the document supplied none.</param>
    /// <param name="ownerName">The owner's id, named in the "not a config field of" refusal.</param>
    /// <param name="values">The bound values, set only when this returns <see langword="true"/>.</param>
    /// <param name="reason">The refusal reason naming the field, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="config"/> is valid.</returns>
    public static bool TryBind(IReadOnlyDictionary<string, ShaderConfigField>? schema, JsonElement? config, string ownerName, out ShaderConfigValues values, out string reason) {
        values = ShaderConfigValues.Empty;
        reason = "";

        var bound = new Dictionary<string, ShaderConfigValue>(comparer: StringComparer.Ordinal);
        var authored = (((config is { } supplied) && (supplied.ValueKind != JsonValueKind.Null))
            ? supplied
            : ((JsonElement?)null)
        );

        if (authored is { } present) {
            if (present.ValueKind != JsonValueKind.Object) {
                reason = "config must be an object.";

                return false;
            }

            foreach (var property in present.EnumerateObject()) {
                if (
                    (schema is null) ||
                    !schema.ContainsKey(key: property.Name)
                ) {
                    reason = $"'{property.Name}' is not a config field of '{ownerName}'.";

                    return false;
                }
            }
        }

        if (schema is not null) {
            foreach (var (name, field) in schema) {
                JsonElement? value = null;

                if (
                    (authored is { } source) &&
                    source.TryGetProperty(
                    propertyName: name,
                    value: out var authoredValue
                )
                ) {
                    value = authoredValue;
                } else if (field.Default is { } defaultValue) {
                    value = defaultValue;
                }

                if (value is not { } element) {
                    reason = $"'{name}' is required.";

                    return false;
                }
                if (!TryReadValue(
                    bytes: out var bytes,
                    element: element,
                    field: field,
                    name: name,
                    reason: out reason
                )) {
                    return false;
                }

                bound[name] = new ShaderConfigValue(
                    Type: field.Type,
                    Bytes: bytes
                );
            }
        }


        values = new ShaderConfigValues(values: bound);

        return true;
    }
    /// <summary>Refuses a schema whose own declaration is malformed: an empty field name, a min above a max, or a
    /// default outside the field's own range.</summary>
    /// <param name="schema">The config schema, or <see langword="null"/> for none.</param>
    /// <param name="ownerName">The owner's id, named in the refusal.</param>
    /// <exception cref="InvalidDataException">The schema is malformed.</exception>
    public static void ValidateSchema(IReadOnlyDictionary<string, ShaderConfigField>? schema, string ownerName) {
        if (schema is null) {
            return;
        }

        foreach (var (name, field) in schema) {
            if (string.IsNullOrEmpty(value: name)) {
                throw new InvalidDataException(message: $"'{ownerName}' manifest declares a config field with an empty name.");
            }
            if (
                (field.Min is { } min) &&
                (field.Max is { } max) &&
                (min > max)
            ) {
                throw new InvalidDataException(message: $"'{ownerName}' manifest's config field '{name}' has min {Format(value: min)} above max {Format(value: max)}.");
            }
            if (
                (field.Default is { } defaultValue) &&
                !TryReadValue(
                bytes: out _,
                element: defaultValue,
                field: field,
                name: name,
                reason: out var reason
            )
            ) {
                throw new InvalidDataException(message: $"'{ownerName}' manifest's config field '{name}' has a default outside its own schema: {reason}");
            }
        }
    }
}
