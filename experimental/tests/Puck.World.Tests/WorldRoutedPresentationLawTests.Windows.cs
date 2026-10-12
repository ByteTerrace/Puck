using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldRoutedPresentationLawTests {
    // A window onto a world is a view of the scene the seats presented there render (WorldFramePresenter.AttachWindow),
    // after their views and at its own quality, so one frame, and the one residency that renders it, serves both; the
    // scene stays while the window is attached, and goes once neither a seat nor a window shows the world.
    [Fact]
    public void AWindowIsAViewOfTheSceneItsWorldsSeatsRenderAtItsOwnQuality() {
        using var state = new TemporaryDirectory(prefix: "puck-routed-window-");
        var host = state.Own(owner: WorldBootHarness.Compose(
            edit: definition => (definition with {
                ViewsRaw = (definition.Views with {
                    Layouts = [new WorldViewLayout(Name: "seat", Slots: [new WorldViewSlot(Height: 1f, Width: 1f, X: 0f, Y: 0f)])],
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
        var emitter = new WorldSessionSceneEmitter(domains: new WorldValueDomainGuard(), effectiveCameraName: null, mirror: north.Mirror);
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
    // A window whose camera fit is unavailable renders the world's default projection, which takes the same resolution
    // policy as a fitted camera: the ceiling and the grid a pin holds.
    [Fact]
    public void AWindowsDefaultProjectionFallbackIsDressedByItsResolutionPolicy() {
        using var state = new TemporaryDirectory(prefix: "puck-routed-window-fallback-");
        var host = state.Own(owner: WorldBootHarness.Compose(
            edit: definition => (definition with {
                ViewsRaw = (definition.Views with {
                    Layouts = [new WorldViewLayout(Name: "seat", Slots: [new WorldViewSlot(Height: 1f, Width: 1f, X: 0f, Y: 0f)])],
                }),
            }),
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: state,
            world: "tests/Puck.Counters/counters.puck"
        ).Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        using var window = presenter.AttachWindow(endpoint: north);

        _ = Capture(source: presenter);
        Assert.Null(value: window.View);
        Assert.Equal(actual: Assert.Single(collection: Capture(source: window.Scene.FrameSource).Views).RenderScale, expected: 1f);

        window.FallbackResolution = static view => (view with { RenderScale = 0.75f, ResolvedRenderScale = 0.625f });

        var dressed = Assert.Single(collection: Capture(source: window.Scene.FrameSource).Views);

        Assert.Equal(actual: dressed.RenderScale, expected: 0.75f);
        Assert.Equal(actual: dressed.ResolvedRenderScale, expected: 0.625f);
    }
}
