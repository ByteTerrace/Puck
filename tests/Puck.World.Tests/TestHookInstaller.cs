using System.Runtime.CompilerServices;
using Puck.World.Client;

namespace Puck.World.Tests;

/// <summary>
/// This project's wiring of <c>Puck.World.Schema</c>'s composition-root injection seams — the same
/// <see cref="WorldSchemaVocabularyHooks.Install"/> both real roots call, so a law here exercises the real refusals
/// rather than a stand-in that could drift from them. Both extension predicates are permissive: this suite exercises
/// the DOCUMENT/AUTHORITY substrate, never machine boot, so every engine and post-render key a shipped world names is
/// accepted rather than checked against the real registrations (out of scope; see README.md).
/// </summary>
internal static class TestHookInstaller {
    [ModuleInitializer]
    internal static void Install() => WorldSchemaVocabularyHooks.Install(
        postRenderExtensionCheck: static _ => true,
        screenMachineEngineCheck: static _ => true
    );
}
