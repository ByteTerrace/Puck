using Puck.Assets;
using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A source-authenticated projection request, with a disclosure ceiling and a bounded forwarding path.</summary>
/// <param name="SourceAuthority">The namespace authenticated by the sending connection.</param>
/// <param name="Mobility">The committed traveler credential issued to that namespace.</param>
/// <param name="Ceiling">The greatest document disclosure the requester may receive.</param>
/// <param name="RemainingHops">The remaining local or remote forwarding hops, from 1 through 64.</param>
public readonly record struct WorldTravelerObservation(string SourceAuthority, WorldMobilityIdentity Mobility,
    WorldDisclosureTier Ceiling = WorldDisclosureTier.Replica, byte RemainingHops = 64);
public static partial class WorldFederationCodec {
    /// <summary>Encodes one bounded traveler observation request.</summary>
    /// <param name="request">The source credential and forwarding limits.</param>
    /// <returns>The encoded request payload.</returns>
    public static byte[] EncodeTravelerObservation(in WorldTravelerObservation request) {
        var writer = new WireWriter();

        writer.WriteString(value: request.SourceAuthority);
        WorldWireLeaves.WriteMobility(
            writer,
            request.Mobility
        );
        writer.WriteByte(value: ((byte)request.Ceiling));
        writer.WriteByte(value: request.RemainingHops);
        return writer.ToArray();
    }
    /// <summary>Decodes exactly one traveler observation request and validates its forwarding limits.</summary>
    /// <param name="body">The complete request payload.</param>
    /// <param name="request">The decoded request on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns>True only for a complete, valid request.</returns>
    public static bool TryDecodeTravelerObservation(ReadOnlySpan<byte> body, out WorldTravelerObservation request, out WireFailure failure) {
        var reader = new WireReader(bytes: body);
        var source = reader.ReadRequiredString("observation source authority");
        var mobility = WorldWireLeaves.ReadMobility(reader: ref reader);
        var ceiling = ((WorldDisclosureTier)reader.ReadByte());
        var hops = reader.ReadByte();

        if (
            !Enum.IsDefined(value: ceiling) ||
            (hops is 0 or > 64)
        ) {
            reader.Fail(
                detail: "observation needs a defined disclosure tier and 1..64 remaining hops",
                refusal: WireRefusal.PayloadMalformed
            );
        }
        request = new(
            Ceiling: ceiling,
            Mobility: mobility,
            RemainingHops: hops,
            SourceAuthority: source
        );
        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
}
/// <summary>Authorizes a prototype fetch by composing the same live view that authorizes its reference.</summary>
public static class WorldProjectionPrototypeFetch {
    /// <summary>Returns an object's bytes only while this recipient's current projection contains its pin.</summary>
    /// <param name="server">The authority receiving the request.</param>
    /// <param name="pin">The requested content pin.</param>
    /// <param name="sourceAuthority">The authenticated connection's namespace.</param>
    /// <param name="ceiling">The connection's disclosure ceiling.</param>
    /// <param name="traveler">The traveler credential, or null for public observation.</param>
    /// <param name="content">The authority's content store, or the shared store.</param>
    /// <returns>The disclosed bytes, or null on refusal.</returns>
    public static byte[]? Fetch(WorldServer server, ContentPin pin, string sourceAuthority, WorldDisclosureTier ceiling,
        WorldTravelerObservation? traveler = null, ContentAddressedStore? content = null) {
        var forward = false;
        var bytes = server.ExecuteAuthorityOperation(operation: () => {
            var liveTier = ((WorldAdmissionDoor.TryAdmitArrival(entries: server.Definition.Admission,
                sourceAuthority: sourceAuthority, verdict: out var publicAdmission) is null)
                    ? publicAdmission!.Tier : WorldDisclosureTier.Presentation);

            ceiling = ((WorldDisclosureTier)Math.Min(val1: ((byte)ceiling), val2: ((byte)liveTier)));
            if (ceiling == WorldDisclosureTier.Frames) { return null; }
            Principal? recipient = null;

            if (traveler is { } request) {
                if ((request.RemainingHops is 0 or > 64) || (request.SourceAuthority != sourceAuthority) ||
                    !server.TryTransferredPrincipal(sourceAuthority: sourceAuthority, mobility: request.Mobility, principal: out var principal) ||
                    (WorldAdmissionDoor.TryAdmitArrival(entries: server.Definition.Admission, sourceAuthority: sourceAuthority,
                        verdict: out var admission) is not null)) { return null; }
                ceiling = ((WorldDisclosureTier)Math.Min(val1: ((byte)ceiling), val2: Math.Min(val1: ((byte)request.Ceiling), val2: ((byte)admission!.Tier))));
                traveler = request with { Ceiling = ceiling };
                if (ceiling == WorldDisclosureTier.Frames) { return null; }
                if (!WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(principal: principal, server: server)) {
                    forward = true;
                    return null;
                }
                if (!server.Grants.Allows(capability: WorldCapability.Observe, principal: principal,
                    subject: GrantSubject.Body(index: principal.Index)).IsAllowed) { return null; }
                recipient = principal;
            }
            try {
                var store = (content ?? WorldProjectionContent.Shared);
                var projection = WorldProjection.Compose(definition: server.Definition, tier: WorldDisclosureTier.Presentation,
                    authority: server.AuthorityIdentity, revision: server.Population.Revision, arena: server.Arena,
                    time: server.DeliveryTime, recipient: recipient, content: store)!;

                if (!projection.Creations.Any(predicate: reference => (reference.Content == pin.ToString())) ||
                    !store.TryGet(content: out var body, pin: pin)) { return null; }
                return body;
            } catch (WorldDisclosureException) {
                return null;
            }
        });

        if (forward && (traveler is { RemainingHops: > 1 } onward) && (server.TransferForwarder is { } forwarder)) {
            return forwarder.FetchForwardedPrototype(pin: pin, request: onward, source: server);
        }
        if (bytes is not null) {
            WorldProjectionWork.Count(kind: WorldProjectionWork.PrototypeFetches);
            WorldProjectionWork.Count(kind: WorldProjectionWork.PrototypeBytes, amount: bytes.Length);
        }
        return bytes;
    }
}
public static partial class WorldFederationCodec {
    /// <summary>Encodes a prototype pin and an optional traveler credential.</summary>
    /// <param name="pin">The requested pin.</param>
    /// <param name="traveler">The traveler, or null for public observation.</param>
    /// <returns>The request bytes.</returns>
    public static byte[] EncodePrototypeRequest(ContentPin pin, WorldTravelerObservation? traveler) {
        var writer = new WireWriter();

        writer.WriteString(value: pin.ToString());
        writer.WriteBlock(value: ((traveler is { } request) ? EncodeTravelerObservation(request: request) : []));
        return writer.ToArray();
    }
    /// <summary>Decodes exactly one prototype request.</summary>
    /// <param name="body">The request bytes.</param>
    /// <param name="pin">The pin on success.</param>
    /// <param name="traveler">The traveler, or null for public observation.</param>
    /// <returns>Whether the request is well formed.</returns>
    public static bool TryDecodePrototypeRequest(ReadOnlySpan<byte> body, out ContentPin pin, out WorldTravelerObservation? traveler) {
        var reader = new WireReader(bytes: body);
        var text = reader.ReadRequiredString(field: "prototype pin");
        var credential = reader.ReadBlock(field: "prototype recipient", maxBytes: (4 * WireLimits.MaxStringBytes));

        traveler = null;
        pin = default;
        if (!Finish(failure: out _, reader: ref reader) || !ContentPin.TryParse(pin: out pin, text: text)) { return false; }
        if (credential.Length != 0) {
            if (!TryDecodeTravelerObservation(body: credential, failure: out _, request: out var request)) { return false; }
            traveler = request;
        }
        return true;
    }
}
