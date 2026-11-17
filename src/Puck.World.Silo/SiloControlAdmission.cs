using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>The silo's remote console surface, shared by command dispatch and capability discovery.</summary>
public static class SiloControlAdmission {
    /// <summary>Checks the live peer generation, its current OAuth entitlement and this command's audience.</summary>
    public static bool Allows(CommandMetadata command, WorldServer server, WorldPeerEventEntry peer, ControlIdentity identity) =>
        server.IsLivePeer(peer.Identity) && AllowsSurface(command, server.Grants.IsOperator(peer.Identity)) &&
        WorldAdmissionDoor.TryMatchOAuthEntry(server.Definition.Admission, identity.Issuer, identity.Subject, out var current) &&
        current.Tier == WorldDisclosureTier.Replica && server.Population.PeerAdmissionTier(peer.BodyIndex) == WorldDisclosureTier.Replica;

    /// <summary>Describes the surface an accepted admission would offer; dispatch rechecks the live grant.</summary>
    public static bool Describes(CommandMetadata command, WorldAdmissionVerdict verdict) => verdict.Tier == WorldDisclosureTier.Replica &&
        AllowsSurface(command, verdict.Templates.Any(row => row.Capability == WorldCapability.Operate));

    private static bool AllowsSurface(CommandMetadata command, bool operates) => command.Audience != CommandAudience.Host &&
        ((operates && command.Audience == CommandAudience.Operator) || command.Name is "world.wait" or "world.admission" or "world.links" or
            "world.state" or "world.state.cell.set" or "world.state.cell.remove" or "world.state.similar");
}
