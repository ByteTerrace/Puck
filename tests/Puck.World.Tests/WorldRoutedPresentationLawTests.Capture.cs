using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldRoutedPresentationLawTests {
    [Fact]
    public async Task ACrossingCaptureIgnoresAPinnedRouteOlderThanItsArmAndWaitsForAVisibleSeat() {
        var home = Fixtures.BuildDocument() with { DocumentId = "home" };
        using var boot = Endpoint(definition: home, identity: Home, position: HomePose);
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        var routes = new WorldSeatAuthorityRouter();
        var continuum = new WorldContinuum(adjacencies: new NoNeighbours(), client: ClientFixtures.Client(definition: home), routes: routes);
        var viewports = new WorldSeatViewports();
        var target = new CrossingTarget();
        using var capture = new WorldCrossingCapture(captureTarget: () => target, continuum: continuum, routes: routes, viewports: viewports);
        var request = new FrameCaptureRequest(path: "crossing.png");

        _ = routes.Publish(endpoint: boot, entity: boot.Mirror.Address(index: 0), slot: 0);
        continuum.BeginFrame();
        PublishSeat(viewports: viewports);
        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        Assert.True(condition: await Task.Run(function: () => capture.TryArm(reason: out _, request: request, slot: 0)));
        capture.Present();
        Assert.Null(@object: target.Request);
        continuum.EndFrame();

        continuum.BeginFrame();
        capture.Present();
        Assert.Null(@object: target.Request);
        continuum.EndFrame();

        _ = routes.Publish(endpoint: boot, entity: boot.Mirror.Address(index: 0), slot: 0);
        continuum.BeginFrame();
        viewports.BeginFrame();
        capture.Present();
        Assert.Null(@object: target.Request);
        PublishSeat(viewports: viewports);
        capture.Present();
        Assert.Same(expected: request, actual: target.Request);
        Assert.Null(@object: capture.PendingPath);
        continuum.EndFrame();

        // An accepted request that the root did not serve in that frame must not turn into a capture of a later one.
        continuum.BeginFrame();
        capture.Present();
        continuum.EndFrame();
        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Contains(expectedSubstring: "first crossing frame", actualString: (await request.Completion).Error!.Message);
    }
    [Fact]
    public async Task ACrossingCaptureRefusesMissingSeatsBusyTargetsAndDisposalAndDropsWithdrawals() {
        var home = Fixtures.BuildDocument();
        using var boot = Endpoint(definition: home, identity: Home, position: HomePose);
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        var routes = new WorldSeatAuthorityRouter();
        var continuum = new WorldContinuum(adjacencies: new NoNeighbours(), client: ClientFixtures.Client(definition: home), routes: routes);
        var viewports = new WorldSeatViewports();
        var target = new CrossingTarget { PendingCapturePath = "ordinary.png" };
        using var capture = new WorldCrossingCapture(captureTarget: () => target, continuum: continuum, routes: routes, viewports: viewports);
        var request = new FrameCaptureRequest(path: "crossing.png");

        Assert.False(condition: capture.TryArm(reason: out var reason, request: request, slot: 0));
        Assert.Contains(actualString: reason, expectedSubstring: "no authority route");
        _ = routes.Publish(endpoint: boot, entity: boot.Mirror.Address(index: 0), slot: 0);
        Assert.True(condition: capture.TryArm(reason: out _, request: request, slot: 0));
        Assert.False(condition: capture.TryArm(reason: out _, request: new FrameCaptureRequest(path: "second.png"), slot: 0));
        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        continuum.BeginFrame();
        PublishSeat(viewports: viewports);
        capture.Present();
        continuum.EndFrame();
        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Contains(expectedSubstring: "ordinary.png", actualString: (await request.Completion).Error!.Message);

        var withdrawn = new FrameCaptureRequest(path: "withdrawn.png");

        Assert.True(condition: capture.TryArm(reason: out _, request: withdrawn, slot: 0));
        _ = withdrawn.TryFail(error: new OperationCanceledException());
        Assert.Null(@object: capture.PendingPath);
        var pending = new FrameCaptureRequest(path: "pending.png");

        Assert.True(condition: capture.TryArm(reason: out _, request: pending, slot: 0));
        capture.Dispose();
        Assert.True(condition: pending.Completion.IsCompleted);
        Assert.IsType<ObjectDisposedException>(@object: (await pending.Completion).Error);
        Assert.False(condition: capture.TryArm(reason: out _, request: new FrameCaptureRequest(path: "closed.png"), slot: 0));
    }

    private static void PublishSeat(WorldSeatViewports viewports) {
        var camera = default(CameraSnapshot);

        viewports.Publish(camera: in camera, height: 36U, region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f), slot: 0, width: 64U);
    }

    private sealed class CrossingTarget : ICaptureRequestTarget {
        public string? PendingCapturePath { get; set; }
        public FrameCaptureRequest? Request { get; private set; }

        public void RequestCapture(FrameCaptureRequest request) => Request = request;
    }
}
