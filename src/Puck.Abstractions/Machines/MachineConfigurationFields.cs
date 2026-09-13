using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Abstractions.Machines;

/// <summary>A present provider field or array element, with its descriptor and editable JSON value.</summary>
public sealed class MachineConfigurationFieldSite {
    private readonly int m_index;
    private readonly string? m_member;
    private readonly JsonNode m_parent;

    internal MachineConfigurationFieldSite(JsonNode parent, string? member, int index, string path, MachineFieldDescriptor field) {
        m_parent = parent;
        m_member = member;
        m_index = index;
        Path = path;
        Field = field;
    }

    /// <summary>Gets the provider's field description.</summary>
    public MachineFieldDescriptor Field { get; }
    /// <summary>Gets the complete dotted field path, including any array indices.</summary>
    public string Path { get; }
    /// <summary>Gets or replaces the authored value in its owning tree.</summary>
    public JsonNode? Value {
        get => ((m_member is { } member)
            ? ((JsonObject)m_parent)[member]
            : ((JsonArray)m_parent)[m_index]
        );
        set {
            if (m_member is { } member) {
                ((JsonObject)m_parent)[member] = value;
            } else {
                ((JsonArray)m_parent)[m_index] = value;
            }
        }
    }
}
/// <summary>Walks authored provider fields for import rewriting, asset relocation, and resource preparation.</summary>
public static class MachineConfigurationFields {
    private static void AppendDescriptor(StringBuilder text, MachineObjectDescriptor descriptor) {
        AppendPart(
            text: text,
            value: descriptor.Id
        );
        AppendFields(
            text,
            descriptor.Fields,
            ""
        );
    }
    private static void AppendEngineDescriptor(StringBuilder text, MachineEngineDescriptor descriptor) {
        AppendPart(
            text: text,
            value: descriptor.Id
        );
        AppendPart(
            text: text,
            value: descriptor.Configuration.Id
        );
        AppendFields(
            text,
            descriptor.Configuration.Fields,
            ""
        );
    }
    private static void AppendFields(StringBuilder text, IReadOnlyList<MachineFieldDescriptor>? fields,
        string prefix, HashSet<MachineFieldDescriptor>? active = null) {
        active ??= new HashSet<MachineFieldDescriptor>(comparer: ReferenceEqualityComparer.Instance);
        if (fields is null) {
            AppendPart(
                text: text,
                value: prefix
            );
            AppendPart(
                text: text,
                value: "<null>"
            );
            return;
        }

        AppendPart(
            text: text,
            value: prefix
        );
        _ = text.Append(value: fields.Count).Append(value: ';');
        foreach (var field in fields) {
            if (field is null) {
                AppendPart(
                    text: text,
                    value: "<null-field>"
                );
                continue;
            }

            if (!active.Add(item: field)) {
                AppendPart(
                    text: text,
                    value: "<cycle>"
                );
                continue;
            }

            AppendPart(
                text: text,
                value: field.Name
            );
            AppendPart(
                text: text,
                value: field.Kind.ToString()
            );
            AppendPart(
                text: text,
                value: field.Role.ToString()
            );
            AppendPart(
                text: text,
                value: (field.Required
                ? "true"
                : "false")
            );
            AppendPart(
                text: text,
                value: field.Minimum?.ToString(provider: CultureInfo.InvariantCulture)
            );
            AppendPart(
                text: text,
                value: field.Maximum?.ToString(provider: CultureInfo.InvariantCulture)
            );
            if (field.Choices is { } choices) {
                _ = text.Append(value: choices.Count).Append(value: ';');
                foreach (var choice in choices) {
                    AppendPart(
                        text: text,
                        value: choice
                    );
                }
            } else {
                _ = text.Append(value: "-1;");
            }

            AppendFields(
                text,
                field.Fields,
                ((prefix + field.Name) + "."),
                active
            );
            if (field.Item is { } item) {
                AppendFields(
                    text,
                    [item],
                    ((prefix + field.Name) + "[]:"),
                    active
                );
            }

            active.Remove(item: field);
        }
    }
    private static void AppendPart(StringBuilder text, string? value) {
        if (value is null) {
            _ = text.Append(value: "-1:");
            return;
        }

        _ = text.Append(value: value.Length).Append(value: ':').Append(value: value);
    }
    private static void ValidateField(MachineFieldDescriptor field, string path,
        ICollection<string> errors, HashSet<MachineFieldDescriptor> active) {
        if (!Enum.IsDefined(value: field.Kind)) {
            errors.Add(item: (((path + ".kind: unknown machine field kind value ") + ((int)field.Kind)) + "."));
        }

        if (!Enum.IsDefined(value: field.Role)) {
            errors.Add(item: (((path + ".role: unknown machine field role value ") + ((int)field.Role)) + "."));
        }

        if (
            string.IsNullOrWhiteSpace(value: field.Name) ||
            (field.Name.IndexOfAny(anyOf: new[] { '.', '[', ']' }) >= 0)
        ) {
            errors.Add(item: (path + ": field names must be non-empty and cannot contain '.', '[' or ']'."));
        }

        if (string.IsNullOrWhiteSpace(value: field.Description)) {
            errors.Add(item: (path + ": a description is required."));
        }

        if (
            (field.Minimum is { } minimum) &&
            (field.Maximum is { } maximum) &&
            (minimum > maximum)
        ) {
            errors.Add(item: (path + ": minimum cannot exceed maximum."));
        }

        if (field.Kind == MachineFieldKind.Array) {
            if (field.Item is null) {
                errors.Add(item: (path + ": array item metadata is required."));
            }

            if (field.Fields is { Count: > 0 }) {
                errors.Add(item: (path + ": an array cannot carry object fields beside its item metadata."));
            }
        } else {
            if (field.Item is not null) {
                errors.Add(item: (path + ": item metadata is only valid for arrays."));
            }
        }

        if (field.Kind == MachineFieldKind.Object) {
            if (!active.Add(item: field)) {
                errors.Add(item: (path + ": descriptor metadata contains a cycle."));
                return;
            }

            ValidateFields(
                field.Fields,
                (path + ".fields"),
                errors,
                active
            );
            active.Remove(item: field);
        } else if (field.Fields is { Count: > 0 }) {
            errors.Add(item: (path + ": child fields are only valid for objects."));
        }

        if (
            (field.Kind == MachineFieldKind.Array) &&
            (field.Item is { } item)
        ) {
            if (!active.Add(item: field)) {
                errors.Add(item: (path + ": descriptor metadata contains a cycle."));
                return;
            }

            ValidateField(
                active: active,
                errors: errors,
                field: item,
                path: (path + "[]")
            );
            active.Remove(item: field);
        }

        if (
            (field.Kind != MachineFieldKind.String) &&
            (field.Choices is { Count: > 0 })
        ) {
            errors.Add(item: (path + ": choices are only valid for string fields."));
        }

        if (
            (field.Kind != MachineFieldKind.Integer) &&
            ((field.Minimum is not null) || (field.Maximum is not null))
        ) {
            errors.Add(item: (path + ": minimum and maximum are only valid for integer fields."));
        }

        if (
            (field.Role != MachineFieldRole.Value) &&
            (field.Kind != MachineFieldKind.String)
        ) {
            errors.Add(item: (path + ": composition roles require a string field."));
        }
    }
    private static void ValidateFields(IReadOnlyList<MachineFieldDescriptor>? fields, string path,
        ICollection<string> errors, HashSet<MachineFieldDescriptor> active) {
        if (fields is null) {
            errors.Add(item: (path + ": field metadata is required."));
            return;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var field in fields) {
            if (field is null) {
                errors.Add(item: (path + ": field metadata cannot be null."));
                continue;
            }

            if (!names.Add(item: field.Name)) {
                errors.Add(item: (((path + ".") + field.Name) + ": duplicate field name."));
            }

            ValidateField(
                field,
                ((path + ".") + field.Name),
                errors,
                active
            );
        }
    }
    private static void VisitObject(JsonObject obj, IReadOnlyList<MachineFieldDescriptor> fields, string prefix,
        Action<MachineConfigurationFieldSite> visitor) {
        foreach (var field in fields) {
            if (
                !obj.TryGetPropertyValue(
                field.Name,
                out var value
            ) ||
                (value is null)
            ) {
                continue;
            }
            var path = ((prefix.Length == 0)
                ? field.Name
                : $"{prefix}.{field.Name}"
            );

            visitor(new(
                obj,
                field.Name,
                -1,
                path,
                field
            ));
            VisitValue(
                obj[field.Name],
                field,
                path,
                visitor
            );
        }
    }
    private static void VisitValue(JsonNode? value, MachineFieldDescriptor field, string path,
        Action<MachineConfigurationFieldSite> visitor) {
        if (
            (field.Kind == MachineFieldKind.Object) &&
            (value is JsonObject obj)
        ) {
            VisitObject(
                obj,
                (field.Fields ?? []),
                path,
                visitor
            );
        } else if (
            (field.Kind == MachineFieldKind.Array) &&
            (value is JsonArray array) &&
            (field.Item is { } item)
        ) {
            for (var index = 0; (index < array.Count); index++) {
                var itemPath = $"{path}[{index}]";

                visitor(new(
                    field: item,
                    index: index,
                    member: null,
                    parent: array,
                    path: itemPath
                ));
                VisitValue(
                    array[index],
                    item,
                    itemPath,
                    visitor
                );
            }
        }
    }

