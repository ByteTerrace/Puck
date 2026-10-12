using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a seat routed to an authority the boot presentation cannot map it into (another document that no
/// adjacency relates to the boot world) is presented in that authority's world, never at foreign coordinates in the boot
/// frame. Two seats are split across two worlds: seat 1 stays home and seat 2 is routed to a destination that runs its
/// own document. <see cref="WorldContinuum.PresentedElsewhere"/> names the destination for seat 2 alone. The boot frame
/// holds no pose for seat 2, while its presented pose is the destination's own. The seat's view, drawn by the
/// destination's <see cref="WorldRoutedScene"/>, emits exactly the program the destination's delivered definition
/// composes through the session emitter, not the boot world's, and frames it with the seat's own camera.
/// </summary>
[Collection(SceneProbeCollection.Name)]
public sealed partial class WorldRoutedPresentationLawTests {
    private const string Away = "north";
    private const string Home = "boot";

    private static readonly Vector3 HomePose = new(x: 1f, y: 0f, z: 1f);
    private static readonly Vector3 AwayPose = new(x: 5f, y: 0f, z: 7f);

    private sealed class Lease : IDisposable {
        public void Dispose() {
        }
    }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public IReadOnlyList<WorldAdjacencyProjection> Projections { get; init; } = [];

        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => Projections;
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;

            return false;
        }
    }

    // One active body at index 0, posed.
    private static WorldSnapshot Snapshot(string authority, Vector3 position) => new(
        Authority: authority,
        EngineTick: 1680UL,
        Entries: new[] { new EntitySnapshot(
            Active: true,
            BodyColor: Vector3.One,
            CatalogRig: 0,
            Continuity: EntityContinuity.Continuous,
            Generation: 1,
            Index: 0,
            Kit: 0,
            Look: 0,
            Orientation: Quaternion.Identity,
            Position: position
        ) },
        Revision: 0,
        StepTicks: 1680UL,
        Tick: 1UL
    );
    private static WorldAuthorityEndpoint Endpoint(string identity, WorldDefinition definition, Vector3 position) => new(
        adjacencies: static () => null,
        clockOwnedHere: false,
        definition: () => definition,
        identity: identity,
        nextInputTick: static () => 2UL,
        observe: sink => {
            sink.DeliverDefinition(definition: definition, version: default);
            sink.DeliverSnapshot(snapshot: Snapshot(
                authority: identity,
                position: position
            ));

            return new Lease();
        },
        submissions: new SilentLink(definition: definition)
    );
    // The destination runs a document of its own, and stands a door the home world has not.
    private static WorldDefinition AwayDocument() => (Fixtures.BuildDocument() with {
        CreationsRaw = [PortalFixtures.BuildDoorCreation()],
        DocumentId = "away",
        PlacementRowsRaw = [new WorldPlacement(
            Id: "door",
            Position: new Vector3(x: 2f, y: 0f, z: -4f),
            PrototypeId: "door",
            Scale: 2f,
            YawDegrees: 0f
        )],
    });
    private static SdfFrame Capture(ISdfFrameSource source) => source.CaptureFrame(
        deltaSeconds: 0f,
        height: 36U,
        interpolationAlpha: 0f,
        width: 64U
    );

    [Fact]
    public void MixedLayoutsLatchViewIndicesAndRetireScenesWhenSeatsReturnHome() {
        using var state = new TemporaryDirectory(prefix: "puck-routed-layout-");
        var host = state.Own(owner: WorldBootHarness.Compose(
            edit: definition => (definition with {
                ViewsRaw = (definition.Views with {
                    Graphs = [new WorldViewGraph(Name: "pane", Package: "sdf.world")],
                    Layouts = [new WorldViewLayout(Name: "mixed", Slots: [
                        new WorldViewSlot(Camera: definition.Cameras[0].Name, Height: 1f, Width: 0.2f, X: 0f, Y: 0f),
                        new WorldViewSlot(Height: 1f, Instance: "pane", Width: 0.2f, X: 0.2f, Y: 0f),
                        new WorldViewSlot(Height: 1f, Width: 0.2f, X: 0.4f, Y: 0f),
                        new WorldViewSlot(Camera: definition.Cameras[0].Name, Height: 1f, Width: 0.2f, X: 0.6f, Y: 0f),
                        new WorldViewSlot(Height: 1f, Width: 0.2f, X: 0.8f, Y: 0f),
                    ])],
                }),
            }),
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: state,
            world: "tests/Puck.Counters/counters.puck"
        ).Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var routes = host.Services.GetRequiredService<WorldSeatAuthorityRouter>();

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            _ = client.Roster.VacateSeat(slot: slot);
        }
        _ = client.Roster.OccupySeat(profile: null, slot: 0);
        _ = client.Roster.OccupySeat(profile: null, slot: 1);
        using var boot = Endpoint(definition: client.Definition, identity: Home, position: HomePose);
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        _ = routes.Publish(endpoint: boot, entity: boot.Mirror.Address(index: 0), slot: 1);
        var frame = Capture(source: presenter);

        Assert.Equal(actual: frame.Views.Count, expected: 4);
        Assert.True(condition: presenter.TryRoutedView(index: out var index, scene: out var scene, view: 1));
        Assert.Equal(actual: index, expected: 0);
        Assert.Equal(actual: Capture(source: scene!.FrameSource).Views[0], expected: frame.Views[1]);
        foreach (var view in new[] { -1, 0, 2, 3, 4, int.MaxValue }) {
            Assert.False(condition: presenter.TryRoutedView(index: out _, scene: out _, view: view));
        }
        _ = routes.Publish(endpoint: boot, entity: boot.Mirror.Address(index: 0), slot: 0);
        _ = Capture(source: presenter);
        Assert.False(condition: presenter.Presents(scene: scene));
        Assert.False(condition: presenter.TryRoutedView(index: out _, scene: out _, view: 1));

        _ = client.Roster.VacateSeat(slot: 0);
        _ = client.Roster.VacateSeat(slot: 1);
        client.DeliverDefinition(definition: (client.Definition with { ViewsRaw = (client.Definition.Views with { Layouts = [] }) }), version: default);
        _ = Capture(source: presenter);
        Assert.Single(collection: presenter.CaptureFrame(deltaSeconds: 1f, height: 36U, interpolationAlpha: 1f, width: 64U).Views);
        Assert.False(condition: presenter.TryRoutedView(index: out _, scene: out _, view: 0));
    }
}
