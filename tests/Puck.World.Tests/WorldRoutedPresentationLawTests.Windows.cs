using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldRoutedPresentationLawTests {
    // A window onto a world is a view of the scene the seats presented there render (WorldFramePresenter.AttachWindow),
    // after their views and at its own quality, so one frame, and the one residency that renders it, serves both; the
    // scene stays while the window is attached, and goes once neither a seat nor a window shows the world.
    [Fact]
    public void AWindowIsAViewOfTheSceneItsWorldsSeatsRenderAtItsOwnQuality() {
        using var state = new TemporaryDirectory(prefix: "puck-routed-window-");
        using var host = WorldBootHarness.Compose(
            edit: definition => (definition with {
                ViewsRaw = (definition.Views with {
                    Layouts = [new WorldViewLayout(Name: "seat", Slots: [new WorldViewSlot(Height: 1f, Width: 1f, X: 0f, Y: 0f)])],
                }),
            }),
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: state,
            world: "tests/Puck.Counters/counters.world.json"
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var routes = host.Services.GetRequiredService<WorldSeatAuthorityRouter>();

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            _ = client.Roster.VacateSeat(slot: slot);
        }
        _ = client.Roster.OccupySeat(profile: null, slot: 0);
        using var boot = Endpoint(definition: client.Definition, identity: Home, position: HomePose);
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        var window = presenter.AttachWindow(endpoint: north);
        var home = Capture(source: presenter);

        var routed = Enumerable.Range(start: 0, count: home.Views.Count).Single(predicate: view => presenter.TryRoutedView(index: out _, scene: out _, view: view));

        Assert.True(condition: presenter.TryRoutedView(index: out var seat, scene: out var scene, view: routed));
        Assert.Same(actual: window.Scene, expected: scene);
        Assert.Equal(actual: window.Index, expected: 1);

        window.View = new SdfViewSnapshot(
            Camera: home.Views[routed].Camera,
            Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
        ) {
            Quality = WorldSessionSceneEmitter.ReducedQuality,
        };

        var frame = Capture(source: scene!.FrameSource);

        Assert.Equal(actual: frame.Views.Count, expected: 2);
        Assert.Equal(actual: frame.Views[seat], expected: home.Views[routed]);
        Assert.Equal(actual: frame.Views[window.Index], expected: window.View);
        Assert.NotEqual(expected: frame.Views[seat].Quality, actual: frame.Views[window.Index].Quality);

        // The seat returns home; the window keeps the scene, and becomes its first view.
        _ = routes.Publish(endpoint: boot, entity: boot.Mirror.Address(index: 0), slot: 0);
        _ = Capture(source: presenter);
        Assert.True(condition: presenter.Presents(scene: scene));
        Assert.Equal(actual: window.Index, expected: 0);
        Assert.Equal(actual: Capture(source: scene.FrameSource).Views, expected: [window.View.Value]);

        // Attachments between the presenter and scene dresses wait for the next presenter latch.
        var pending = presenter.AttachWindow(endpoint: north);

        pending.View = window.View.Value with { Quality = default };
        Assert.Equal(actual: pending.Index, expected: -1);
        Assert.Single(collection: Capture(source: scene.FrameSource).Views);
        _ = Capture(source: presenter);
        var pendingIndex = pending.Index;

        Assert.Equal(actual: pendingIndex, expected: 1);
        window.Dispose();
        Assert.Equal(actual: pending.Index, expected: pendingIndex);
        Assert.Throws<ObjectDisposedException>(testCode: () => window.Scene);

        var held = Capture(source: scene.FrameSource);

        Assert.Equal(actual: held.Views[pendingIndex], expected: pending.View);
        Assert.Equal(actual: held.Views.Count, expected: 2);

        // A disposal after scene dress keeps every index already resolved for that frame valid.
        _ = Capture(source: presenter);
        Assert.Equal(actual: pending.Index, expected: 0);
        var current = Capture(source: scene.FrameSource);
        var following = presenter.AttachWindow(endpoint: north);

        Assert.Equal(actual: following.Index, expected: -1);
        pending.Dispose();
        Assert.Equal(actual: current.Views[0], expected: pending.View);
        _ = Capture(source: presenter);
        Assert.Equal(actual: following.Index, expected: 0);

        var fallback = Capture(source: scene.FrameSource);
        var emitter = new WorldSessionSceneEmitter(effectiveCameraName: null, mirror: north.Mirror);
        var expected = Capture(source: new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]));

        Assert.Equal(actual: Assert.Single(collection: fallback.Views), expected: expected.Views[0]);

        following.Dispose();
        _ = Capture(source: presenter);
        Assert.False(condition: presenter.Presents(scene: scene));
        Assert.Equal(actual: window.Index, expected: -1);

        using var reopened = presenter.AttachWindow(endpoint: north);

        Assert.NotSame(actual: reopened.Scene, expected: scene);
        Assert.True(condition: presenter.Presents(scene: reopened.Scene));
        Assert.False(condition: presenter.Presents(scene: scene));
        Assert.Equal(actual: reopened.Index, expected: -1);
        _ = Capture(source: presenter);
        Assert.Equal(actual: reopened.Index, expected: 0);
        Assert.Single(collection: Capture(source: reopened.Scene.FrameSource).Views);
    }
    // A window shows its destination under the destination's own sky, as the destination's authority renders it, never
    // the pinned default sky.
    [Fact]
    public void AWindowRendersItsDestinationUnderTheDestinationsOwnSky() {
        var destination = (AwayDocument() with {
            RenderRaw = (WorldRenderDefaults.Absent with {
                Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [
                    new WorldRenderSkyStop(Color: new BindableColor(Raw: "#401020"), Elevation: -1f),
                    new WorldRenderSkyStop(Color: new BindableColor(Raw: "#20C040"), Elevation: 1f),
                ])]),
            }),
        });
        using var north = Endpoint(definition: destination, identity: Away, position: AwayPose);
        var scene = new WorldRoutedScene(bodyColor: north.Mirror.BodyColor, endpoint: north, hostFrame: static () => null);
        var frame = Capture(source: scene.FrameSource);
        var sky = new WorldEnvironmentResolve().Resolve(
            definition: destination,
            mirror: north.FollowState(),
            revision: north.Mirror.DefinitionRevision
        );

        Assert.Equal(actual: frame.Sky.Stops.ToArray(), expected: sky.Sky.Stops.ToArray());
        Assert.Equal(actual: frame.Sky.Block, expected: sky.Sky.Block);
        Assert.NotEqual(expected: new SdfSky().Stops.ToArray(), actual: frame.Sky.Stops.ToArray());

        var retained = frame.Sky.Stops.ToArray();

        north.Mirror.DeliverDefinition(definition: AwayDocument(), version: default);
        _ = Capture(source: scene.FrameSource);
        var changed = Capture(source: scene.FrameSource);

        Assert.NotEqual(expected: retained, actual: changed.Sky.Stops.ToArray());
    }
    // A routed view's sky clock (its stars' twinkle, its clouds' drift, its media's motion) is the destination's presented
    // tick: the sky it shows moves as its world does, whatever the viewer's own world has reached. Its presentation time
    // is still the viewer's frame's.
    [Fact]
    public void ARoutedViewsSkyClockIsTheDestinationsTick() {
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        SdfFrame? viewer = null;
        var scene = new WorldRoutedScene(bodyColor: north.Mirror.BodyColor, endpoint: north, hostFrame: () => viewer);

        viewer = (Capture(source: scene.FrameSource) with {
            Clock = new PresentedTick(
                Fraction: 0d,
                Whole: 123456789UL
            ),
            Time = 3f,
        });

        var frame = Capture(source: scene.FrameSource);

        Assert.NotEqual(expected: viewer.Clock, actual: frame.Clock);
        Assert.Equal(actual: frame.Clock, expected: north.FollowState().Presented);
        Assert.Equal(actual: frame.Time, expected: viewer.Time);

        north.Mirror.DeliverSnapshot(snapshot: Snapshot(authority: Away, position: AwayPose) with {
            EngineTick = 50400UL,
            Tick = 2UL,
        });
        var advanced = scene.FrameSource.CaptureFrame(deltaSeconds: 5f, height: 36U, interpolationAlpha: 1f, width: 64U);

        Assert.Equal(actual: advanced.Clock, expected: north.FollowState().Presented);
        Assert.Equal(actual: advanced.Clock.Whole, expected: 50400UL);
        Assert.Equal(actual: advanced.Time, expected: viewer.Time);
    }
}
