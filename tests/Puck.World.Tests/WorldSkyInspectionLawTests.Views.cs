using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSkyInspectionLawTests {
    [Fact]
    public void StandaloneSessionViewsReadEachLiveQualityOverrideAndAutoClearsIt() {
        var definition = Fixtures.BuildDocument();
        var settings = new WorldRenderSettings(defaults: definition.Render);
        var emitter = new WorldSessionSceneEmitter(effectiveCameraName: null,
            mirror: new WorldSessionMirror(placeholder: definition), settings: settings);
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]);

        foreach (var tier in new QualityTier?[] { QualityTier.Low, QualityTier.High, null }) {
            settings.SkyQuality = tier;
            var frame = Capture(source: source);

            Assert.Equal(tier, Assert.Single(collection: frame.Views).SkyQuality);
            Assert.Equal(WorldSessionSceneEmitter.ReducedQuality, frame.Views[0].Quality);
        }
    }

    private static SdfFrame Capture(ISdfFrameSource source) => source.CaptureFrame(
        deltaSeconds: 0f, height: 36U, interpolationAlpha: 0f, width: 64U);
}
public sealed partial class WorldRoutedPresentationLawTests {
    [Fact]
    public void ComposedQualityOverrideReachesMainRoutedAndWindowViewsIncludingRetainedCameras() {
        using var files = new TemporaryDirectory();
        using var host = WorldBootHarness.Compose(files, WorldHostPresentation.Offscreen,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json", edit: definition => definition with {
                CamerasRaw = [new WorldCamera(Name: "inspection-camera", RenderWidth: 64U, RenderHeight: 36U,
                    Rig: new WorldCameraProgram(Name: "inspection-camera", Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: 0.9f)], Version: WorldCameraProgram.CurrentVersion))],
            }).Build();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var routes = host.Services.GetRequiredService<WorldSeatAuthorityRouter>();
        var screens = host.Services.GetRequiredService<IWorldScreenPresenter>();
        // The host's concrete binder is internal; invoke its public source declaration, just as a HUD frame does.
        screens.GetType().GetMethod(name: "DeclareFrameSource")!.Invoke(obj: screens,
            parameters: [new WorldScreenSource.View(CameraName: "inspection-camera"), 1]);

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            _ = client.Roster.VacateSeat(slot: slot);
        }
        _ = client.Roster.OccupySeat(profile: null, slot: 0);
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);

        _ = routes.Publish(endpoint: north, entity: north.Mirror.Address(index: 0), slot: 0);
        using var explicitWindow = presenter.AttachWindow(endpoint: north);
        using var defaultWindow = presenter.AttachWindow(endpoint: north);

        foreach (var tier in new QualityTier?[] { QualityTier.Low, QualityTier.High, null }) {
            settings.SkyQuality = tier;
            var main = Capture(source: presenter);

            Assert.True(condition: (main.Views.Count >= 2), userMessage: "The named camera must be registered beside the seat view.");
            Assert.True(condition: ((IWorldViewScenes)screens).TryCamera(camera: out _, view: "inspection-camera"));
            Assert.All(collection: main.Views, action: view => Assert.Equal(tier, view.SkyQuality));
            explicitWindow.View ??= main.Views[0] with { SkyQuality = QualityTier.Medium };
            var routed = Capture(source: explicitWindow.Scene.FrameSource);

            Assert.True(condition: (routed.Views.Count >= 3));
            Assert.All(collection: routed.Views, action: view => Assert.Equal(tier, view.SkyQuality));
        }

        var scene = explicitWindow.Scene;

        scene.BeginViews();
        explicitWindow.Dispose();
        defaultWindow.Dispose();
        scene.EndViews();
        settings.SkyQuality = QualityTier.Low;
        var retained = Capture(source: scene.FrameSource);

        Assert.NotEmpty(collection: retained.Views);
        Assert.All(collection: retained.Views, action: view => Assert.Equal(QualityTier.Low, view.SkyQuality));
    }
}
