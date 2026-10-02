using Puck.GamingBricks.Forge;
using Puck.GamingBricks.Transpiler;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Lowering;
using Puck.World;

namespace Puck.Cli.Transpiler;

/// <summary>Provides the shared document vocabulary resolver for CLI commands.</summary>
internal static class CliVocabularyResolver {
    public static DocumentVocabularyResolver Instance { get; } = new(
        vocabularies: new Dictionary<string, IDocumentVocabulary>(comparer: StringComparer.Ordinal) {
            [CartridgeDocument.SchemaId] = CartridgeVocabulary.Instance,
            [WorldDefinition.SchemaVersion] = WorldDocumentVocabulary.Instance,
        },
        fallback: WorldDocumentVocabulary.Instance
    );
}
