using System.Text.Json.Nodes;

namespace Puck.Abstractions.Machines;

/// <summary>A present provider field or array element, with its descriptor and editable JSON value.</summary>
public sealed class MachineConfigurationFieldSite {
    private readonly JsonNode m_parent;
    private readonly string? m_member;
    private readonly int m_index;

    internal MachineConfigurationFieldSite(JsonNode parent, string? member, int index, string path, MachineFieldDescriptor field) {
        m_parent = parent;
        m_member = member;
        m_index = index;
        Path = path;
        Field = field;
    }

    /// <summary>Gets the complete dotted field path, including any array indices.</summary>
    public string Path { get; }
    /// <summary>Gets the provider's field description.</summary>
    public MachineFieldDescriptor Field { get; }
    /// <summary>Gets or replaces the authored value in its owning tree.</summary>
    public JsonNode? Value {
        get => m_member is { } member ? ((JsonObject)m_parent)[member] : ((JsonArray)m_parent)[m_index];
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
        VisitObject(configuration, descriptor.Fields, string.Empty, visitor);
    }

    private static void VisitObject(JsonObject obj, IReadOnlyList<MachineFieldDescriptor> fields, string prefix,
        Action<MachineConfigurationFieldSite> visitor) {
        foreach (var field in fields) {
            if (!obj.TryGetPropertyValue(field.Name, out var value) || value is null) {
                continue;
            }
            var path = prefix.Length == 0 ? field.Name : $"{prefix}.{field.Name}";
            visitor(new(obj, field.Name, -1, path, field));
            VisitValue(obj[field.Name], field, path, visitor);
        }
    }

    private static void VisitValue(JsonNode? value, MachineFieldDescriptor field, string path,
        Action<MachineConfigurationFieldSite> visitor) {
        if (field.Kind == MachineFieldKind.Object && value is JsonObject obj) {
            VisitObject(obj, field.Fields ?? [], path, visitor);
        } else if (field.Kind == MachineFieldKind.Array && value is JsonArray array && field.Item is { } item) {
            for (var index = 0; index < array.Count; index++) {
                var itemPath = $"{path}[{index}]";
                visitor(new(array, null, index, itemPath, item));
                VisitValue(array[index], item, itemPath, visitor);
            }
        }
    }
}
