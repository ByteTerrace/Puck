using System.Runtime.CompilerServices;
using Puck.World.Client;

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
        var projections = new Puck.Testing.TemporaryDirectory(prefix: "puck-protoref-process-");

        WorldProjectionContent.ConfigureShared(store: new Puck.Assets.ContentAddressedStore(root: projections.RootPath));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => projections.Dispose();
    }
}
