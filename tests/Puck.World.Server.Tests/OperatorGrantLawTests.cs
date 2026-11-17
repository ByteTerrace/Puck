using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class OperatorGrantLawTests {
    private static readonly Principal Seat = Principal.Seat(slot: 0);

    private static WorldGrant Operate(Grantee grantee) => new(grantee, WorldCapability.Operate, GrantSubject.All, false);
    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [new WorldStateRow(Name: CellName.Parse(candidate: "secret"), Kind: CellKind.Int,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "value"), Value: CellValue.Int(value: 41))], Visibility: new StateVisibility(Readers: ["seat2"])), StateFixtures.IntSlot("public", 0)]),
    };
    private static WorldGroupKind Kind(params WorldCapability[] reach) => new("authority", [new(Capabilities: reach, Name: "member")], WorldGroupLifetime.Persistent, WorldGroupEvictionPolicy.Remove, 8);
    private static WorldGroup Group(params Principal[] members) => new(SafeName.Parse(candidate: "operators"), "authority",
        members.Select(selector: (p, i) => new WorldGroupMember(WorldMemberRef.Local(principal: p), "member", i)).ToArray(), NextJoinOrdinal: members.Length);
    private static bool Apply(WorldFixture fixture, WorldMutation mutation) {
        WorldEditEcho? result = null;

        fixture.Server.EchoTap = echo => { if (echo.Kind == WorldEditEchoKind.Mutation) { result = echo; } };
        fixture.Server.EnqueueMutation(mutation);
        fixture.Step();
        return (result is { Rejected: false });
    }

    [Fact]
    public void SeatOperatorReadsDeclaresAdministersAndRevocationCutsTheSameView() {
        using var fixture = Fixtures.FreshServer(Document());
        var server = fixture.Server;
        var view = WorldStateReadView.Of(reader: Seat, server: server);

        Assert.NotNull(@object: view.Withheld(row: "secret"));
        Assert.False(condition: server.GrantTable.TryGrant(Operate(grantee: Seat), Seat, out _));
        Assert.True(condition: server.GrantTable.TryGrant(Operate(grantee: Seat), Principal.Console, out var reason), userMessage: reason);
        Assert.True(condition: view.TryReadValue("secret", "value", out _, out _));
        Assert.True(condition: server.GrantTable.TryGrant(Operate(grantee: Principal.Seat(slot: 1)), Seat, out reason), userMessage: reason);
        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertStateRow(Principal: Seat, Row: new(Name: CellName.Parse(candidate: "declared"), Kind: CellKind.Int, Cells: []))));
        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertWorldRule(Principal: Seat, Rule: new(CellName.Parse(candidate: "authored"), [new ActionEffect.SetState("declared", Value: 1)]))));
        Assert.True(condition: server.GrantTable.TryGrant(new(Seat, WorldCapability.Mutate, GrantSubject.Section(section: WorldSection.Rules), false,
            Budget: 16, KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.Rules)), Seat, out reason), userMessage: reason);
        Assert.True(condition: server.GrantTable.Revoke(Seat, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Assert.NotNull(@object: view.Withheld(row: "secret"));
        Assert.False(condition: server.Grants.IsOperator(principal: Seat));
        Assert.False(condition: server.GrantTable.TryGrant(Operate(grantee: Seat), Seat, out _));
        Assert.False(condition: Apply(fixture: fixture, mutation: new WorldMutation.RemoveWorldRule(Seat, CellName.Parse(candidate: "absent"))));
    }
    [Fact]
    public void OrdinarySeatKeepsMeteredControlsAndOwnBodyConsentOnly() {
        using var fixture = Fixtures.FreshServer(Document());
        var grants = fixture.Server.GrantTable;

        Assert.True(condition: grants.TryGetBudget(Seat, WorldCapability.Mutate, GrantSubject.Section(section: WorldSection.Groups), out _));
        Assert.True(condition: grants.TryGetKindMask(Seat, WorldCapability.Mutate, GrantSubject.Section(section: WorldSection.Groups), out _));
        Assert.True(condition: grants.TryGetBudget(Seat, WorldCapability.Edit, GrantSubject.State(name: "public"), out _));
        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertStateCell(Seat, "public", StateRow.SlotKey.Value, 1, WorldDocumentWriteKind.Set)));
        var consent = new WorldGrant(Seat, WorldCapability.Drive, GrantSubject.Body(index: 0), false, Ceiling: 10, Consent: new WorldChannelSelection(["forward"]));

        Assert.True(condition: grants.TryGrant(actor: Seat, grant: consent, reason: out var reason), userMessage: reason);
        Assert.False(condition: grants.TryGrant(consent with { Subject = GrantSubject.Body(index: 1) }, Seat, out _));
        Assert.False(condition: grants.TryGrant(Operate(grantee: Principal.Addon(name: "guest")), Principal.Console, out _));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void BothBatchOrdersAndGrantsSectionCannotSelfPromote(bool grantFirst) {
        var document = Document() with { Groups = new WorldGroupsSection(Kinds: [Kind(WorldCapability.Operate)], Groups: [], Ownership: []) };
        using var fixture = Fixtures.FreshServer(document);
        WorldMutation grant = new WorldMutation.UpsertGrant(Principal: Seat, Row: Operate(grantee: Grantee.Group(id: "operators")));
        WorldMutation form = new WorldMutation.FormGroup(Id: "operators", KindName: "authority", Principal: Seat);

        Assert.False(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertGrant(Principal: Seat, Row: Operate(grantee: Seat))));
        Assert.False(condition: Apply(fixture: fixture, mutation: new WorldMutation.Batch(Seat, (grantFirst ? [grant, form] : [form, grant]))));
        Assert.False(condition: fixture.Server.Grants.IsOperator(principal: Seat));
        Assert.Empty(collection: fixture.Server.Definition.Groups!.Groups);
    }
    [Fact]
    public void OperatorGroupsProtectMembershipAndRoleDefinitionsAndRejectAddonLaundering() {
        var document = Document() with {
            Groups = new WorldGroupsSection(Kinds: [Kind(WorldCapability.Operate)], Groups: [Group(Principal.Seat(slot: 1))], Ownership: []),
            GrantsRaw = [Operate(grantee: Grantee.Group(id: "operators"))],
        };
        using var fixture = Fixtures.FreshServer(document);

        Assert.True(condition: fixture.Server.Grants.IsOperator(principal: Principal.Seat(slot: 1)));
        Assert.False(condition: Apply(fixture: fixture, mutation: new WorldMutation.JoinGroup(GroupId: "operators", Member: Seat, Principal: Seat)));
        Assert.False(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertGroupKind(Seat, Kind(WorldCapability.Observe))));
        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.JoinGroup(Principal.Console, "operators", Seat)));
        Assert.False(condition: fixture.Server.Grants.IsOperator(principal: Seat)); // joining does not assign a role
        Assert.False(condition: Apply(fixture: fixture, mutation: new WorldMutation.JoinGroup(Seat, "operators", Principal.Addon(name: "guest"))));
        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.LeaveGroup(Principal.Console, "operators", Seat)));
        Assert.False(condition: fixture.Server.Grants.IsOperator(principal: Seat));
    }
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
    [Theory]
    public void OperatorTemplatesRequireOneSubjectAtReplica(WorldAdmissionTrustMode mode, string domain, string? subject, WorldDisclosureTier tier, bool allowed) {
        var entry = new WorldAdmissionEntry(Domain: domain, Subject: subject, Mode: mode, Disclosure: tier, Grants: [new(WorldCapability.Operate, GrantSubject.All)]);

        Assert.Equal(allowed, WorldAdmissionDoor.AllowsOperatorTemplates(entry: entry));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void PeerOperatorAdmissionIsOrderIndependentAndDisconnectClosesAuthority(bool operateFirst) {
        WorldAdmissionGrant operate = new(WorldCapability.Operate, GrantSubject.All);
        WorldAdmissionGrant program = new(WorldCapability.Mutate, GrantSubject.Section(section: WorldSection.Rules),
            Budget: 16, KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.Rules));
        WorldAdmissionEntry entry = new(Domain: "https://issuer.test", Subject: "owner", Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica, Grants: (operateFirst ? [operate, program] : [program, operate]));
        var document = Document();
        using var fixture = Fixtures.FreshServer(document with { Admission = [entry], PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4 } });
        var server = fixture.Server;

        Assert.True(condition: WorldAdmissionDoor.TryMatchOAuthEntry(entries: [entry], issuer: entry.Domain, subject: entry.Subject!, verdict: out var verdict));
        Assert.True(condition: server.TryAdmitPeerConnection(verdict, server.Definition.Admission, out var peer, out var reason), userMessage: reason);
        Assert.True(condition: server.Grants.IsOperator(principal: peer.Identity));
        var handles = server.Grants.HandleTable(peer.Identity, WorldCapability.Mutate);

        Assert.True(condition: handles.TryMintFor(GrantSubject.Section(section: WorldSection.Rules), out var programHandle));
        Assert.True(condition: WorldStateReadView.Of(server, peer.Identity).TryReadValue("secret", "value", out _, out _));
        Assert.True(condition: server.GrantTable.TryGrant(Operate(grantee: Seat), peer.Identity, out reason), userMessage: reason);
        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertStateRow(Principal: peer.Identity, Row: new(Name: CellName.Parse(candidate: "peer-row"), Kind: CellKind.Int, Cells: []))));
        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertWorldRule(Principal: peer.Identity, Rule: new(CellName.Parse(candidate: "peer-program"), [new ActionEffect.SetState("peer-row", Value: 1)]))));
        Assert.True(condition: server.GrantTable.Revoke(peer.Identity, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Assert.True(condition: handles.TryResolve(handle: programHandle, subject: out _)); // designation survives; authority does not
        Assert.False(condition: Apply(fixture: fixture, mutation: new WorldMutation.RemoveWorldRule(peer.Identity, CellName.Parse(candidate: "peer-program"))));
        Assert.True(condition: server.GrantTable.TryGrant(Operate(grantee: peer.Identity), Principal.Console, out reason), userMessage: reason);
        var closed = false;

        server.PeerDisconnected += p => { if (p == peer.Identity) { closed = true; Assert.False(condition: server.IsLivePeer(principal: p)); } };
        Assert.True(condition: server.DisconnectPeer(peer.Identity, Seat));
        Assert.True(condition: closed);
        Assert.False(condition: server.Grants.IsOperator(principal: peer.Identity));
        Assert.False(condition: server.GrantTable.TryGrant(Operate(grantee: Seat), peer.Identity, out _));
    }
    [Fact]
    public void OperateHasAnExplicitWorldWireTag() {
        Assert.True(condition: WorldWireTags.TryToWire(value: WorldCapability.Operate, wire: out var wire));
        Assert.True(condition: WorldWireTags.TryFromWire(value: out WorldCapability capability, wire: wire));
        Assert.Equal(actual: capability, expected: WorldCapability.Operate);
    }
}
