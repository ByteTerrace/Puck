using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The fly control application's dissolve, in one place: the rig stops framing the seat and the seat's captured
/// intent source is re-admitted on both halves (the authoritative submission and the client-side mirror), so no
/// caller can perform half of it. <c>player.mode</c> leaving a
/// <see cref="WorldSeatModeState.CameraTarget"/> state runs this, and so does the composition root when a mode
/// reseed drops that state under a live application (<see cref="WorldSeatBindings.CameraApplicationDropped"/>).
/// Composing the application is <c>PlayerCommandModule.Mode.cs</c>'s alone — it is the only path holding the
/// authority check that gates entry.
/// </summary>
internal static class WorldCameraApplication {
    /// <summary>Dissolves seat <paramref name="slot"/>'s fly application, restoring the intent source latched when
    /// it was composed. A no-op-shaped call on an inactive seat restores <see cref="IntentSource.Live"/>.</summary>
    /// <param name="flyRig">The seat fly rig holding the latched source.</param>
    /// <param name="link">The link the restoring <see cref="WorldCommand.SetControl"/> is submitted through.</param>
    /// <param name="roster">The client roster carrying the seat's own mirror of the source.</param>
    /// <param name="actingPrincipal">The principal the restoring submission is stamped with — the server's Drive
    /// gate checks it against the target body exactly as <c>player.control</c> does.</param>
    /// <param name="slot">The 0-based seat slot.</param>
    public static void Deactivate(WorldSeatFlyRig flyRig, IServerLink link, PlayerRoster roster, WorldPrincipal actingPrincipal, int slot) {
        var priorSource = flyRig.Deactivate(slot: slot);

        link.SubmitCommand(command: new WorldCommand.SetControl(
            Principal: actingPrincipal,
            EntityIndex: slot,
            Source: priorSource
        ));
        roster.Seat(slot: slot)?.SetIntentSource(source: priorSource);
    }
}
