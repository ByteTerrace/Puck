using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

public sealed partial class SessionObservationLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ASessionStateReadViewCarriesOnlyItsDeliveredProjection(bool visible) {
        using var fixture = Fixtures.FreshServer(definition: WithHue(
            bound: false,
            visibility: (visible ? new StateVisibility() : null)
        ));

        var (observation, mirror, refusal) = Observe(fixture: fixture);

        Assert.NotNull(@object: observation);
        Assert.Empty(collection: refusal);
        using var release = observation;
        var view = WorldStateReadView.Of(reader: observation.Session, server: fixture.Server);
        var cells = view.Definition.State.Where(predicate: row => (row.Name.Value == "hue")).Sum(selector: row => (row.Cells?.Count ?? 0));

        Assert.Equal(actual: cells, expected: (visible ? 1 : 0));
        Assert.Equal(expected: mirror.Definition.Grants.Count, actual: view.Definition.Grants.Count);
        Assert.Equal(expected: 0L, actual: view.ArenaBytes);
    }
    [InlineData(WorldDisclosureTier.Frames)]
    [InlineData(WorldDisclosureTier.Presentation)]
    [InlineData(WorldDisclosureTier.Replica)]
    [Theory]
    public void SessionReadDoorsRefuseFramesAndRetiredEpochs(WorldDisclosureTier tier) {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: tier));

        var (observation, _, refusal) = Observe(fixture: fixture);

        Assert.NotNull(@object: observation);
        Assert.Empty(collection: refusal);
        var session = observation.Session;
        var query = new WorldQuery.StateObservations();

        Assert.Equal(expected: (tier == WorldDisclosureTier.Frames), actual: fixture.Server.AnswerSubmittedQuery(principal: session, query: query).Refused);
        if (tier == WorldDisclosureTier.Frames) {
            Assert.Throws<InvalidOperationException>(testCode: () => WorldStateReadView.Of(reader: session, server: fixture.Server));
        } else {
            Assert.NotNull(@object: WorldStateReadView.Of(reader: session, server: fixture.Server));
        }
        observation.Dispose();
        var (replacement, _, _) = Observe(fixture: fixture);

        Assert.NotNull(@object: replacement);
        using var release = replacement;

        Assert.NotEqual(expected: session, actual: replacement.Session);
        Assert.True(condition: fixture.Server.AnswerSubmittedQuery(principal: session, query: query).Refused);
        Assert.Throws<InvalidOperationException>(testCode: () => WorldStateReadView.Of(reader: session, server: fixture.Server));
    }
    [InlineData(WorldObserverDisclosureMode.All)]
    [InlineData(WorldObserverDisclosureMode.SelfOnly)]
    [Theory]
    public void ASessionSinkAllocatesNothingForSteadyDelivery(WorldObserverDisclosureMode mode) {
        var definition = Document(tier: WorldDisclosureTier.Replica);

        using var fixture = Fixtures.FreshServer(definition: (definition with {
            PopulationRaw = (definition.Population with { Disclosure = new WorldObserverDisclosure(Mode: mode) }),
        }));
        // The server's one output hub, reached through the population it narrates through, so the law times the
        // session sink's own delivery rather than a whole step around it.
        var hub = fixture.Server.Population.NarrationHub!;

        var (observation, mirror, refusal) = Observe(fixture: fixture);

        Assert.NotNull(@object: observation);
        Assert.Empty(collection: refusal);
        using var release = observation;
        var snapshot = new WorldSnapshot(Entries: ReadOnlyMemory<EntitySnapshot>.Empty, Revision: 1, StepTicks: 1UL, Tick: 1UL);
        var stamp = new WorldStateStamp(EngineTick: 1UL, Everything: false, MovedRows: ReadOnlyMemory<int>.Empty, Tick: 1UL);
        var revision = mirror.DefinitionRevision;

        for (var index = 0; (index < 100); index++) {
            hub.DeliverSnapshot(snapshot: in snapshot);
            hub.DeliverState(definition: fixture.Server.Definition, stamp: in stamp, version: fixture.Server.DocumentVersion);
            _ = mirror.FollowState();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 100); index++) {
            hub.DeliverSnapshot(snapshot: in snapshot);
            hub.DeliverState(definition: fixture.Server.Definition, stamp: in stamp, version: fixture.Server.DocumentVersion);
            _ = mirror.FollowState();
        }
        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));
        Assert.Equal(expected: revision, actual: mirror.DefinitionRevision);
    }
    [Fact]
    public void AnOverflowEndsTheSessionBeforeItsWireCanDrain() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));
        var sink = new WorldFederationProjectionSink(
            authority: fixture.Server.AuthorityIdentity,
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            revision: () => fixture.Server.Population.Revision,
            tier: WorldDisclosureTier.Replica
        );
        using var observation = fixture.Server.TryObserveAsSession(refusal: out var refusal, sink: sink, sourceAuthority: Viewer);

        Assert.NotNull(@object: observation);
        Assert.Empty(collection: refusal);
        // Nothing drains the wire, so the primer and the ticks after it fill the queue; the step that overflows it
        // must end the session then, not when a reader that never comes drains what was retained.
        for (var step = 0; ((step <= WorldFederationProjectionSink.PendingDeliveryLimit) && (sink.DetachReason is null)); step++) {
            fixture.Step();
        }
        Assert.Equal(expected: WorldFederationProjectionSink.BackpressureDetachReason, actual: sink.DetachReason);
        Assert.True(condition: observation.Ended);
        Assert.False(condition: fixture.Server.IsLiveSession(principal: observation.Session));
        Assert.True(condition: fixture.Server.AnswerSubmittedQuery(principal: observation.Session, query: new WorldQuery.StateObservations()).Refused);
        Assert.Throws<InvalidOperationException>(testCode: () => WorldStateReadView.Of(reader: observation.Session, server: fixture.Server));
    }
}
