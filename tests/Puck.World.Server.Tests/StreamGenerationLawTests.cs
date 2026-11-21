using Puck.Abstractions.Presentation;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A stream's generation moves exactly on a cut its recipient's own delivery shows, and a mirror's ingress
/// takes records only from the stream that bound it, in order, until its span closes.</summary>
public sealed class StreamGenerationLawTests {
    private static StateCell Cell(string key, long value) => new(CellName.Parse(key), CellValue.Int(value));
    private static StateCell Member(string key) => new(CellName.Parse(key), CellValue.Bool(true));

    // A deck of two cards and their faces, an ordered public zone both start in, and an ordered zone only seat 1 reads.
    // The faces carry no policy of their own: which zone holds a card decides who reads its face.
    private static WorldDefinition Table() => Fixtures.BuildDocument() with {
        Admission = [new WorldAdmissionEntry(Domain: "*", Disclosure: WorldDisclosureTier.Replica, Mode: WorldAdmissionTrustMode.FederatedAuthority,
            Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: GrantSubject.All, Budget: 100)])],
        StateRaw = new WorldStateSection(World: [
            new WorldStateRow(Name: CellName.Parse("cards"), Kind: CellKind.Int, Capacity: 2, Cells: [Cell("a", 1), Cell("b", 1)]),
            new WorldStateRow(Name: CellName.Parse("face"), Kind: CellKind.Int, Domain: new StateDomain.KeysOf(CellName.Parse("cards")),
                Cells: [Cell("a", 10), Cell("b", 20)]),
            new WorldStateRow(Name: CellName.Parse("table"), Kind: CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true),
                Cells: [Member("a"), Member("b")]),
            new WorldStateRow(Name: CellName.Parse("held"), Kind: CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true),
                Visibility: new StateVisibility(Readers: ["seat1"]), Cells: []),
        ]),
    };
    private static (WorldSessionMirror Mirror, WorldSessionObservation Observation) Observe(WorldFixture fixture) {
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var observation = fixture.Server.TryObserveAsSession(refusal: out var refusal, sink: mirror, sourceAuthority: "spectator");

        Assert.True(observation is not null, refusal);
        return (mirror, observation!);
    }

    [Fact]
    public void ACardTheSpectatorReadMovingIntoAZoneItCannotReadStepsTheGenerationOnce() {
        using var fixture = Fixtures.FreshServer(Table());
        var (mirror, observation) = Observe(fixture);

        using (observation) {
            fixture.Step();
            var before = mirror.Span;

            Assert.True(before.Stream != 0UL);
            Assert.Equal(0L, before.Generation);
            // A changed value in a cell the spectator still reads is no cut.
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "face", "b", 21, WorldDocumentWriteKind.Set));
            Assert.True(fixture.Server.DrainAdministrative());
            fixture.Step();
            Assert.Equal(0L, mirror.Span.Generation);
            // Card a leaves the public zone for the zone only seat 1 reads: the spectator loses the face it was shown.
            fixture.Server.EnqueueMutation(new WorldMutation.RemoveStateCell(Key: "a", Principal: Principal.Console, Row: "table"));
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "held", "a", 1, WorldDocumentWriteKind.Set));
            Assert.True(fixture.Server.DrainAdministrative());
            fixture.Step();
            fixture.Step();

            var after = mirror.Span;

            Assert.Equal(before.Stream, after.Stream);
            Assert.Equal(1L, after.Generation);
            Assert.False(after.Closed);
            Assert.Equal(0L, mirror.IngressRefusals);
        }
    }

    [Fact]
    public void BodyMembershipUnderAnUnchangedPolicyNeverCutsAndARedactingPolicyChangeDoes() {
        var cursor = new WorldDeliveryCursor();

        cursor.ObserveBodies(new(Mode: WorldObserverDisclosureMode.Radius, Radius: 8f));
        cursor.ObserveBodies(new(Mode: WorldObserverDisclosureMode.Radius, Radius: 8f, UpdateSeconds: 0f));
        Assert.Equal(0L, cursor.Generation);
        Assert.False(cursor.TakeCut());
        cursor.ObserveBodies(WorldObserverDisclosure.Default);
        Assert.Equal(0L, cursor.Generation);
        cursor.ObserveBodies(new(Mode: WorldObserverDisclosureMode.Radius, Radius: 4f));
        Assert.Equal(1L, cursor.Generation);
        Assert.True(cursor.TakeCut());
        Assert.False(cursor.TakeCut());
    }

    [Fact]
    public void AMirrorDropsARecordOfAnEarlierGenerationAndOneOfAnotherStream() {
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var first = Fixtures.BuildDocument();
        var stale = first with { DocumentId = "stale" };
        var foreign = first with { DocumentId = "foreign" };

        mirror.DeliverDefinition(definition: first, stream: new WorldStreamStamp(Generation: 1L, Ordinal: 5L, Stream: 7UL), version: default);
        mirror.DeliverDefinition(definition: stale, stream: new WorldStreamStamp(Generation: 0L, Ordinal: 6L, Stream: 7UL), version: default);
        mirror.DeliverDefinition(definition: foreign, stream: new WorldStreamStamp(Generation: 1L, Ordinal: 6L, Stream: 9UL), version: default);
        mirror.DeliverDefinition(definition: stale, stream: new WorldStreamStamp(Generation: 1L, Ordinal: 5L, Stream: 7UL), version: default);
        mirror.DeliverDefinition(definition: foreign, stream: WorldStreamStamp.Unspanned, version: default);

        Assert.Same(first, mirror.Definition);
        Assert.Equal(4L, mirror.IngressRefusals);
        Assert.Equal(new WorldStreamSpan(Closed: false, Generation: 1L, Ordinal: 5L, Stream: 7UL), mirror.Span);
    }

    [Fact]
    public void ALostConnectionOrATerminalRecordClosesTheSpanAndWithdrawsWhatTheMirrorHeld() {
        var placeholder = WorldProjection.Undisclosed;
        var delivered = Fixtures.BuildDocument();

        var lost = new WorldSessionMirror(placeholder: placeholder);
        lost.DeliverDefinition(definition: delivered, stream: new WorldStreamStamp(Generation: 0L, Ordinal: 1L, Stream: 3UL), version: default);
        // A close of a stream the mirror does not hold changes nothing.
        lost.CloseSpan(stream: 9UL);
        Assert.False(lost.Span.Closed);
        Assert.Same(delivered, lost.Definition);
        lost.CloseSpan(stream: 3UL);
        Assert.True(lost.Span.Closed);
        Assert.Same(placeholder, lost.Definition);
        // A late record of the closed stream never brings it back; the next admission's stream binds.
        lost.DeliverDefinition(definition: delivered, stream: new WorldStreamStamp(Generation: 0L, Ordinal: 2L, Stream: 3UL), version: default);
        Assert.Same(placeholder, lost.Definition);
        lost.DeliverDefinition(definition: delivered, stream: new WorldStreamStamp(Generation: 0L, Ordinal: 1L, Stream: 4UL), version: default);
        Assert.Same(delivered, lost.Definition);
        Assert.Equal(4UL, lost.Span.Stream);

        using var fixture = Fixtures.FreshServer(Table());
        var (mirror, observation) = Observe(fixture);

        fixture.Step();
        Assert.NotSame(WorldProjection.Undisclosed, mirror.Definition);
        observation.Dispose();
        Assert.True(mirror.Span.Closed);
        Assert.Same(WorldProjection.Undisclosed, mirror.Definition);
    }

    [Fact]
    public void NoStreamThatClosedOnAMirrorBindsItAgainAndNothingUnspannedReachesOneThatHeldASpan() {
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var first = Fixtures.BuildDocument();
        var second = first with { DocumentId = "second" };
        var replayed = first with { DocumentId = "replayed" };

        mirror.DeliverDefinition(definition: first, stream: new WorldStreamStamp(Generation: 0L, Ordinal: 1L, Stream: 1UL), version: default);
        mirror.CloseSpan(stream: 1UL);
        mirror.DeliverDefinition(definition: second, stream: new WorldStreamStamp(Generation: 0L, Ordinal: 1L, Stream: 2UL), version: default);
        Assert.Same(second, mirror.Definition);
        mirror.DeliverCut(stream: new WorldStreamStamp(Generation: 0L, Ordinal: 2L, Stream: 2UL), terminal: true);
        Assert.Same(WorldProjection.Undisclosed, mirror.Definition);
        // The first stream closed before the second bound: it stays closed, as does an unordered record.
        mirror.DeliverDefinition(definition: replayed, stream: new WorldStreamStamp(Generation: 0L, Ordinal: 1L, Stream: 1UL), version: default);
        mirror.DeliverDefinition(definition: replayed, stream: WorldStreamStamp.Unspanned, version: default);
        Assert.Same(WorldProjection.Undisclosed, mirror.Definition);
        Assert.Equal(2L, mirror.IngressRefusals);
    }

    [Fact]
    public void EveryCutWithdrawsTheMirrorAndTheNewGenerationsFirstSnapshotSeedsBothPoseEndpoints() {
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var document = Fixtures.BuildDocument();
        var ordinal = 0L;
        WorldStreamStamp Next(long generation) => new(Generation: generation, Ordinal: ++ordinal, Stream: 5UL);
        static WorldSnapshot At(ulong tick, float x) => new(Authority: "a", Revision: 0, StepTicks: 1UL, Tick: tick, EngineTick: tick,
            Entries: new[] { new EntitySnapshot(Active: true, BodyColor: System.Numerics.Vector3.One, CatalogRig: 0, Continuity: EntityContinuity.Continuous,
                Generation: 1, Index: 0, Kit: 0, Look: 0, Orientation: System.Numerics.Quaternion.Identity, Position: new System.Numerics.Vector3(x, 0f, 0f)) });

        mirror.DeliverDefinition(definition: document, stream: Next(0L), version: default);
        mirror.DeliverSnapshot(snapshot: At(1UL, 1f), stream: Next(0L));
        mirror.DeliverSnapshot(snapshot: At(2UL, 2f), stream: Next(0L));
        Assert.Equal(1f, mirror.PreviousPosition(index: 0).X);
        // A cut that does not end the stream withdraws all the same: nothing of the ended span stays showable while the
        // new generation's document is still on its way.
        mirror.DeliverCut(stream: Next(1L), terminal: false);
        Assert.Same(WorldProjection.Undisclosed, mirror.Definition);
        Assert.False(mirror.IsActive(index: 0));
        Assert.False(mirror.Span.Closed);
        mirror.DeliverDefinition(definition: document, stream: Next(1L), version: default);
        mirror.DeliverSnapshot(snapshot: At(3UL, 3f), stream: Next(1L));
        Assert.True(mirror.IsActive(index: 0));
        Assert.Equal(3f, mirror.PreviousPosition(index: 0).X);
        Assert.Equal(3f, mirror.CurrentPosition(index: 0).X);
    }

    [Fact]
    public void AMirrorsDeliverySpanStaysEndedWhileItsCutWithdrawsAndOpensAgainOnlyAfter() {
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var document = Fixtures.BuildDocument();
        var ordinal = 0L;
        WorldStreamStamp Next(long generation) => new(Generation: generation, Ordinal: ++ordinal, Stream: 5UL);
        var duringWithdrawal = new List<bool>();

        mirror.DeliverDefinition(definition: document, stream: Next(0L), version: default);

        var before = mirror.DeliverySpan;

        Assert.True(DeliveryLatch.Capture().IsCurrent(span: before));
        // A presentation that reads the mirror's span while the cut withdraws it must read an ended span: the old
        // content is still being dropped, and pairing it with the next span would carry it past the cut.
        mirror.DocumentDelivered += _ => duringWithdrawal.Add(item: DeliveryLatch.Capture().IsCurrent(span: mirror.DeliverySpan));
        mirror.DeliverCut(stream: Next(1L), terminal: false);

        Assert.NotEmpty(duringWithdrawal);
        Assert.DoesNotContain(true, duringWithdrawal);

        var after = mirror.DeliverySpan;

        Assert.False(DeliveryLatch.Capture().IsCurrent(span: before));
        Assert.NotSame(before, after);
        Assert.True(DeliveryLatch.Capture().IsCurrent(span: after));
        // A generation step that arrives on a content record withdraws the same way before the record applies.
        duringWithdrawal.Clear();
        mirror.DeliverDefinition(definition: document, stream: Next(2L), version: default);
        Assert.NotEmpty(duringWithdrawal);
        Assert.False(duringWithdrawal[0]);
        Assert.Same(document, mirror.Definition);
        Assert.False(DeliveryLatch.Capture().IsCurrent(span: after));
        Assert.True(DeliveryLatch.Capture().IsCurrent(span: mirror.DeliverySpan));
    }

    [Fact]
    public void AHiddenZoneInstalledOverARowTheSpectatorReadWholeCutsOnlyWhenACellItReadIsHidden() {
        using var fixture = Fixtures.FreshServer(Table() with {
            StateRaw = new WorldStateSection(World: [.. Table().State.Where(row => row.Name.Value != "held")]),
        });
        var (mirror, observation) = Observe(fixture);

        using (observation) {
            fixture.Step();
            // An empty zone only seat 1 reads, over the cards the spectator read whole: nothing it read is hidden.
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(Principal.Console, new WorldStateRow(Name: CellName.Parse("secret"), Kind: CellKind.Bool,
                Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true), Visibility: new StateVisibility(Readers: ["seat1"]), Cells: [])));
            Assert.True(fixture.Server.DrainAdministrative());
            fixture.Step();
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "face", "b", 21, WorldDocumentWriteKind.Set));
            Assert.True(fixture.Server.DrainAdministrative());
            fixture.Step();
            Assert.Equal(0L, mirror.Span.Generation);
            // Card a enters the zone: its cell and face are hidden from the spectator now, which is a loss.
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "secret", "a", 1, WorldDocumentWriteKind.Set));
            Assert.True(fixture.Server.DrainAdministrative());
            fixture.Step();
            fixture.Step();
            Assert.Equal(1L, mirror.Span.Generation);
        }
    }

    [Fact]
    public void APresentationSpectatorShownADealtCardCutsWhenTheCardMovesIntoAZoneItCannotRead() {
        var deck = new WorldPlacement(Id: "deck", PrototypeId: "card", Position: new DocumentVector3(value: System.Numerics.Vector3.Zero), YawDegrees: 0f, Scale: 1f,
            Distribution: new WorldDistribution(
                Region: new WorldDistributionRegion.Lattice(StepA: new DocumentVector3(value: System.Numerics.Vector3.UnitX), CountA: 2,
                    StepB: new DocumentVector3(value: System.Numerics.Vector3.UnitZ), CountB: 1),
                Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)),
            Deal: new WorldPlacementDeal(Row: "cards"));
        var table = Table();

        using var fixture = Fixtures.FreshServer(table with {
            Admission = [new WorldAdmissionEntry(Domain: "*", Disclosure: WorldDisclosureTier.Presentation, Mode: WorldAdmissionTrustMode.FederatedAuthority,
                Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: GrantSubject.All, Budget: 100)])],
            CreationsRaw = [Puck.World.Testing.CreationFixtures.UnitSphere(id: "card")],
            PlacementRowsRaw = [deck],
        });
        var (mirror, observation) = Observe(fixture);

        using (observation) {
            fixture.Step();
            Assert.Equal(0L, mirror.Span.Generation);
            // The cards cross to a presentation recipient only as the placements dealt from them, never as a row: losing
            // one is a cut all the same.
            fixture.Server.EnqueueMutation(new WorldMutation.RemoveStateCell(Key: "a", Principal: Principal.Console, Row: "table"));
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "held", "a", 1, WorldDocumentWriteKind.Set));
            Assert.True(fixture.Server.DrainAdministrative());
            fixture.Step();
            fixture.Step();
            Assert.Equal(1L, mirror.Span.Generation);
            Assert.Equal(0L, mirror.IngressRefusals);
        }
    }
}
