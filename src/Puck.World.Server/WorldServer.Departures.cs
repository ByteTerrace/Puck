using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One source body a crossing detached, captured the instant before the detach discarded it: everything a
/// rollback needs to put the same body back at its source index. A transferred peer or entity also carries its
/// admission event, the grant templates its admission installed and the grant rows its principal held, which the
/// detach revoked and a rollback grants again.</summary>
/// <param name="Slot">The source body index.</param>
/// <param name="Profile">The body's retained profile, or <see langword="null"/> for an anonymous seat.</param>
/// <param name="BodyColor">The body's color.</param>
/// <param name="Position">The body's position.</param>
/// <param name="Yaw">The body's heading, in radians.</param>
/// <param name="DynamicState">The body's perceivable dynamic state.</param>
/// <param name="Designations">The seat's designation register.</param>
/// <param name="Peer">The transferred peer's or entity's admission, or <see langword="null"/> for a local seat.</param>
/// <param name="AdmissionGrants">The grant templates the peer's admission installed.</param>
/// <param name="SourceGrants">The grant rows the peer's principal held here.</param>
public sealed record WorldDetachedBody(int Slot, WorldIdentity? Profile, Vector3 BodyColor, FixedVector3 Position, FixedQ4816 Yaw, WorldBodyTransferState DynamicState, WorldTargetDesignation[] Designations, WorldPeerEventEntry? Peer, IReadOnlyList<WorldAdmissionGrant> AdmissionGrants, IReadOnlyList<WorldGrant> SourceGrants);
public sealed partial class WorldServer {
    // A re-drive's departures, held until the recorded rollback that restores each one.
    private readonly Dictionary<(ulong TransferId, int Slot), (WorldDetachedBody Body, FixedQ4816 TravelTurn)> m_redrivenDepartures = new();

    /// <summary>Gets or sets the observer of every source body a crossing detaches or restores: the source-scoped
    /// transfer id, the body index, and whether the body came back (a rollback) rather than left. It hears each one
    /// inside the authority operation that made it, so its position among the authority's inputs is the decision's
    /// own, whichever thread carried it and however long the crossing stays in doubt. The replay tape attaches here
    /// while it records.</summary>
    public Action<ulong, int, bool>? DepartureTap { get; set; }

