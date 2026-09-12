using System.Runtime.CompilerServices;
using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.World.Client;
using Puck.World.Machines;

namespace Puck.World.Tests;

/// <summary>
/// This project's wiring of <c>Puck.World.Schema</c>'s composition-root injection seams — the same
/// <see cref="WorldSchemaVocabularyHooks.Install"/> both real roots call, so a law here exercises the real refusals
/// rather than a stand-in that could drift from them.
/// </summary>
internal static class TestHookInstaller {
    internal static WorldMachineCatalog CreateMachineCatalog() {
        var registry = new WorldMachineExtensionRegistry();
        new HumbleGamingBrickExtension().Initialize(registry);
        new AdvancedGamingBrickExtension().Initialize(registry);
        return registry.Build();
    }

    [ModuleInitializer]
    internal static void Install() {
        WorldSchemaVocabularyHooks.Install(
            postRenderExtensionCheck: static _ => true,
            probeKindCheck: static _ => true
        );
    }
}
