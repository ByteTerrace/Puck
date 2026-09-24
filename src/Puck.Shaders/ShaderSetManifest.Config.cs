using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Shaders;

public sealed partial record ShaderSetManifest {
    /// <summary>Binds a document's <c>config</c> for this set, or throws with the id and the refusing field.</summary>
    /// <param name="config">The authored configuration, or <see langword="null"/> when the document supplied none.</param>
    /// <returns>The bound values, every absent field at its default.</returns>
    /// <exception cref="InvalidOperationException">The configuration is invalid.</exception>
    public ShaderConfigValues BindConfig(JsonElement? config) {
        return (TryBindConfig(
            config: config,
            reason: out var reason,
            values: out var values
        )
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
        ShaderConfigBinding.JsonSchema(
            schema: Config,
            description: Description
        );
    /// <summary>Validates a document's <c>config</c> against this set's config schema and resolves every absent field
    /// to its default: an unknown property, a value of the wrong shape or type, a component outside its inclusive
    /// range, or a missing field without a default refuses, naming the field.</summary>
    /// <param name="config">The authored configuration, or <see langword="null"/> when the document supplied none.</param>
    /// <param name="values">The bound values, set only when this returns <see langword="true"/>.</param>
    /// <param name="reason">The refusal reason naming the field, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="config"/> is valid.</returns>
    public bool TryBindConfig(JsonElement? config, out ShaderConfigValues values, out string reason) =>
        ShaderConfigBinding.TryBind(
            schema: Config,
            config: config,
            ownerName: Name,
            values: out values,
            reason: out reason
        );

    private void ValidateConfigSchema() =>
        ShaderConfigBinding.ValidateSchema(
            schema: Config,
            ownerName: Name
        );
}