    /// <summary>Detaches one departing body for a crossing: captures what a rollback needs, detaches the body, revokes
    /// the grant rows a transferred peer's principal held, and reports the departure to <see cref="DepartureTap"/>.
    /// The host's crossing and a replay's re-drive both detach here. The caller holds the authority gate.</summary>
    /// <param name="transferId">The source-scoped transfer id the body departs under.</param>
    /// <param name="slot">The body index.</param>
    /// <returns>The detached body, or <see langword="null"/> when the index holds no active body.</returns>
    public WorldDetachedBody? DetachForTransfer(ulong transferId, int slot) {
        if (
            (((uint)slot) >= ((uint)m_population.Capacity)) ||
            !m_population.IsActive(index: slot) ||
            (m_population.EntryBody(index: slot) is not { } body)
        ) {
            return null;
        }

        // Captured before the detach, which discards pose, dynamic state and designations and keeps only the profile.
        var position = body.FixedPosition;
        var bodyColor = m_population.BodyColor(index: slot);
        var yaw = body.FixedYaw;
        var dynamicState = body.CaptureTransferState();
        var designations = m_population.CaptureDesignations(slot: slot);
        WorldPeerEventEntry? peer = null;
        IReadOnlyList<WorldAdmissionGrant> admissionGrants = [];
        IReadOnlyList<WorldGrant> sourceGrants = [];

        if (m_population.TryCaptureTransferredEntity(
            index: slot,
            peer: out var capturedPeer
        )) {
            peer = capturedPeer;
            admissionGrants = [.. m_population.PeerAdmissionInstalledGrantTemplates(bodyIndex: slot)];
            sourceGrants = [.. GrantRows(principal: capturedPeer.Identity)];
        }
        if (!m_population.TryDetachSeatForTransfer(
            profile: out var profile,
            slot: slot
        )) {
            return null;
        }

        // Dissolving a departing member's rows is administration, symmetric with the admission mint and the rollback
        // re-grant: the rows may belong to a peer principal while the member travels under a different one.
        foreach (var grant in sourceGrants) {
            Revoke(
                actor: Principal.Console,
                grant: grant
            );
        }

        DepartureTap?.Invoke(
            arg1: transferId,
            arg2: slot,
            arg3: false
        );
        return new WorldDetachedBody(
            AdmissionGrants: admissionGrants,
            BodyColor: bodyColor,
            Designations: designations,
            DynamicState: dynamicState,
            Peer: peer,
            Position: position,
            Profile: profile,
            Slot: slot,
            SourceGrants: sourceGrants,
            Yaw: yaw
        );
    }
    /// <summary>Rolls one departure back: puts the detached body at its source index again with its departure turn,
    /// its mobility credential when one is given, its color and scale, and the grant rows the detach revoked, then
    /// reports the return to <see cref="DepartureTap"/>. The host's crossing and a replay's re-drive both restore here.
    /// The caller holds the authority gate.</summary>
    /// <param name="transferId">The source-scoped transfer id the body departed under.</param>
    /// <param name="detached">The body <see cref="DetachForTransfer"/> detached.</param>
    /// <param name="travelTurn">The body's accumulated arrival turn at its departure.</param>
    /// <param name="mobility">The body's mobility credential, or <see langword="null"/> to keep the one its index
    /// holds.</param>
    /// <returns><see langword="true"/> when the body is back; <see langword="false"/> when its index is occupied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detached"/> is <see langword="null"/>.</exception>
    public bool RestoreDetachedForTransfer(ulong transferId, WorldDetachedBody detached, FixedQ4816 travelTurn, WorldMobilityIdentity? mobility) {
        ArgumentNullException.ThrowIfNull(argument: detached);

        var restored = ((detached.Peer is { } peer)
            ? m_population.RestoreDetachedPeer(
                designations: detached.Designations,
                dynamicState: detached.DynamicState,
                grantTemplates: detached.AdmissionGrants,
                peer: in peer,
                position: detached.Position,
                profile: detached.Profile,
                yawRadians: detached.Yaw
            )
            : m_population.RestoreDetachedSeat(
                designations: detached.Designations,
                dynamicState: detached.DynamicState,
                position: detached.Position,
                profile: detached.Profile,
                slot: detached.Slot,
                yawRadians: detached.Yaw
            ));

        if (!restored) {
            return false;
        }

        m_population.SetTravelTurn(
            slot: detached.Slot,
            travelTurn: travelTurn
        );
        // A slot reused while the crossing was in doubt has a new local generation, not the returning individual's
        // durable identity, so a known credential is installed again.
        if (mobility is { } credential) {
            m_population.SetMobility(
                index: detached.Slot,
                mobility: in credential
            );
        }
        m_population.SetBodyColor(
            color: detached.BodyColor,
            slot: detached.Slot
        );
        // The restored body postdates the last scale resync, like every other admission door's fresh body.
        m_population.SyncBodyScale(definition: Definition);
        // The restored principal provably holds none of these rows at this moment, so the server administers it.
        foreach (var grant in detached.SourceGrants) {
            Grant(
                actor: Principal.Console,
                grant: grant
            );
        }

        DepartureTap?.Invoke(
            arg1: transferId,
            arg2: detached.Slot,
            arg3: true
        );
        return true;
    }

    // A re-drive's departure or its rollback, applied through the same detach and restore the host's crossing used.
    // A departure keeps what the shadow captured, with its departure turn; its rollback restores exactly that body.
    // Returns why the shadow could not reproduce it, or null. The caller holds the authority gate.
    internal string? RedriveDeparture(ulong transferId, int slot, bool restored) {
        var key = (transferId, slot);

        if (!restored) {
            if (m_redrivenDepartures.ContainsKey(key: key)) {
                return $"transfer {transferId} departs body:{slot} twice";
            }

            var travelTurn = ((((uint)slot) < ((uint)m_population.Capacity))
                ? m_population.TravelTurn(index: slot)
                : FixedQ4816.Zero);

            if (DetachForTransfer(
                slot: slot,
                transferId: transferId
            ) is not { } detached) {
                return $"transfer {transferId} departs body:{slot}, which holds no active body";
            }

            m_redrivenDepartures[key] = (detached, travelTurn);
            return null;
        }
        if (!m_redrivenDepartures.Remove(
            key: key,
            value: out var departure
        )) {
            return $"transfer {transferId} restores body:{slot} with no departure before it on this tape";
        }
        if (!RestoreDetachedForTransfer(
            detached: departure.Body,
            mobility: null,
            transferId: transferId,
            travelTurn: departure.TravelTurn
        )) {
            return $"transfer {transferId} cannot restore body:{slot}, which is occupied";
        }
        return null;
    }
}
