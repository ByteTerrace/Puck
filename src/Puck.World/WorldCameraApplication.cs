using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The camera control application's dissolve, in one place: disengages the seat's possession route through the
/// ordinary door <c>player.disengage</c> uses, so the avatar resumes driving itself and the perceived body/camera
/// eye/audio listener fall back to it as a side effect of the SAME route WorldEngagement already tracks — never a
/// second mechanism. <c>player.mode</c> leaving a <see cref="WorldSeatModeState.CameraTarget"/> state runs this, and
/// so does the composition root when a mode reseed drops that state under a live application
/// (<see cref="WorldSeatBindings.CameraApplicationDropped"/>). Composing the application is
/// <c>PlayerCommandModule.Mode.cs</c>'s alone — it is the only path holding the authority check that gates entry.
/// </summary>
internal static class WorldCameraApplication {
    /// <summary>Disengages seat <paramref name="slot"/>'s camera control application. A no-op-shaped call on an
    /// already-disengaged seat (the ordinary <c>NotEngaged</c> outcome).</summary>
    /// <param name="link">The link the disengaging <see cref="WorldCommand.Disengage"/> is submitted through.</param>
    /// <param name="actingPrincipal">The principal the disengage submission is stamped with — the server's Control
    /// gate checks it against the target's own route exactly as <c>player.disengage</c> does.</param>
    /// <param name="slot">The 0-based seat slot.</param>
    public static void Deactivate(IServerLink link, WorldPrincipal actingPrincipal, int slot) {
        link.SubmitCommand(command: new WorldCommand.Disengage(
            EntityIndex: slot,
            Principal: actingPrincipal,
            TargetPrincipal: WorldPrincipal.Seat(slot: slot)
        ));
    }
}
