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
    /// <param name="drawKeys">The receiving authority sampler when restoring a checkpoint.</param>
    /// <param name="positions">Installation ordinals from that checkpoint.</param>
    public static WorldDefinitionAdmission DeserializeForAdmission(byte[] utf8Json, IMachineValidationCatalog machines, string? documentDirectory = null, WorldDrawSampler? drawKeys = null, WorldDrawPositions? positions = null) {
        ArgumentNullException.ThrowIfNull(argument: utf8Json);
        ArgumentNullException.ThrowIfNull(argument: machines);
        try {
            var definition = ParseEmbedded(documentDirectory: documentDirectory, utf8Json: utf8Json);

            if (drawKeys is not null) { definition = drawKeys.Install(definition, restore: positions); }
            if (!WorldDefinitionValidator.TryAdmitLocally(admission: out var admission, definition: definition,
                machines: machines, reason: out var reason)) {
                throw new InvalidOperationException(message: reason);
            }
            return admission;
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            throw InvalidEmbedded(exception: exception);
        }
    }

    /// <summary>Deserializes a document another process delivered. It is read as untrusted input
    /// (<see cref="WorldJsonContext.Untrusted"/>), it has no directory, so no path it names is ever opened, and it is
    /// validated as a delivery (<see cref="WorldDefinitionValidator.TryValidateDelivered"/>).</summary>
    /// <param name="utf8Json">The delivered document's bytes.</param>
    /// <param name="held">Runs between the parse and the validation with the parsed document, so content it names by
    /// pin can be fetched before its rows are checked; <see langword="null"/> to fetch nothing.</param>
    /// <returns>The deserialized, validated definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a document this process may present.</exception>
    public static WorldDefinition DeserializeDelivered(byte[] utf8Json, Action<WorldDefinition>? held = null) {
        ArgumentNullException.ThrowIfNull(argument: utf8Json);

        try {
            var definition = ParseEmbedded(context: WorldJsonContext.Untrusted, documentDirectory: null, utf8Json: utf8Json);

            held?.Invoke(obj: definition);
            if (!WorldDefinitionValidator.TryValidateDelivered(definition: definition, reason: out var reason)) {
                throw new InvalidOperationException(message: reason);
            }

            return definition;
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            throw InvalidEmbedded(exception: exception);
        }
    }

    private static WorldDefinition ParseEmbedded(byte[] utf8Json, string? documentDirectory, WorldJsonContext? context = null) {
        WorldBootWork.Count(kind: WorldBootWork.Parses);

        var definition = ((JsonSerializer.Deserialize(utf8Json: utf8Json, jsonTypeInfo: (context ?? WorldJsonContext.Authored).WorldDefinition)
            ?? throw new InvalidDataException(message: "the embedded world definition deserialized to null.")) with { DocumentDirectory = documentDirectory });

        if (!WorldStateDocumentValues.TryResolve(definition: definition, reason: out var reason, resolved: out var resolved)) {
            throw new InvalidOperationException(message: reason);
        }
        return resolved;
    }
    private static InvalidDataException InvalidEmbedded(Exception exception) => new(
        message: $"the embedded world definition is not a valid {WorldDefinition.SchemaVersion} document: {WorldJsonPayload.Reason(exception: exception)}",
        innerException: exception
    );
}
