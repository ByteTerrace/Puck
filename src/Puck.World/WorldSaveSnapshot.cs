using Puck.Launcher;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Composes the snapshot the <c>world.save</c> verb and the <c>save</c> rule effect write: the authority half
/// (<see cref="WorldSessionCapture.Capture"/>) folds the session state the server owns, and then
/// <see cref="WorldSessionLevers.Fold"/> folds the presentation levers. Both keep every section the session left alone
/// as its author wrote it, so the snapshot of an untouched world is its authored document.</summary>
internal static class WorldSaveSnapshot {
    /// <summary>Composes the save snapshot of <paramref name="server"/>'s live definition.</summary>
    /// <param name="server">The authoritative server whose definition, population, and machines fold.</param>
    /// <param name="tick">The server's completed tick, the instant cycling and easing state settles at.</param>
    /// <param name="render">The live render levers.</param>
    /// <param name="pacing">The live present-rate control.</param>
    /// <param name="audio">The audio director owning the master-volume lever.</param>
    /// <param name="bindingBar">The live per-seat binding-bar visibility overrides.</param>
    /// <returns>The snapshot definition to serialize.</returns>
    public static WorldDefinition Compose(WorldServer server, ulong tick, WorldRenderSettings render, PresentPacingControl pacing, IWorldAudioLever audio, WorldBindingBarVisibility bindingBar) =>
        WorldSessionLevers.Fold(
            audio: audio,
            bindingBar: bindingBar,
            definition: WorldSessionCapture.Capture(
                definition: server.Definition,
                engineTick: server.CompletedEngineTicks,
                machines: server.Machines,
                population: server.Population,
                tick: tick
            ),
            pacing: pacing,
            settings: render
        );
}
