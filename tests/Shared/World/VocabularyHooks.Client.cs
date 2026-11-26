using System.Runtime.CompilerServices;

namespace Puck.World.Testing;

/// <summary>
/// The wiring of <c>Puck.World.Schema</c>'s composition-root injection seams for a suite that carries the client: the
/// same <see cref="WorldSchemaVocabularyHooks.Install"/> both real roots call, so a law here exercises the real
/// refusals rather than a stand-in that could drift from them, and the process-wide projection content store. A suite
/// below the client links <c>VocabularyHooks.Server.cs</c> instead.
/// </summary>
internal static class VocabularyHooks {
    [ModuleInitializer]
    internal static void Install() {
        WorldSchemaVocabularyHooks.Install(probeKindCheck: static _ => true);
        // Best effort: a process that fetched content from another authority holds that authority's store until it exits
        // (WorldProjectionContent.AuthorityStore), so the directory cannot be proven empty of owners from inside it.
        var projections = new Puck.Testing.TemporaryDirectory(bestEffortDelete: true, prefix: "puck-protoref-process-");

        WorldProjectionContent.ConfigureShared(store: new Puck.Assets.ContentAddressedStore(root: projections.RootPath));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => projections.Dispose();
    }
}
