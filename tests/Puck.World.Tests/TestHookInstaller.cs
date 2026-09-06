using System.Runtime.CompilerServices;
using Puck.World.Client;

namespace Puck.World.Tests;

/// <summary>
/// This project's wiring of <c>Puck.World.Schema</c>'s composition-root injection seams — the same
/// <see cref="WorldSchemaVocabularyHooks.Install"/> both real roots call, so a law here exercises the real refusals
/// rather than a stand-in that could drift from them. The engine and post-render predicates are permissive: this
/// suite exercises the document and authority substrate, so every engine and post-render key a shipped world names
/// is accepted rather than checked against the real registrations (see README.md). The cartridge predicate is the
/// real one, since which engine compiles a cartridge document is what the machine-cartridge laws hold.
/// </summary>
internal static class TestHookInstaller {
    [ModuleInitializer]
    internal static void Install() => WorldSchemaVocabularyHooks.Install(
        postRenderExtensionCheck: static _ => true,
        probeKindCheck: static _ => true,
        screenMachineCartridgeCheck: WorldScreenMachineEngines.CompilesCartridges,
        screenMachineEngineCheck: static _ => true
    );
}
