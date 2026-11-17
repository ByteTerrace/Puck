using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Silo.Tests;

public sealed class SiloOperatorLawTests {
    [Fact]
    public void RemoteSurfaceUsesLiveOperateAndNeverAdmitsHostCommands() {
        WorldAdmissionEntry entry = new(Domain: "https://issuer.test", Subject: "owner", Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica, Grants: [new(WorldCapability.Operate, GrantSubject.All)]);
        var document = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(document with { Admission = [entry], PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4 } });
        var server = fixture.Server;
        Assert.True(WorldAdmissionDoor.TryMatchOAuthEntry([entry], entry.Domain, entry.Subject!, out var verdict));
        Assert.True(server.TryAdmitPeerConnection(verdict, server.Definition.Admission, out var peer, out var reason), reason);
        var registry = new CommandRegistry([new ProbeModule()]);
        var world = Assert.Single(registry.Definitions, row => row.Name == "world.peers");
        var host = Assert.Single(registry.Definitions, row => row.Name == "host.probe");
        var identity = new ControlIdentity(entry.Domain, entry.Subject!);
        Assert.True(SiloControlAdmission.Describes(world, verdict));
        Assert.False(SiloControlAdmission.Describes(host, verdict));
        Assert.True(SiloControlAdmission.Allows(world, server, peer, identity));
        Assert.False(SiloControlAdmission.Allows(host, server, peer, identity));
        Assert.True(server.GrantTable.Revoke(peer.Identity, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Assert.False(SiloControlAdmission.Allows(world, server, peer, identity));
        Assert.True(server.GrantTable.TryGrant(new(peer.Identity, WorldCapability.Operate, GrantSubject.All, false), Principal.Console, out reason), reason);
        Assert.True(SiloControlAdmission.Allows(world, server, peer, identity));
        Assert.True(server.DisconnectPeer(peer.Identity, Principal.Console));
        Assert.False(SiloControlAdmission.Allows(world, server, peer, identity));
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            foreach (var (name, audience) in new[] { ("world.peers", CommandAudience.Operator), ("host.probe", CommandAudience.Host) }) {
                yield return CommandDefinition.Verb(name: name, audience: audience, bindability: CommandBindability.Unbindable,
                    description: "Exercises remote command admission.", valueKind: CommandValueKind.Digital, handler: _ => CommandResult.None);
            }
        }
    }
}
