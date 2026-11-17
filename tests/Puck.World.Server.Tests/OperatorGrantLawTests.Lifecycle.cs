using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class OperatorGrantLawTests {
    [Fact]
    public void PeerCommandsHideHostIdentityAndDisconnectClosesTheExistingSession() {
        WorldAdmissionEntry entry = new(Domain: "https://issuer.test", Subject: "private-owner", Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica, Grants: [new(WorldCapability.Operate, GrantSubject.All)]);
        var document = Document();
        using var row = HostRow.Build("operator", document with { Admission = [entry], PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4 } });
        var server = row.Server;
        Assert.True(WorldAdmissionDoor.TryMatchOAuthEntry([entry], entry.Domain, entry.Subject!, out var verdict));
        Assert.True(server.TryAdmitPeerConnection(verdict, server.Definition.Admission, out var peer, out var reason), reason);
        IWorldConsoleAuthority authority = new FixedAuthority(row.Instance);
        var registry = new CommandRegistry([new WorldNetworkCommandModule(authority)], authority: authority);
        var source = new TextCommandSource(registry);
        var result = CommandResult.None;
        using var session = source.CreateSession(peer.Identity, onResult: (_, answer) => result = answer);
        session.Enqueue("world.peers"); source.Collect();
        Assert.False(result.IsError, result.Output);
        Assert.Contains(peer.Identity.Describe(), result.Output);
        Assert.DoesNotContain(entry.Domain, result.Output);
        Assert.DoesNotContain(entry.Subject!, result.Output);
        Assert.DoesNotContain("identity:", result.Output);
        var closed = false;
        server.PeerDisconnected += principal => closed = principal == peer.Identity;
        session.Enqueue($"world.peer.disconnect {peer.Identity.Describe()}"); source.Collect();
        Assert.False(result.IsError, result.Output);
        Assert.True(closed);
        Assert.False(server.IsLivePeer(peer.Identity));
        session.Enqueue("world.peers"); source.Collect();
        Assert.True(result.IsError);
    }

    [Fact]
    public void ARevocationReseedsTheDeliveryCursorWithoutAnArenaWrite() {
        using var fixture = Fixtures.FreshServer(Document());
        var server = fixture.Server;
        Assert.True(server.GrantTable.TryGrant(Operate(Seat), Principal.Console, out var reason), reason);
        var view = WorldStateReadView.Of(server, Seat).Recipient;
        var cursor = new WorldDeliveryCursor();
        var whole = Assert.IsType<WorldDefinition>(cursor.DeliverDefinition(view));
        Assert.Single(whole.State.Single(row => row.Name.Value == "secret").Cells!);
        Assert.True(server.GrantTable.Revoke(Seat, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        var delta = Assert.Single(view.Advance(cursor, []));
        Assert.Empty(delta.Image.Cells);
        Assert.Empty(view.Advance(cursor, []));
        Assert.True(server.GrantTable.TryGrant(Operate(Seat), Principal.Console, out reason), reason);
        Assert.Single(Assert.Single(view.Advance(cursor, [])).Image.Cells);
    }

    [Fact]
    public void AnExistingCommandSessionChecksTheLiveGrantAndNeverGainsHostAuthority() {
        using var fixture = Fixtures.FreshServer(Document());
        var server = fixture.Server;
        var registry = new CommandRegistry([new OperatorModule()], authority: new LiveAuthority(server));
        var source = new TextCommandSource(registry);
        var result = CommandResult.None;
        using var session = source.CreateSession(Seat, onResult: (_, answer) => result = answer);
        void Invoke(string line) { session.Enqueue(line); source.Collect(); }
        Invoke("operator.probe"); Assert.True(result.IsError);
        Assert.True(server.GrantTable.TryGrant(Operate(Seat), Principal.Console, out var reason), reason);
        Invoke("operator.probe"); Assert.False(result.IsError, result.Output);
        Invoke("host.probe"); Assert.True(result.IsError);
        Assert.True(server.GrantTable.Revoke(Seat, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Invoke("operator.probe"); Assert.True(result.IsError);
    }

    [Theory]
    [InlineData(WorldDisclosureTier.Frames)]
    [InlineData(WorldDisclosureTier.Presentation)]
    public void LivePeersBelowReplicaCannotAcquireOperateDirectlyOrThroughGroups(WorldDisclosureTier tier) {
        WorldAdmissionEntry entry = new(Domain: "https://issuer.test", Subject: "visitor", Mode: WorldAdmissionTrustMode.OAuth, Disclosure: tier, Grants: []);
        var document = Document();
        using var fixture = Fixtures.FreshServer(document with { Admission = [entry], PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4 },
            Groups = new([Kind(WorldCapability.Operate)], [Group(Principal.Peer(7, 1))], []) });
        var server = fixture.Server;
        Assert.True(WorldAdmissionDoor.TryMatchOAuthEntry([entry], entry.Domain, entry.Subject!, out var verdict));
        Assert.True(server.TryAdmitPeerConnection(verdict, server.Definition.Admission, out var peer, out var reason), reason);
        Assert.Equal(Principal.Peer(7, 1), peer.Identity);
        Assert.False(server.GrantTable.TryGrant(Operate(peer.Identity), Principal.Console, out _));
        Assert.False(server.GrantTable.TryGrant(Operate(Grantee.Group("operators")), Principal.Console, out _));
        Assert.DoesNotContain(server.GrantTable.Held(peer.Identity), row => row.Capability == WorldCapability.Operate);
    }

    [Fact]
    public void AuthoredGrantOrderCannotHideAnAddonOperatorInAGroup() {
        var document = Document() with { Groups = new([Kind(WorldCapability.Operate)], [Group(Principal.Addon("guest"))], []) };
        using var fixture = Fixtures.FreshServer(document);
        Assert.False(fixture.Server.GrantTable.TryGrant(Operate(Grantee.Group("operators")), Principal.Console, out var reason));
        Assert.Contains("addons are excluded", reason);
    }

    [Fact]
    public void AnOperatorOwningGroupCannotBeAcquiredByAnOrdinarySeat() {
        var ownership = new WorldOwnership(new(OwnershipSubjectKind.Group, "operators"), new(OwnershipOwnerKind.Principal, Principal: Principal.Seat(1)));
        var document = Document() with { Groups = new([Kind(WorldCapability.Operate)], [Group()], [ownership]), GrantsRaw = [Operate(Grantee.Group("operators"))] };
        using var fixture = Fixtures.FreshServer(document);
        Assert.True(fixture.Server.Grants.IsOperator(Principal.Seat(1)));
        Assert.False(Apply(fixture, new WorldMutation.OfferOwnership(Seat, ownership.Subject, Seat, 100)));
        Assert.True(Apply(fixture, new WorldMutation.OfferOwnership(Principal.Seat(1), ownership.Subject, Seat, 100)));
        Assert.False(Apply(fixture, new WorldMutation.SettleOwnership(Seat, ownership.Subject, Reclaim: false)));
        Assert.False(fixture.Server.Grants.IsOperator(Seat));
    }

    private sealed class LiveAuthority(WorldServer server) : ICommandAuthority {
        public bool IsOperator(CommandContext context) => server.Grants.IsOperator(context.Principal);
    }
    private sealed class OperatorModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            foreach (var (name, audience) in new[] { ("operator.probe", CommandAudience.Operator), ("host.probe", CommandAudience.Host) }) {
                yield return CommandDefinition.Verb(name: name, audience: audience, bindability: CommandBindability.Bindable,
                    description: "Exercises live authority dispatch.", valueKind: CommandValueKind.Digital, handler: _ => CommandResult.None);
            }
        }
    }
}
