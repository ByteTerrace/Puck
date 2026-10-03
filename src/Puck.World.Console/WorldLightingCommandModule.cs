using Puck.World.Client;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>render.lighting</c>/<c>render.sky</c>/<c>render.environment</c>/<c>render.grounding</c>/
/// <c>render.tonemap</c> read-back: <c>world.lighting</c> reports every authored light by slot, the curvature
/// enrichment, every sky layer, the studio-reflection softbox count and horizon colors, the grounding
/// strength/radius, the tonemap mode, and the clock and key count of each keyed section; a keyed value reads as its
/// clock and key count. The sections are authored through <c>world.row.set render</c>; every field is optional and an
/// absent one reads <c>default</c>, which is the engine's pinned value for that field of that kind, not zero.
/// </summary>
public sealed class WorldLightingCommandModule(IWorldConsoleAuthority authority, Func<WorldDefinition, string?>? shadowReport = null) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return authority.CreateServerQueryCommand(
            description: "Reports the render.lighting, render.sky, render.environment, render.grounding, and render.tonemap census (Immediate; the stdin barrier makes it read the settled state after any pending mutation): every light by slot with its kind and fields, the last presented named shadow holders with their selection ranks and transition state, the stylized curvature enrichment and whether its runtime gate is open, every sky layer by index, the studio-reflection softbox count and horizon colors, the grounding strength/radius, the tonemap mode, and the clock and key count of each keyed section. A keyed value reads keys(clock: <name>, <n> keys); an unauthored field reads 'default' — the engine's pinned value for it, not zero.",
            describe: server => ((WorldLightingText.Describe(definition: server.Definition) + " | ") + (shadowReport?.Invoke(server.Definition) ?? "shadowSlots unavailable: no presented frame of this authority")),
            name: "world.lighting"
        );
    }
}
