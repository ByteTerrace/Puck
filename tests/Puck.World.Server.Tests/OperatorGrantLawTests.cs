using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class OperatorGrantLawTests {
    private static readonly Principal Seat = Principal.Seat(0);
    private static WorldGrant Operate(Grantee grantee) => new(grantee, WorldCapability.Operate, GrantSubject.All, false);
    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [new WorldStateRow(Name: CellName.Parse("secret"), Kind: CellKind.Int,
            Cells: [new StateCell(Key: CellName.Parse("value"), Value: CellValue.Int(41))], Visibility: new StateVisibility(Readers: ["seat2"])), StateFixtures.IntSlot("public", 0)]),
    };
    private static WorldGroupKind Kind(params WorldCapability[] reach) => new("authority", [new("member", reach)], WorldGroupLifetime.Persistent, WorldGroupEvictionPolicy.Remove, 8);
    private static WorldGroup Group(params Principal[] members) => new(SafeName.Parse("operators"), "authority",
        members.Select((p, i) => new WorldGroupMember(WorldMemberRef.Local(p), "member", i)).ToArray(), NextJoinOrdinal: members.Length);
    private static bool Apply(WorldFixture fixture, WorldMutation mutation) {
        WorldEditEcho? result = null;
        fixture.Server.EchoTap = echo => { if (echo.Kind == WorldEditEchoKind.Mutation) { result = echo; } };
        fixture.Server.EnqueueMutation(mutation);
        fixture.Step();
        return result is { Rejected: false };
    }

    [Fact]
    public void SeatOperatorReadsDeclaresAdministersAndRevocationCutsTheSameView() {
        using var fixture = Fixtures.FreshServer(Document());
        var server = fixture.Server;
        var view = WorldStateReadView.Of(server, Seat);
        Assert.NotNull(view.Withheld("secret"));
        Assert.False(server.GrantTable.TryGrant(Operate(Seat), Seat, out _));
        Assert.True(server.GrantTable.TryGrant(Operate(Seat), Principal.Console, out var reason), reason);
        Assert.True(view.TryReadValue("secret", "value", out _, out _));
        Assert.True(server.GrantTable.TryGrant(Operate(Principal.Seat(1)), Seat, out reason), reason);
        Assert.True(Apply(fixture, new WorldMutation.UpsertStateRow(Seat, new(Name: CellName.Parse("declared"), Kind: CellKind.Int, Cells: []))));
        Assert.True(Apply(fixture, new WorldMutation.UpsertWorldRule(Seat, new(CellName.Parse("authored"), [new ActionEffect.SetState("declared", Value: 1)]))));
        Assert.True(server.GrantTable.TryGrant(new(Seat, WorldCapability.Mutate, GrantSubject.Section(WorldSection.Rules), false), Seat, out reason), reason);
        Assert.True(server.GrantTable.Revoke(Seat, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Assert.NotNull(view.Withheld("secret"));
        Assert.False(server.Grants.IsOperator(Seat));
        Assert.False(server.GrantTable.TryGrant(Operate(Seat), Seat, out _));
        Assert.False(Apply(fixture, new WorldMutation.RemoveWorldRule(Seat, CellName.Parse("absent"))));
    }

    [Fact]
    public void OrdinarySeatKeepsMeteredControlsAndOwnBodyConsentOnly() {
        using var fixture = Fixtures.FreshServer(Document());
        var grants = fixture.Server.GrantTable;
        Assert.True(grants.TryGetBudget(Seat, WorldCapability.Mutate, GrantSubject.Section(WorldSection.Groups), out _));
        Assert.True(grants.TryGetKindMask(Seat, WorldCapability.Mutate, GrantSubject.Section(WorldSection.Groups), out _));
        Assert.True(grants.TryGetBudget(Seat, WorldCapability.Edit, GrantSubject.State("public"), out _));
        Assert.True(Apply(fixture, new WorldMutation.UpsertStateCell(Seat, "public", StateRow.SlotKey.Value, 1, WorldDocumentWriteKind.Set)));
        var consent = new WorldGrant(Seat, WorldCapability.Drive, GrantSubject.Body(0), false, Ceiling: 10, Consent: new WorldChannelSelection(["forward"]));
        Assert.True(grants.TryGrant(consent, Seat, out var reason), reason);
        Assert.False(grants.TryGrant(consent with { Subject = GrantSubject.Body(1) }, Seat, out _));
        Assert.False(grants.TryGrant(Operate(Principal.Addon("guest")), Principal.Console, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothBatchOrdersAndGrantsSectionCannotSelfPromote(bool grantFirst) {
        var document = Document() with { Groups = new WorldGroupsSection(Kinds: [Kind(WorldCapability.Operate)], Groups: [], Ownership: []) };
        using var fixture = Fixtures.FreshServer(document);
        WorldMutation grant = new WorldMutation.UpsertGrant(Seat, Operate(Grantee.Group("operators")));
        WorldMutation form = new WorldMutation.FormGroup(Seat, "operators", "authority");
        Assert.False(Apply(fixture, new WorldMutation.UpsertGrant(Seat, Operate(Seat))));
        Assert.False(Apply(fixture, new WorldMutation.Batch(Seat, grantFirst ? [grant, form] : [form, grant])));
        Assert.False(fixture.Server.Grants.IsOperator(Seat));
        Assert.Empty(fixture.Server.Definition.Groups!.Groups);
    }

    [Fact]
    public void OperatorGroupsProtectMembershipAndRoleDefinitionsAndRejectAddonLaundering() {
        var document = Document() with { Groups = new WorldGroupsSection(Kinds: [Kind(WorldCapability.Operate)], Groups: [Group(Principal.Seat(1))], Ownership: []),
            GrantsRaw = [Operate(Grantee.Group("operators"))] };
        using var fixture = Fixtures.FreshServer(document);
        Assert.True(fixture.Server.Grants.IsOperator(Principal.Seat(1)));
        Assert.False(Apply(fixture, new WorldMutation.JoinGroup(Seat, "operators", Seat)));
        Assert.False(Apply(fixture, new WorldMutation.UpsertGroupKind(Seat, Kind(WorldCapability.Observe))));
        Assert.True(Apply(fixture, new WorldMutation.JoinGroup(Principal.Console, "operators", Seat)));
        Assert.False(fixture.Server.Grants.IsOperator(Seat)); // joining does not assign a role
        Assert.False(Apply(fixture, new WorldMutation.JoinGroup(Seat, "operators", Principal.Addon("guest"))));
        Assert.True(Apply(fixture, new WorldMutation.LeaveGroup(Principal.Console, "operators", Seat)));
        Assert.False(fixture.Server.Grants.IsOperator(Seat));
    }

    [Theory]
    [InlineData(WorldAdmissionTrustMode.SignsDirectly, "domain", "one", WorldDisclosureTier.Replica, true)]
    [InlineData(WorldAdmissionTrustMode.OAuth, "https://issuer.test", "one", WorldDisclosureTier.Replica, true)]
    [InlineData(WorldAdmissionTrustMode.Vouches, "domain", "one", WorldDisclosureTier.Replica, false)]
    [InlineData(WorldAdmissionTrustMode.FederatedAuthority, "domain", null, WorldDisclosureTier.Replica, false)]
    [InlineData(WorldAdmissionTrustMode.FederatedAuthority, "*", null, WorldDisclosureTier.Replica, false)]
    [InlineData(WorldAdmissionTrustMode.SignsDirectly, "*", "one", WorldDisclosureTier.Replica, false)]
    [InlineData(WorldAdmissionTrustMode.SignsDirectly, "domain", "*", WorldDisclosureTier.Replica, false)]
    [InlineData(WorldAdmissionTrustMode.SignsDirectly, "domain", "one", WorldDisclosureTier.Frames, false)]
    [InlineData(WorldAdmissionTrustMode.OAuth, "https://issuer.test", "one", WorldDisclosureTier.Presentation, false)]
    [InlineData(WorldAdmissionTrustMode.OAuth, "https://issuer.test", "*", WorldDisclosureTier.Replica, false)]
    public void OperatorTemplatesRequireOneSubjectAtReplica(WorldAdmissionTrustMode mode, string domain, string? subject, WorldDisclosureTier tier, bool allowed) {
        var entry = new WorldAdmissionEntry(Domain: domain, Subject: subject, Mode: mode, Disclosure: tier, Grants: [new(WorldCapability.Operate, GrantSubject.All)]);
        Assert.Equal(allowed, WorldAdmissionDoor.AllowsOperatorTemplates(entry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PeerOperatorAdmissionIsOrderIndependentAndDisconnectClosesAuthority(bool operateFirst) {
        WorldAdmissionGrant operate = new(WorldCapability.Operate, GrantSubject.All);
        WorldAdmissionGrant program = new(WorldCapability.Mutate, GrantSubject.Section(WorldSection.Rules));
        WorldAdmissionEntry entry = new(Domain: "https://issuer.test", Subject: "owner", Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica, Grants: operateFirst ? [operate, program] : [program, operate]);
        var document = Document();
        using var fixture = Fixtures.FreshServer(document with { Admission = [entry], PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4 } });
        var server = fixture.Server;
        Assert.True(WorldAdmissionDoor.TryMatchOAuthEntry([entry], entry.Domain, entry.Subject!, out var verdict));
        Assert.True(server.TryAdmitPeerConnection(verdict, server.Definition.Admission, out var peer, out var reason), reason);
        Assert.True(server.Grants.IsOperator(peer.Identity));
        var handles = server.Grants.HandleTable(peer.Identity, WorldCapability.Mutate);
        Assert.True(handles.TryMintFor(GrantSubject.Section(WorldSection.Rules), out var programHandle));
        Assert.True(WorldStateReadView.Of(server, peer.Identity).TryReadValue("secret", "value", out _, out _));
        Assert.True(server.GrantTable.TryGrant(Operate(Seat), peer.Identity, out reason), reason);
        Assert.True(Apply(fixture, new WorldMutation.UpsertStateRow(peer.Identity, new(Name: CellName.Parse("peer-row"), Kind: CellKind.Int, Cells: []))));
        Assert.True(Apply(fixture, new WorldMutation.UpsertWorldRule(peer.Identity, new(CellName.Parse("peer-program"), [new ActionEffect.SetState("peer-row", Value: 1)]))));
        Assert.True(server.GrantTable.Revoke(peer.Identity, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Assert.True(handles.TryResolve(programHandle, out _)); // designation survives; authority does not
        Assert.False(Apply(fixture, new WorldMutation.RemoveWorldRule(peer.Identity, CellName.Parse("peer-program"))));
        Assert.True(server.GrantTable.TryGrant(Operate(peer.Identity), Principal.Console, out reason), reason);
        var closed = false;
        server.PeerDisconnected += p => { if (p == peer.Identity) { closed = true; Assert.False(server.IsLivePeer(p)); } };
        Assert.True(server.DisconnectPeer(peer.Identity, Seat));
        Assert.True(closed);
        Assert.False(server.Grants.IsOperator(peer.Identity));
        Assert.False(server.GrantTable.TryGrant(Operate(Seat), peer.Identity, out _));
    }

    [Fact]
    public void OperateHasAnExplicitWorldWireTag() {
        Assert.True(WorldWireTags.TryToWire(WorldCapability.Operate, out var wire));
        Assert.True(WorldWireTags.TryFromWire(wire, out WorldCapability capability));
        Assert.Equal(WorldCapability.Operate, capability);
    }
}
