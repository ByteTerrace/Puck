using Puck.Abstractions;
using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingDeck.Forge;
using Puck.World;
using Puck.World.Machines;

namespace Puck.Cli;

/// <summary>Composes the CLI's machine vocabulary through the same extension composition as runtime hosts. The CLI's
/// tooling verbs compose its built-in Gaming Brick and Gaming Deck forges only; they discover no installed extensions.</summary>
public static class CliWorldVocabulary {
    // Every verb that reads a world resolves a document with a .puck source to that source, as the game does, and
    // wires the Schema vocabulary hooks before any document load can parse a mask or binding.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void InstallDocumentSource() {
        WorldSchemaVocabularyHooks.Install(probeKindCheck: WorldProbeKinds.IsShipped);
        WorldDefinitionFileSource.UseLocalDocuments(source: Puck.World.Transpiler.Composition.PuckDocumentComposer.Instance);
    }

    /// <summary>Creates an independent catalog and installs the CLI's schema validation hooks.</summary>
    /// <returns>The immutable catalog selected for this invocation.</returns>
    public static WorldMachineCatalog EnsureInstalled() {
        var catalog = WorldMachineCatalog.From(extensions: PuckExtensionSet.Compose(extensions: [
            new AdvancedGamingBrickExtension(),
            new HumbleGamingBrickExtension(),
            new HumbleGamingDeckExtension(),
        ]));

        WorldSchemaVocabularyHooks.Install(probeKindCheck: WorldProbeKinds.IsShipped);
        return catalog;
    }
    /// <summary>Computes the stable metadata fingerprint for the selected invocation catalog.</summary>
    public static string Fingerprint(WorldMachineCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.CompositionFingerprint;
    }
}
