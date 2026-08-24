using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The camera control application's dissolve, in one place: dissolves the seat's possession through the
/// ordinary door <c>body.disengage</c> uses, so the avatar resumes driving itself and the perceived body/camera
/// eye/audio listener fall back to it as a side effect of the SAME application set WorldEngagement already tracks —
/// never a second mechanism. <c>player.mode</c> leaving a <see cref="WorldSeatModeState.CameraTarget"/> state runs this, and
/// so does the composition root when a mode reseed drops that state under a live application
/// (<see cref="WorldSeatBindings.CameraApplicationDropped"/>). Composing the application is
/// <c>PlayerCommandModule.Mode.cs</c>'s alone — it is the only path holding the authority check that gates entry.
/// </summary>
internal static class WorldCameraApplication {
    /// <summary>Dissolves seat <paramref name="slot"/>'s camera control application. A no-op-shaped call on a seat
    /// that has composed nothing (the ordinary <see cref="ControlOutcome.NotApplied"/> outcome).</summary>
    /// <param name="link">The link the <see cref="WorldCommand.DissolveControl"/> is submitted through.</param>
    /// <param name="actingPrincipal">The principal the dissolve submission is stamped with — the server's Control
    /// gate checks it against each applied target exactly as <c>body.disengage</c> does.</param>
    /// <param name="slot">The 0-based seat slot.</param>
    public static void Deactivate(IServerLink link, WorldPrincipal actingPrincipal, int slot) {
        link.SubmitCommand(command: new WorldCommand.DissolveControl(
            EntityIndex: slot,
            Principal: actingPrincipal,
            TargetPrincipal: WorldPrincipal.Seat(slot: slot)
        ));
    }
}
