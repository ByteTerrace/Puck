using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Canonical (de)serialization and file loading for the silo document (<c>puck.silo.def.v1</c>).</summary>
public static class WorldSiloDefinitionSerialization {
    /// <summary>Serializes a silo document to its canonical UTF-8 bytes (no BOM, LF newlines, one trailing
    /// newline).</summary>
    /// <param name="definition">The document to serialize.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize(WorldSiloDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return CanonicalJsonDocument.Serialize(
            jsonTypeInfo: WorldJsonContext.Default.WorldSiloDefinition,
            value: definition
        );
    }
    /// <summary>Loads and validates a silo document from a file path.</summary>
    /// <param name="path">The document path.</param>
    /// <param name="definition">The loaded, validated document, on success.</param>
    /// <param name="reason">Why the load failed, on failure.</param>
    /// <returns><see langword="true"/> when the document parsed and validated.</returns>
    public static bool TryLoadFile(string path, out WorldSiloDefinition? definition, out string reason) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            definition = null;
            reason = "a silo document path is required";

            return false;
        }

        if (!File.Exists(path: path)) {
            definition = null;
            reason = $"no silo document at '{path}'";

            return false;
        }

        var json = File.ReadAllText(path: path);

        if (!WorldJsonPayload.TryParse(
            error: out reason,
            info: WorldJsonContext.Default.WorldSiloDefinition,
            json: json,
            value: out var parsed
        )) {
            definition = null;
            reason = $"'{path}' is not a valid {WorldSiloDefinition.SchemaVersion} document: {reason}";

            return false;
        }

        if (!WorldSiloDefinitionValidator.TryValidate(
            definition: parsed,
            reason: out reason
        )) {
            definition = null;
            reason = $"'{path}' refused: {reason}";

            return false;
        }

        definition = parsed;
        reason = string.Empty;

        return true;
    }
}
