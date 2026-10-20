using System.Globalization;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    // Resolves a mapped portal arrival's counterpart face against the destination's own definition, and refuses a pair
    // that does not map world up onto itself: a traveler's heading and fall cross only a pair that does. The counterpart
    // resolve proves the authored anchor exists; the arrival frame is that face's own derived frame (WorldFaceCatalog),
    // exactly like the source frame captured at scan, so scan, arrival and the drawn door agree on where a face sits.
    private static bool TryResolvePortalCounterpartFrame(in PendingTransfer transfer, in WorldFaceFrame sourceFrame, WorldDefinition destinationDefinition, string targetName, out WorldFaceFrame frame, out string reason) {
        frame = default;

        if (!WorldPortalCounterpart.TryResolve(
            definition: destinationDefinition,
            counterpart: transfer.Counterpart,
            placement: out var counterpartPlacement,
            face: out var counterpartFace,
            reason: out var counterpartReason
        )) {
            reason = $"mapped arrival's {counterpartReason} in '{targetName}'";

            return false;
        }

        if (!WorldFaceCatalog.For(definition: destinationDefinition).TryFind(
            placementId: counterpartPlacement!.Id,
            faceName: counterpartFace!.Face,
            out var counterpartRow
        )) {
            reason = $"mapped arrival's counterpart '{transfer.Counterpart}' names no DECLARED creation face in '{targetName}'";

            return false;
        }

        if (!WorldFrameIsometry.PreservesWorldUp(
            destination: counterpartRow.Frame,
            source: in sourceFrame
        )) {
            reason = $"mapped arrival's counterpart '{transfer.Counterpart}' in '{targetName}' and its source face do not map world up onto itself, so a traveler's heading and fall cannot cross the pair; face the two doors along one axis (two walls, or a floor and a ceiling)";

            return false;
        }

        frame = counterpartRow.Frame;
        reason = string.Empty;

        return true;
    }
    // A mapped portal pair whose two faces stand in the source world itself: every member moves through the pair in
    // place, keeping its body index, kit, grants and tape, with no reservation, detach or commit. Applied at the host's
    // transfer drain like any crossing, so the members land before the next step moves them, with the velocity they
    // entered the source face at. A portal scan captures the source face's frame on every mapped transfer it mints.
    private void ApplyInPlaceCrossing(WorldInstance source, in PendingTransfer transfer, int[] members) {
        var transferId = transfer.TransferId;
        var sourceName = source.Name;
        var border = transfer.Border;
        var counterpart = transfer.Counterpart;
        var sourceFrame = transfer.SourceFrame!.Value;

        if (!TryResolvePortalCounterpartFrame(
            destinationDefinition: source.Server.Definition,
            frame: out var counterpartFrame,
            reason: out var reason,
            sourceFrame: in sourceFrame,
            targetName: sourceName,
            transfer: in transfer
        )) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused ({reason})]"
                );
            }

            return;
        }

        var crossed = source.Server.ExecuteAuthorityOperation(operation: () => {
            var moved = new List<(int Slot, WorldBodyTransferState State, FixedVector3 Position)>(capacity: members.Length);

            foreach (var slot in members) {
                if (
                    source.Server.ApplyInPlaceCrossing(crossing: new WorldInPlaceCrossing(
                        Destination: counterpartFrame,
                        Slot: slot,
                        Source: sourceFrame
                    )) &&
                    (source.Server.Population.EntryBody(index: slot) is { } body)
                ) {
                    moved.Add(item: (slot, body.CaptureTransferState(), body.FixedPosition));
                }
            }

            return moved;
        });
        var localSeats = source.Server.Population.LocalSeatCount;
        var turn = WorldFrameIsometry.YawDelta(
            destination: in counterpartFrame,
            source: in sourceFrame
        );

        foreach (var (slot, state, position) in crossed) {
            // A turning pair turns the seat's view with its body, as a mapped arrival does; a pair that keeps the
            // heading leaves the chase boom as it was, and the camera rides the carried body.
            if (
                (turn != FixedQ4816.Zero) &&
                (FollowedRosterSlot(
                    instance: source,
                    slot: slot
                ) is { } rosterSlot)
            ) {
                m_seats.CrossView(
                    slot: rosterSlot,
                    yawDelta: turn,
                    yawReference: source.Server.Definition.Views.SeatControl.YawReference
                );
            }

            if (m_narration.HasNarrationSink) {
                var planar = state.PlanarVelocity;

                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} '{sourceName}' {TravellerName(index: slot, localSeatCount: localSeats)} crossed {border} -> {counterpart} in place, at ({Format(value: ((double)position.X))}, {Format(value: ((double)position.Y))}, {Format(value: ((double)position.Z))}) moving ({Format(value: ((double)planar.X))}, {Format(value: ((double)state.VerticalVelocity))}, {Format(value: ((double)planar.Z))}) m/s]"
                );
            }
        }

        static string Format(double value) => value.ToString(
            format: "0.00",
            provider: CultureInfo.InvariantCulture
        );
    }
    // The local roster seat whose view follows an instance's body: the seat routed to it, or, in the boot world, the
    // local seat at its own index.
    private int? FollowedRosterSlot(WorldInstance instance, int slot) {
        if (TryFindFollowedRosterSlot(
            instanceName: instance.Name,
            instanceSlot: slot,
            rosterSlot: out var routed
        )) {
            return routed;
        }

        return ((ReferenceEquals(objA: instance, objB: Boot) && (slot < instance.Server.Population.LocalSeatCount))
            ? slot
            : null
        );
    }
}
