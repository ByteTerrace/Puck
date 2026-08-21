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
    public JsonObject ConfigJsonSchema() =>
        ShaderConfigBinding.JsonSchema(schema: Config, description: Description, quantizeRateFields: QuantizeRateConfigFields());
    /// <summary>Validates a document's <c>config</c> against this set's config schema and resolves every absent field
    /// to its default: an unknown property, a value of the wrong shape or type, a component outside its inclusive
    /// range, a missing field without a default, or a tick-quantization rate that does not divide
    /// <see cref="EngineTicks.PerSecond"/> refuses, naming the field.</summary>
    /// <param name="config">The authored configuration, or <see langword="null"/> when the document supplied none.</param>
    /// <param name="values">The bound values, set only when this returns <see langword="true"/>.</param>
    /// <param name="reason">The refusal reason naming the field, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="config"/> is valid.</returns>
    public bool TryBindConfig(JsonElement? config, out ShaderConfigValues values, out string reason) =>
        ShaderConfigBinding.TryBind(schema: Config, config: config, ownerName: Name, values: out values, reason: out reason, quantizeRateFields: QuantizeRateConfigFields());

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
    private void ValidateConfigSchema() =>
        ShaderConfigBinding.ValidateSchema(schema: Config, ownerName: Name);
}
