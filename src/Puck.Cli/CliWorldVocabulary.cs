using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.World;
using Puck.World.Client;
using Puck.World.Machines;

namespace Puck.Cli;

/// <summary>Wires the CLI process's one copy of every <c>Puck.World.Schema</c> vocabulary hook, registering the
/// shipped screen-machine engines through the same <see cref="Puck.GamingBricks.Forge.IGamingBrickExtension"/>
/// entry points the game's dynamic extension loader (<see cref="WorldMachineExtensionLoader"/>) calls into: a
/// <see cref="WorldMachineExtensionRegistry"/> forwards <c>RegisterEngine</c>/<c>RegisterCompiler</c> calls straight
/// into the same <see cref="WorldScreenMachineEngines"/> static registry that the game's own
/// <c>Puck.World.WorldDataHookInstaller</c> module initializer populates. The CLI ships no <c>extensions/</c>
/// directory of its own to scan (<see cref="WorldMachineExtensionLoader.LoadFromDirectory(string, Action{string}?)"/>
/// would find nothing), so it instantiates the two shipped extensions directly and feeds them that registry instead
/// — the same single registration path, entered without a filesystem round trip.</summary>
/// <remarks><see cref="EnsureInstalled"/> is the CLI's one vocabulary-hook installer. Every verb that parses,
/// validates, or composes a world document — <c>compile --validate</c>, <c>lint</c>, <c>world prepare</c> — calls
/// it in place of keeping a private copy (see
/// <see cref="Puck.World.Client.WorldSchemaVocabularyHooks.Install"/>'s remarks on why every composition root must
/// wire the identical hooks).</remarks>
public static class CliWorldVocabulary {
    private static readonly Lock s_gate = new();
    private static bool s_installed;

    /// <summary>Registers the shipped gaming-brick engines and installs the schema vocabulary hooks, exactly once
    /// per process. Safe to call repeatedly and from any verb.</summary>
    public static void EnsureInstalled() {
        lock (s_gate) {
            if (s_installed) {
                return;
            }

            var registry = new WorldMachineExtensionRegistry();

            new HumbleGamingBrickExtension().Initialize(registry: registry);
            new AdvancedGamingBrickExtension().Initialize(registry: registry);

            WorldSchemaVocabularyHooks.Install(
                postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
                probeKindCheck: WorldProbeKinds.IsShipped,
                screenMachineCartridgeCheck: WorldScreenMachineEngines.CompilesCartridges,
                screenMachineEngineCheck: WorldScreenMachineEngines.IsRegistered
            );

            s_installed = true;
        }
    }
}
