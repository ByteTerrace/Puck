using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The windowed World's answer to what the passthrough door asks of a local seat: the framed screen's pointer
/// feel from the delivered document, and the seat's leaving, submitted as the dissolve its context button's press makes
/// under the seat's own acting principal, which is the principal the input router stamps on that seat's physical
/// input.</summary>
/// <param name="client">The client whose delivered document names each screen's route.</param>
/// <param name="roster">The roster mapping a local seat to its acting principal and held input.</param>
/// <param name="link">The link the dissolve is submitted through.</param>
internal sealed class WorldPassthroughSeats(WorldClient client, PlayerRoster roster, IServerLink link) : IWorldPassthroughSeats {
    /// <inheritdoc/>
    public WorldScreenPadPointer? PadPointerOf(int screen) {
        foreach (var row in client.Definition.Screens) {
            if (row.Index == screen) {
                return row.Route.PadPointer;
            }
        }

        return null;
    }
    /// <inheritdoc/>
    public void Leave(int slot) {
        var principal = roster.PrincipalOf(slot: slot);

        link.SubmitCommand(command: new WorldCommand.DissolveControl(
            EntityIndex: slot,
            Principal: principal,
            TargetPrincipal: principal
        ));
        // The keys and buttons the seat held stay behind with the engagement, as body.disengage drops them.
        roster.Seat(slot: slot)?.ReleaseAllHeld();
    }
}
