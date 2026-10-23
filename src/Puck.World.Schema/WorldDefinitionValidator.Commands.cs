namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The document-local half compiles every row (names, parameters, defaults, line templates) and refuses by row; the
    // registry half, which needs the host's verbs, runs through AuthoredCommandVocabularyHook once a registry exists
    // and again in TryCompleteAdmission for a document validated before it did.
    private static void ValidateCommands(WorldDefinition definition, List<string> errors) {
        if (definition.CommandsRaw is not { Count: > 0 }) {
            return;
        }

        var set = WorldConsoleCommands.Compile(
            definition: definition,
            errors: errors
        );

        AuthoredCommandVocabularyHook.Check?.Invoke(arg1: set, arg2: errors);
    }
}
