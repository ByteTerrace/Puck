using System.Text.Json;
using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionSerialization {
    /// <summary>Deserializes an embedded definition and retains its local validation and compiled programs for
    /// immediate installation on the selected host. Cross-document claims remain the load boundary's proof.</summary>
    /// <param name="utf8Json">The canonical embedded document bytes.</param>
    /// <param name="machines">The machine catalog of the receiving host.</param>
    /// <param name="documentDirectory">The directory the document's relative paths resolve beside
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or null for a document with none.</param>
    /// <returns>The admitted definition and its compilation.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">The embedded definition is malformed or refused by the host.</exception>
    public static WorldDefinitionAdmission DeserializeForAdmission(byte[] utf8Json, IMachineValidationCatalog machines, string? documentDirectory = null) {
        ArgumentNullException.ThrowIfNull(argument: utf8Json);
        ArgumentNullException.ThrowIfNull(argument: machines);
        try {
            var definition = ParseEmbedded(documentDirectory: documentDirectory, utf8Json: utf8Json);

            if (!WorldDefinitionValidator.TryAdmitLocally(admission: out var admission, definition: definition,
                machines: machines, reason: out var reason)) {
                throw new InvalidOperationException(message: reason);
            }
            return admission;
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            throw InvalidEmbedded(exception: exception);
        }
    }

    private static WorldDefinition ParseEmbedded(byte[] utf8Json, string? documentDirectory) {
        WorldBootWork.Count(kind: WorldBootWork.Parses);

        var definition = ((JsonSerializer.Deserialize(utf8Json: utf8Json, jsonTypeInfo: WorldJsonContext.Default.WorldDefinition)
            ?? throw new InvalidDataException(message: "the embedded world definition deserialized to null.")) with { DocumentDirectory = documentDirectory });

        if (!WorldStateDocumentValues.TryResolve(definition: definition, reason: out var reason)) {
            throw new InvalidOperationException(message: reason);
        }
        return definition;
    }
    private static InvalidDataException InvalidEmbedded(Exception exception) => new(
        message: $"the embedded world definition is not a valid {WorldDefinition.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}",
        innerException: exception
    );
}
