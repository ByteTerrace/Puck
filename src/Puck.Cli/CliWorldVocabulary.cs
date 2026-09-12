using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.World;
using Puck.World.Client;
using Puck.World.Machines;

namespace Puck.Cli;

/// <summary>Composes the CLI's machine vocabulary through the same extension entry points as runtime hosts.</summary>
public static class CliWorldVocabulary {
    /// <summary>Creates an independent catalog and installs the CLI's schema validation hooks.</summary>
    /// <returns>The immutable catalog selected for this invocation.</returns>
    public static WorldMachineCatalog EnsureInstalled() {
        var registry = new WorldMachineExtensionRegistry();
        new HumbleGamingBrickExtension().Initialize(registry);
        new AdvancedGamingBrickExtension().Initialize(registry);
        var catalog = registry.Build();
        WorldSchemaVocabularyHooks.Install(
            postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
            probeKindCheck: WorldProbeKinds.IsShipped);
        return catalog;
    }
    /// <summary>Computes the stable metadata fingerprint for the selected invocation catalog.</summary>
    public static string Fingerprint(WorldMachineCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.CompositionFingerprint;
    }
}
