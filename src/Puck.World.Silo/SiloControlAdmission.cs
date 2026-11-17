using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>The silo's remote console surface, shared by command dispatch and capability discovery.</summary>
public static class SiloControlAdmission {
    /// <summary>Checks the live peer generation, its current OAuth entitlement and this command's audience.</summary>
    public static bool Allows(CommandMetadata command, WorldServer server, WorldPeerEventEntry peer, ControlIdentity identity) {
        if (server.Grants.IsOperator(principal: peer.Identity)) { return AllowsSurface(command: command, operates: true); }
        return (server.IsLivePeer(principal: peer.Identity) && AllowsSurface(command: command, operates: false) &&
        WorldAdmissionDoor.TryMatchOAuthEntry(entries: server.Definition.Admission, issuer: identity.Issuer, subject: identity.Subject, verdict: out var current) &&
        (current.Tier == WorldDisclosureTier.Replica) && (server.Population.PeerAdmissionTier(bodyIndex: peer.BodyIndex) == WorldDisclosureTier.Replica));
    }
    /// <summary>Describes the surface an accepted admission would offer; dispatch rechecks the live grant.</summary>
    public static bool Describes(CommandMetadata command, WorldAdmissionVerdict verdict) => ((verdict.Tier == WorldDisclosureTier.Replica) &&
        AllowsSurface(command: command, operates: verdict.Templates.Any(predicate: row => (row.Capability == WorldCapability.Operate))));

    private static bool AllowsSurface(CommandMetadata command, bool operates) => ((command.Audience != CommandAudience.Host) &&
        ((operates && (command.Audience == CommandAudience.Operator)) || (command.Name is "world.wait" or "world.admission" or "world.links" or
            "world.state" or "world.state.cell.set" or "world.state.cell.remove" or "world.state.similar")));
}
