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
        writer.WriteString(request.SourceAuthority);
        WorldWireLeaves.WriteMobility(writer, request.Mobility);
        writer.WriteByte((byte)request.Ceiling);
        writer.WriteByte(request.RemainingHops);
        return writer.ToArray();
    }
    /// <summary>Decodes exactly one traveler observation request and validates its forwarding limits.</summary>
    /// <param name="body">The complete request payload.</param>
    /// <param name="request">The decoded request on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns>True only for a complete, valid request.</returns>
    public static bool TryDecodeTravelerObservation(ReadOnlySpan<byte> body, out WorldTravelerObservation request, out WireFailure failure) {
        var reader = new WireReader(body);
        var source = reader.ReadRequiredString("observation source authority");
        var mobility = WorldWireLeaves.ReadMobility(ref reader);
        var ceiling = (WorldDisclosureTier)reader.ReadByte();
        var hops = reader.ReadByte();
        if (!Enum.IsDefined(ceiling) || hops is 0 or > 64) {
            reader.Fail(WireRefusal.PayloadMalformed, "observation needs a defined disclosure tier and 1..64 remaining hops");
        }
        request = new(source, mobility, ceiling, hops);
        return Finish(ref reader, out failure);
    }
}
