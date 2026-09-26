using Puck.Abstractions.Counting;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for where a seat's route change lands. A federated observer publishes an onward handoff on its socket worker,
/// but the change's subscribers rewrite the seat's routed document, bind its reads to a state mirror, and follow a
/// session mirror, none of which takes a lock. <see cref="WorldSeatAuthorityRouter.RouteChanged"/> therefore fires only
/// from <see cref="WorldSeatAuthorityRouter.DeliverRouteChanges"/>, on the thread that pumps and presents.
/// </summary>
public sealed class SeatRouteDeliveryLawTests {
    private sealed class Lease : IDisposable {
        public void Dispose() {
        }
    }

    private static WorldStateRow IntRow(string name, long value) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    private static long Reads(WorldStateMirror mirror) {
        Assert.True(condition: ((IWorkCounterSource)mirror).TryRead(
            kind: WorldStateMirror.Reads,
            value: out var value
        ));

        return value;
    }
    private static WorldSnapshot Snapshot(string authority, ulong tick) => new(
        Authority: authority,
        EngineTick: (tick * 1680UL),
        Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
        Revision: 0,
        StepTicks: 1680UL,
        Tick: tick
    );

    [Fact]
    public void ARouteChangePublishedOffThePresentationThreadLandsOnItsNextDelivery() {
        var local = Fixtures.BuildDocument().WithWorldState(rows: [IntRow(
            name: "heading",
            value: 0
        )]);
        var remote = Fixtures.BuildDocument().WithWorldState(rows: [IntRow(
            name: "heading",
            value: 1
        )]);
        var client = ClientFixtures.Client(definition: local);
        var routes = new WorldSeatAuthorityRouter();
        var bindings = new WorldSeatBindings(definition: local);
        IClientSink? remoteSink = null;

        client.DeliverSnapshot(snapshot: Snapshot(
            authority: "boot",
            tick: 1UL
        ));

        using var boot = new WorldAuthorityEndpoint(
            adjacencies: static () => null,
            clockOwnedHere: true,
            definition: () => local,
            identity: "boot",
            nextInputTick: static () => 2UL,
            observe: sink => {
                sink.DeliverDefinition(definition: local);
                sink.DeliverSnapshot(snapshot: Snapshot(
                    authority: "boot",
                    tick: 1UL
                ));

                return new Lease();
            },
            submissions: new SilentLink(definition: local)
        );
        using var north = new WorldAuthorityEndpoint(
            adjacencies: static () => null,
            clockOwnedHere: false,
            definition: () => remote,
            identity: "north",
            nextInputTick: static () => 6UL,
            observe: sink => {
                remoteSink = sink;
                sink.DeliverDefinition(definition: remote);
                sink.DeliverSnapshot(snapshot: Snapshot(
                    authority: "north",
                    tick: 5UL
                ));

                return new Lease();
            },
            submissions: new SilentLink(definition: remote)
        );

        // The boot claim is published before anything subscribes, so it owes no edge.
        var bootRoute = routes.Publish(
            endpoint: boot,
            entity: new WorldEntityAddress(
                Authority: "boot",
                Generation: 0,
                Index: 0
            ),
            slot: 0
        );

        bindings.FollowRoutes(
            client: client,
            routes: routes
        );

        var presentationThread = Environment.CurrentManagedThreadId;
        var deliveredOn = new List<int>();

        routes.RouteChanged += _ => deliveredOn.Add(item: Environment.CurrentManagedThreadId);
        Assert.Equal(
            actual: routes.DeliverRouteChanges(),
            expected: 0
        );

        // The seat reads the remote rows through the remote authority's own mirror; a registered slot there reads the
        // heading, and a delivery the mirror has not followed yet moves it.
        var routed = client.StateMirrorFor(endpoint: north);
        var heading = routed.Bind(
            conversion: WorldStateConversion.Number,
            token: "state.heading"
        );

        remote = Fixtures.BuildDocument().WithWorldState(rows: [IntRow(
            name: "heading",
            value: 2
        )]);
        remoteSink!.DeliverState(
            definition: remote,
            stamp: new WorldStateStamp(
                EngineTick: (5UL * 1680UL),
                Everything: false,
                MovedRows: new[] { 0 },
                Tick: 5UL
            )
        );

        var readsBefore = Reads(mirror: routed);
        var publisherThread = -1;
        var observer = new Thread(start: () => {
            publisherThread = Environment.CurrentManagedThreadId;
            _ = routes.CompareExchangeEntity(
                current: out _,
                entity: bootRoute.Entity,
                expected: bootRoute,
                slot: 0
            );
            _ = routes.Publish(
                endpoint: north,
                entity: new WorldEntityAddress(
                    Authority: "north",
                    Generation: 0,
                    Index: 1
                ),
                slot: 0
            );
        });

        observer.Start();
        observer.Join();
        Assert.NotEqual(
            actual: publisherThread,
            expected: presentationThread
        );

        // The claim itself is published: every reader of the table sees it at once.
        Assert.Same(
            actual: routes.Route(slot: 0).Endpoint,
            expected: north
        );

        // Nothing the claim's subscribers own moved on the publishing thread: the seat still presents the boot
        // document, and the remote mirror has not followed its pending delivery.
        bindings.GetRoutedState(
            definition: out var beforeDefinition,
            slot: 0,
            state: out var beforeState
        );

        Assert.Empty(collection: deliveredOn);
        Assert.Same(
            actual: beforeDefinition,
            expected: local
        );
        Assert.Null(@object: beforeState);
        Assert.Equal(
            actual: Reads(mirror: routed),
            expected: readsBefore
        );

        // The presentation turn delivers both publications as one edge for the seat, on its own thread, and the
        // seat's mutation and the mirror's follow land there.
        Assert.Equal(
            actual: routes.DeliverRouteChanges(),
            expected: 1
        );
        Assert.Equal(
            actual: Assert.Single(collection: deliveredOn),
            expected: presentationThread
        );

        bindings.GetRoutedState(
            definition: out var afterDefinition,
            slot: 0,
            state: out var afterState
        );

        Assert.Same(
            actual: afterDefinition,
            expected: north.Definition
        );
        Assert.Same(
            actual: afterState,
            expected: routed
        );
        Assert.True(condition: (Reads(mirror: routed) > readsBefore));
        Assert.True(condition: routed.TryNumber(
            slot: heading,
            value: out var value
        ));
        Assert.Equal(
            actual: value,
            expected: 2f
        );

        // A delivered edge is owed once.
        Assert.Equal(
            actual: routes.DeliverRouteChanges(),
            expected: 0
        );
        Assert.Single(collection: deliveredOn);
    }
}
