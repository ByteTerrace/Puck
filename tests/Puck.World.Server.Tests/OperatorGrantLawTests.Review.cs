using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class OperatorGrantLawTests {
    [Fact]
    public void UndoRefusesAConflictingGrantBeforeReplacingSolids() {
        var row = new WorldGrant(Seat, WorldCapability.Drive, GrantSubject.Body(index: 3), false, Budget: 4);
        using var fixture = Fixtures.FreshServer(Document() with { GrantsRaw = [row] });
        var server = fixture.Server;

        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.RemoveGrant(Principal: Principal.Console, Target: row)));
        Assert.True(condition: server.GrantTable.Revoke(Principal.Seat(slot: 3), WorldCapability.Drive, row.Subject, Principal.Console));
        Assert.True(condition: server.GrantTable.TryGrant(row with { Grantee = Principal.Seat(slot: 1), Exclusive = true }, Principal.Console, out var reason), userMessage: reason);
        var definition = server.Definition;
        var solids = server.SolidField;
        var revision = server.SolidRevision;
        WorldEditEcho? answer = null;

        server.EchoTap = echo => answer = echo;
        server.EnqueueUndo(1, Principal.Console);
        fixture.Step();
        Assert.True(condition: answer?.Rejected);
        Assert.Same(definition, server.Definition);
        Assert.Same(solids, server.SolidField);
        Assert.Equal(revision, server.SolidRevision);
    }
    [Fact]
    public void AdmissionKeepsGoodRowsBesideARefusedReservation() {
        var document = Document();
        WorldAdmissionEntry entry = new(Domain: "https://issuer.test", Subject: "visitor", Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica, Grants: [
                new(WorldCapability.Control, GrantSubject.Composition, Exclusive: true),
                new(WorldCapability.Observe, GrantSubject.State(name: "public"), Budget: 4),
            ]);
        using var fixture = Fixtures.FreshServer(document with { Admission = [entry], PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4 } });

        Assert.True(condition: WorldAdmissionDoor.TryMatchOAuthEntry(entries: [entry], issuer: entry.Domain, subject: entry.Subject!, verdict: out var verdict));
        Assert.True(condition: fixture.Server.TryAdmitPeerConnection(verdict, fixture.Server.Definition.Admission, out var peer, out var reason), userMessage: reason);
        Assert.True(condition: fixture.Server.Grants.Allows(peer.Identity, WorldCapability.Observe, GrantSubject.State(name: "public")).IsAllowed);
        Assert.DoesNotContain(collection: fixture.Server.GrantRows(principal: peer.Identity), filter: row => row.Exclusive);
    }
    [InlineData(WorldRebuildKind.Load)]
    [InlineData(WorldRebuildKind.Reload)]
    [Theory]
    public void RefusedRebuildLeavesDefinitionSolidsAndInputUntouched(WorldRebuildKind kind) {
        using var fixture = Fixtures.FreshServer(Document());

        fixture.JoinSeat();
        var server = fixture.Server;
        var definition = server.Definition;
        var input = server.Input.Admit(bodyIndex: 0, principal: Seat, sources: []);
        var epoch = input.Epoch;

        Assert.True(condition: server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        var solidRevision = checkpoint!.Server.SolidRevision;
        var candidate = definition with { GrantsRaw = [new(Seat, WorldCapability.Drive, GrantSubject.Body(index: 3), false)] };
        WorldEditEcho? answer = null;

        server.EchoTap = echo => answer = echo;
        server.EnqueueRebuild(new WorldRebuildRequest(Kind: kind, Definition: candidate, Force: true,
            ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate)),
            Origin: new WorldRebuildOrigin.File(Path: "d2-refused.world.json")), Principal.Console);
        fixture.Step();
        Assert.True(condition: answer?.Rejected);
        Assert.Same(definition, server.Definition);
        Assert.True(condition: server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out checkpoint, out reason), userMessage: reason);
        Assert.Equal(solidRevision, checkpoint!.Server.SolidRevision);
        Assert.Equal(epoch, input.Epoch);
    }
    [Fact]
    public void BootRefusesOnlyTheBadAuthoredGrant() {
        using var fixture = Fixtures.FreshServer(Document() with {
            GrantsRaw = [
            new(Seat, WorldCapability.Drive, GrantSubject.Body(index: 3), false),
            new(Seat, WorldCapability.Drive, GrantSubject.Body(index: 2), false, Budget: 7),
        ],
        });

        Assert.False(condition: fixture.Server.Grants.Allows(Seat, WorldCapability.Drive, GrantSubject.Body(index: 3)).IsAllowed);
        Assert.True(condition: fixture.Server.Grants.Allows(Seat, WorldCapability.Drive, GrantSubject.Body(index: 2)).IsAllowed);
    }
    [Fact]
    public void RefusedCheckpointAuthorityLeavesTheLiveWorldUntouched() {
        using var fixture = Fixtures.FreshServer(Document());
        var server = fixture.Server;

        Assert.True(condition: server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertStateCell(Principal.Console, "public", StateRow.SlotKey.Value, 9, WorldDocumentWriteKind.Set)));
        var before = fixture.DefinitionBytes();
        var input = server.Input;
        var malformed = checkpoint! with {
            Grants = checkpoint!.Grants with {
                Grantees = [.. checkpoint.Grants.Grantees,
            new(Principal.Addon(name: "illicit"), [], [], [], [], [], [GrantSubject.All], [])],
            },
        };

        Assert.Throws<InvalidOperationException>(testCode: () => server.Persistence.RestoreCheckpoint(malformed));
        Assert.Equal(before, fixture.DefinitionBytes());
        Assert.Same(input, server.Input);
    }
    [Fact]
    public void ExclusiveReservationsBindOperatorsAndOperatorPayloadsUseTheirOwnRows() {
        using var fixture = Fixtures.FreshServer(Document());
        var grants = fixture.Server.GrantTable;

        Assert.True(condition: grants.TryGrant(Operate(grantee: Seat), Principal.Console, out var reason), userMessage: reason);
        var hold = FixedQ4816.FromDouble(value: 0.25).Value;

        Assert.True(condition: grants.TryGrant(new(Seat, WorldCapability.Drive, GrantSubject.Body(index: 0), true, Budget: 8, HoldCeiling: hold), Principal.Console, out reason), userMessage: reason);
        Assert.False(condition: grants.Allows(Principal.Console, WorldCapability.Drive, GrantSubject.Body(index: 0)).IsAllowed);
        Assert.True(condition: grants.Allows(Seat, WorldCapability.Drive, GrantSubject.Body(index: 0)).IsAllowed);
        Assert.Equal(hold, grants.HoldCeiling(grantee: Seat, subject: GrantSubject.Body(index: 0)));
        for (var slot = 0; (slot < fixture.Server.Population.LocalSeatCount); slot++) {
            if (slot != 1) { Assert.True(condition: grants.Revoke(Principal.Seat(slot: slot), WorldCapability.Control, GrantSubject.Composition, Principal.Console)); }
        }
        Assert.True(condition: grants.Revoke(Principal.Console, WorldCapability.Control, GrantSubject.Composition, Principal.Console));
        Assert.True(condition: grants.TryGrant(new(Principal.Seat(slot: 1), WorldCapability.Control, GrantSubject.Composition, true), Principal.Console, out reason), userMessage: reason);
        Assert.False(condition: grants.Allows(Seat, WorldCapability.Control, GrantSubject.Composition).IsAllowed);
        Assert.False(condition: grants.Allows(Principal.Console, WorldCapability.Control, GrantSubject.Composition).IsAllowed);
    }
    [Fact]
    public void ReplayStartDiscardsRetiredSessionEpochsAndTheOrdinalBound() {
        using var fixture = Fixtures.FreshServer(Document() with {
            Admission = [new(Domain: "portal", Mode: WorldAdmissionTrustMode.FederatedAuthority,
            Disclosure: WorldDisclosureTier.Replica, Grants: [])],
        });
        var server = fixture.Server;

        Assert.True(condition: server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var start, out var reason), userMessage: reason);
        Assert.True(condition: server.TryAdmitSession(refusal: out reason, session: out var first, sourceAuthority: "portal", tier: out _), userMessage: reason);
        Assert.True(condition: server.EndSession(refusal: out reason, session: first), userMessage: reason);
        Assert.True(condition: server.TryAdmitSession(refusal: out reason, session: out var later, sourceAuthority: "portal", tier: out _), userMessage: reason);
        Assert.NotEqual(actual: later, expected: first);
        server.Persistence.RestoreReplayStart(start!);
        Assert.Equal(0, server.GrantTable.SessionOrdinalBound);
        Assert.Empty(collection: server.GrantTable.Capture().SessionEpochs);
        Assert.True(condition: server.TryAdmitSession(refusal: out reason, session: out var repeated, sourceAuthority: "portal", tier: out _), userMessage: reason);
        Assert.Equal(actual: repeated, expected: first);
        Assert.False(condition: server.GrantTable.TryGrant(Operate(grantee: repeated), Principal.Console, out _));
    }
    [Fact]
    public void ACellWriteDoesNotInvalidateGrantHandleProjections() {
        using var fixture = Fixtures.FreshServer(Document());
        var revision = fixture.Server.GrantTable.Capture().Revision;

        Assert.True(condition: Apply(fixture: fixture, mutation: new WorldMutation.UpsertStateCell(Seat, "public", StateRow.SlotKey.Value, 4, WorldDocumentWriteKind.Set)));
        Assert.Equal(revision, fixture.Server.GrantTable.Capture().Revision);
    }
    [Fact]
    public void ExplicitOperatorRowsKeepTheirBudgetAfterRevocation() {
        using var fixture = Fixtures.FreshServer(Document());
        var grants = fixture.Server.GrantTable;

        Assert.True(condition: grants.TryGrant(Operate(grantee: Seat), Principal.Console, out var reason), userMessage: reason);
        var row = new WorldGrant(Seat, WorldCapability.Drive, GrantSubject.Body(index: 3), false);

        Assert.False(condition: grants.TryGrant(row, Principal.Console, out _));
        Assert.True(condition: grants.TryGrant(row with { Budget = 5 }, Principal.Console, out reason), userMessage: reason);
        Assert.True(condition: grants.Revoke(Seat, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Assert.True(condition: grants.TryGetBudget(Seat, WorldCapability.Drive, GrantSubject.Body(index: 3), out var budget));
        Assert.Equal(actual: budget, expected: 5);
    }
}
