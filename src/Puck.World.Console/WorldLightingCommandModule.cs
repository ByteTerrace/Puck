using Puck.World.Client;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>render.lighting</c>/<c>render.sky</c>/<c>render.atmosphere</c>/<c>render.environment</c>/
/// <c>render.tonemap</c> read-back: <c>world.lighting</c> reports every authored light by slot, the curvature
/// enrichment, every sky layer, the atmosphere's fog, haze and medium, the environment ambient and reflection gains,
/// the tonemap mode, and the clock and key count of each keyed section; a keyed value reads as its
/// clock, key count and cadence change class. Section keys expand through the same field-key resolver.
/// The host callbacks append presented shadow holders and the actual indirect inventory; absent callbacks name
/// the unavailable presentation instead of inferring GPU state.
/// The sections are authored through <c>world.row.set render</c>; every field is optional and an
/// absent one reads <c>default</c>, which is the engine's pinned value for that field of that kind, not zero.
/// </summary>
public sealed class WorldLightingCommandModule(IWorldConsoleAuthority authority, Func<WorldDefinition, string?>? shadowReport = null,
    Func<WorldDefinition, string?>? indirectReport = null) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return authority.CreateServerQueryCommand(
            description: "Reports the render lighting, sky, atmosphere, environment and tonemap census: lights, named shadow holders, curvature, sky layers, fog, haze, medium and ambient/reflection gains, plus the host's indirect cache inventory and allocations. GPU classifications and solve results require a fenced readback and are named unread. Each keyed value, including a field keyed through a section, reports its clock, key count and cadence class (visual-only, lighting-visible, shadow-direction or geometry-or-camera). Sky fields follow their layer visibility, while discs remain visual-only; environment gains and fog density are lighting-visible. An unauthored field reads 'default'. Immediate; the stdin barrier reads settled state after pending mutations.",
            describe: server => WorldLightingText.Describe(definition: server.Definition) + " | "
                + (shadowReport?.Invoke(server.Definition) ?? "shadowSlots unavailable: no presented frame of this authority") + " | "
                + (indirectReport?.Invoke(server.Definition) ?? "indirect unavailable: no presented frame of this authority"),
            name: "world.lighting"
        );
    }
}