    /// <summary>Computes a stable fingerprint for the descriptors selected by one host catalog.</summary>
    /// <param name="descriptors">Engine identifiers paired with their descriptors; null means unavailable.</param>
    /// <returns>A lowercase SHA-256 fingerprint independent of registration order.</returns>
    public static string CatalogFingerprint(IEnumerable<(string EngineId, MachineEngineDescriptor? Descriptor)> descriptors) {
        ArgumentNullException.ThrowIfNull(descriptors);
        var text = new StringBuilder();

        foreach (var (engineId, descriptor) in descriptors.OrderBy(
            static item => item.EngineId,
            StringComparer.Ordinal
        )) {
            AppendPart(
                text: text,
                value: engineId
            );
            if (descriptor is null) {
                AppendPart(
                    text: text,
                    value: "<unavailable>"
                );
            } else {
                AppendEngineDescriptor(
                    descriptor: descriptor,
                    text: text
                );
            }
        }
        return Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: text.ToString()))).ToLowerInvariant();
    }
    /// <summary>Computes a stable fingerprint of the metadata that affects configuration composition.</summary>
    /// <param name="descriptor">The descriptor whose field names, roles, shapes, and constraints are included.</param>
    /// <returns>A lowercase SHA-256 fingerprint.</returns>
    public static string DescriptorFingerprint(MachineObjectDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);
        var text = new StringBuilder();

        AppendDescriptor(
            descriptor: descriptor,
            text: text
        );
        return Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: text.ToString()))).ToLowerInvariant();
    }
    /// <summary>Checks a provider object descriptor before it is used by validation, schema tooling, or composition.</summary>
    /// <param name="descriptor">The provider-owned object descriptor.</param>
    /// <param name="errors">Receives precise descriptor paths and structural errors.</param>
    /// <returns><see langword="true"/> when the descriptor is structurally usable.</returns>
    public static bool TryValidateDescriptor(MachineObjectDescriptor descriptor, ICollection<string> errors) {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(errors);
        var before = errors.Count;

        if (string.IsNullOrWhiteSpace(value: descriptor.Id)) {
            errors.Add(item: "descriptor.id: a non-empty schema identifier is required.");
        }
        ValidateFields(
            descriptor.Fields,
            "descriptor.fields",
            errors,
            new HashSet<MachineFieldDescriptor>(comparer: ReferenceEqualityComparer.Instance)
        );
        return (errors.Count == before);
    }
    /// <summary>Visits present fields using the installed descriptor. The visitor may replace a member's value.
    /// Field paths are dotted, with array indices in brackets; they are also the keys of prepared assets.</summary>
    /// <param name="configuration">The provider configuration object.</param>
    /// <param name="descriptor">The selected field description.</param>
    /// <param name="visitor">Receives each editable field or array-element site.</param>
    public static void Visit(JsonObject configuration, MachineObjectDescriptor descriptor,
        Action<MachineConfigurationFieldSite> visitor) {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(visitor);
        VisitObject(
            configuration,
            descriptor.Fields,
            string.Empty,
            visitor
        );
    }
}
