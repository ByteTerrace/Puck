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

        cursor.ObserveBodies(mode: WorldObserverDisclosureMode.Radius, radius: 8f);
        cursor.ObserveBodies(mode: WorldObserverDisclosureMode.Radius, radius: 8f);
        Assert.Equal(0L, cursor.Generation);
        Assert.False(cursor.TakeCut());
        cursor.ObserveBodies(mode: WorldObserverDisclosureMode.All, radius: null);
        Assert.Equal(0L, cursor.Generation);
        cursor.ObserveBodies(mode: WorldObserverDisclosureMode.Radius, radius: 4f);
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
        lost.CloseSpan();
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
}
