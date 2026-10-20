using System.Runtime.CompilerServices;
using Puck.World.Transpiler.Composition;

namespace Puck.World;

/// <summary>
/// Wires every <c>Puck.World.Schema</c> injection seam the instant this assembly loads — before <c>Main</c>, before
/// the DI container, before any validator can run. Machine catalogs are supplied explicitly by the host; this wires
/// <see cref="WorldSchemaVocabularyHooks.Install"/>, and installs the transpiler's document composer as the source
/// every local load resolves a basis or an import through, so a document with a <c>.puck</c> source boots from it.
/// </summary>
internal static class WorldDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        WorldSchemaVocabularyHooks.Install(probeKindCheck: WorldProbeKinds.IsShipped);
        // The World validates every document again against its own registry once it is built, so before then a verb
        // the catalog lacks is the registry's to judge rather than a refusal.
        WorldAffordances.DeferUnknownVerbsToRegistry = true;
        WorldDefinitionFileSource.UseLocalDocuments(source: PuckDocumentComposer.Instance);
    }
}
