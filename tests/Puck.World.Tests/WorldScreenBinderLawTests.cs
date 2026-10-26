using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;
using Puck.Maths;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the screen binder as a headless boot composes it, and as a presentation drives it with no device
/// behind it: each declared screen binds what its row authors (a view of a declared camera, a session's destination, text,
/// a probe's output) or names why it cannot; the screen verbs rebind a screen live and give it back its row; a mutated
/// screen or camera list is followed; the views the screens show are registered once the views are configured and resolve
/// to the residency that renders them; a produced frame follows every session down to the presentation's nesting depth;
/// and a disposed binder refuses every verb. What needs a device is the binder's device work
/// (<c>WorldScreenBinder.Device.*</c>), which the device laws cover.
/// </summary>
public sealed class WorldScreenBinderLawTests {
    [Fact]
    public void EachScreenBindsItsRowOrNamesWhyItCannot() {
        using var scene = new BinderScene();
        var binder = scene.Binder;

        Assert.Equal(
            actual: binder.State(index: BinderScene.MissingCamera)?.Fault,
            expected: "camera 'missing' not declared"
        );
        Assert.Equal(
            actual: binder.State(index: BinderScene.Pattern),
            expected: new WorldScreenState(Assigned: false, Engine: null, Handle: 0, FramesStepped: 0, PendingSteps: 0, MaximumPendingSteps: 0, BackpressureEvents: 0, Fault: null)
        );
        Assert.Null(@object: binder.State(index: 99));
        Assert.Equal(
            actual: binder.TextSourceAt(index: BinderScene.Text)?.Lines,
            expected: ["hello"]
        );
        Assert.Null(@object: binder.TextSourceAt(index: 99));
        Assert.True(condition: binder.TryDescribeSession(
            description: out var session,
            index: BinderScene.Session
        ));
        Assert.Equal(
            actual: (session.Destination, session.LeaseHeld, session.RendersEveryFrame, session.EffectiveCamera),
            expected: ("garden", true, false, null)
        );
        Assert.True(condition: binder.TryDescribeSession(description: out var filmed, index: BinderScene.CameraSession));
        Assert.Equal(actual: filmed.EffectiveCamera, expected: "cam");
        Assert.True(condition: binder.TryDescribeSession(description: out var ghost, index: BinderScene.GhostSession));
        Assert.Null(@object: ghost.EffectiveCamera);
        Assert.NotNull(@object: binder.State(index: BinderScene.ClosedSession)?.Fault);
        Assert.False(condition: binder.TryDescribeSession(
            description: out _,
            index: BinderScene.Text
        ));

        // What the views bind: a producer's and a probe's source instance, a camera's and a session's view instance.
        Assert.Equal(
            actual: ((IEnumerable<int>)binder.Screens).Where(predicate: binder.Emits),
            expected: [BinderScene.Pattern, BinderScene.Session, BinderScene.Probe, BinderScene.CameraSession, BinderScene.GhostSession, BinderScene.Cabinet]
        );
        Assert.Equal(
            actual: binder.InstanceOf(screen: BinderScene.View),
            expected: WorldSeatAnchors.RegistrationName(camera: scene.Camera, seat: 1)
        );
        Assert.Equal(
            actual: binder.ReadOf(screen: BinderScene.Session, view: 0),
            expected: WorldViewNames.Session(screen: BinderScene.Session)
        );
        Assert.Null(@object: binder.InstanceOf(screen: BinderScene.Text));
        Assert.Null(@object: binder.MappingOf(screen: BinderScene.Pattern));
        Assert.False(condition: binder.TryExtent(height: out _, screen: BinderScene.Pattern, width: out _));
        Assert.Equal(actual: binder.CurrentHandle(index: BinderScene.Pattern), expected: 0);
        Assert.Null(@object: binder.FenceOrderAt(index: BinderScene.Probe));
        Assert.Same(actual: binder.MachinesOf(world: WorldInstanceHost.BootInstanceName), expected: scene.Machines);
        Assert.Null(@object: binder.MachinesOf(world: "nowhere"));
        Assert.NotNull(@object: binder.MachinesOf(world: session.InstanceName));
        Assert.Contains(
            collection: binder.Producers.Producers,
            filter: static producer => (producer.Id == WorldImageProducerSettings.CaptureId)
        );

        // The machine host's read-throughs, over a world that boots no machine.
        Assert.False(condition: binder.HasMachine(index: BinderScene.Pattern));
        Assert.False(condition: binder.HasEngine(engineId: "hgb"));
        Assert.Null(@object: binder.LinkOf(index: BinderScene.Pattern));
        Assert.Null(@object: binder.AudioMachine(index: BinderScene.Pattern));
        Assert.Null(@object: binder.AudioOutput(instance: "cabinet", output: "audio"));
        Assert.False(condition: binder.TryMagazine(index: BinderScene.Pattern, magazine: out _, selected: out _));
        Assert.False(condition: binder.TryPeek(address: 0, index: BinderScene.Pattern, value: out _).Ok);
        Assert.False(condition: binder.TryReadLinkMembers(members: out _, name: "cable"));
        _ = binder.DescribeLinks();

        // No camera device is seated, then one is seated that no scan has seen.
        Assert.Null(@object: binder.DescribeCamera());
        Assert.Null(@object: binder.ResolvedCameraToken(seat: 1));
        Assert.False(condition: binder.TryGetCameraAttachment(attachment: out _, seat: 1, sensor: WorldCameraSensor.Color));
        scene.Roster.ObserveDevice(device: InputDeviceId.FromKey(key: "webcam"), kind: InputDeviceKind.Camera, name: "Webcam");
        Assert.NotNull(@object: binder.ResolvedCameraToken(seat: 1));
        Assert.False(condition: binder.TryGetCameraAttachment(attachment: out _, seat: 1, sensor: WorldCameraSensor.Color));
        Assert.StartsWith(
            actualString: binder.DescribeNesting(),
            expectedStartString: "depth 3"
        );
    }
    [Fact]
    public void AnOpeningThatOptsInRipplesAndEveryOtherScreenDoesNot() {
        using var scene = new BinderScene();
        var binder = scene.Binder;

        Assert.False(condition: binder.TryRippleOf(
            ripple: out _,
            screen: BinderScene.Session
        ));
        // The live bind shows the source over the row whether or not the session then resolves.
        _ = binder.ApplyNonMachineSource(
            index: BinderScene.Session,
            source: new WorldScreenSource.Session(Destination: "garden", Projection: WorldScreenProjection.Window, Ripple: true)
        );
        Assert.True(condition: binder.TryRippleOf(
            ripple: out var ripple,
            screen: BinderScene.Session
        ));
        Assert.Equal(actual: ripple.Contact0.W, expected: 0f);
        Assert.False(condition: binder.TryRippleOf(
            ripple: out _,
            screen: BinderScene.Pattern
        ));
        Assert.False(condition: binder.TryRippleOf(
            ripple: out _,
            screen: 99
        ));
        Assert.False(condition: ((ISdfScreenSources)new QuietSources()).TryRippleOf(
            ripple: out _,
            screen: 0
        ));
    }
    [Fact]
    public void AScreenShowingAMachineReadsItFromTheMachineHost() {
        using var scene = new BinderScene(options: new BinderOptions(Cabinet: true));

        Assert.True(condition: scene.Binder.State(index: BinderScene.Cabinet)?.Assigned);
        Assert.True(condition: scene.Binder.TryExtent(height: out var height, screen: BinderScene.Cabinet, width: out var width));
        Assert.Equal(actual: (width, height), expected: (1, 1));
    }
    [Fact]
    public void AScreenVerbRebindsAScreenLiveAndTheRowSourceComesBack() {
        using var scene = new BinderScene();
        var binder = scene.Binder;

        // A QR code over a screen showing nothing, read back, refused for a bad level, and given back to its row.
        Assert.True(condition: binder.TryQr(
            ecLevel: null,
            index: BinderScene.None,
            payload: "https://puck.example/a-payload-long-enough-to-be-elided-in-the-echo",
            quietZoneModules: null
        ).Ok);
        Assert.True(condition: binder.TryReadQr(
            authoring: out var authoring,
            index: BinderScene.None
        ));
        Assert.Equal(actual: authoring.QuietZoneModules, expected: 4);
        Assert.False(condition: binder.TryQr(ecLevel: "Z", index: BinderScene.None, payload: "x", quietZoneModules: null).Ok);
        Assert.False(condition: binder.TryQr(ecLevel: null, index: 99, payload: "x", quietZoneModules: null).Ok);
        Assert.True(condition: binder.TryShowRow(index: BinderScene.None).Ok);
        Assert.False(condition: binder.TryShowRow(index: BinderScene.None).Ok);
        Assert.False(condition: binder.TryShowRow(index: 99).Ok);
        Assert.False(condition: binder.TryReadQr(authoring: out _, index: BinderScene.None));

        // A webcam is external content: a screen showing one ejects to unbound glass, or to its row's own pattern.
        Assert.False(condition: binder.TryCamera(index: 99, seat: 1, sensor: WorldCameraSensor.Color).Ok);
        Assert.False(condition: binder.TryCamera(index: BinderScene.None, seat: 1, sensor: ((WorldCameraSensor)99)).Ok);
        Assert.True(condition: binder.TryCamera(index: BinderScene.None, seat: 1, sensor: WorldCameraSensor.Infrared).Ok);
        Assert.True(condition: binder.TryEject(index: BinderScene.None).Ok);
        Assert.False(condition: binder.TryEject(index: BinderScene.None).Ok);
        Assert.False(condition: binder.TryEject(index: 99).Ok);
        Assert.True(condition: binder.TryCamera(index: BinderScene.Pattern, seat: 1, sensor: WorldCameraSensor.Color).Ok);
        Assert.True(condition: binder.TryEject(index: BinderScene.Pattern).Ok);
        Assert.NotNull(@object: binder.InstanceOf(screen: BinderScene.Pattern));

        // A probe's output binds only once the probe declares one.
        Assert.False(condition: binder.TryProbe(id: "other", index: BinderScene.None).Ok);
        Assert.False(condition: binder.TryProbe(id: "probe", index: 99).Ok);
        binder.DeclareProbeOutput(external: true, id: "probe");
        Assert.True(condition: binder.TryProbe(id: "probe", index: BinderScene.None).Ok);

        // Every other source a magazine entry names applies live; a machine boots through the machine host instead.
        Assert.False(condition: binder.ApplyNonMachineSource(index: BinderScene.None, source: new WorldScreenSource.Machine(Instance: "cabinet", Output: "video")).Ok);
        Assert.False(condition: binder.ApplyNonMachineSource(index: 99, source: new WorldScreenSource.None()).Ok);
        Assert.True(condition: binder.ApplyNonMachineSource(index: BinderScene.None, source: new WorldScreenSource.Text(Lines: ["live"])).Ok);
        Assert.Equal(actual: binder.TextSourceAt(index: BinderScene.None)?.Lines, expected: ["live"]);
        Assert.True(condition: binder.ApplyNonMachineSource(index: BinderScene.None, source: new WorldScreenSource.Probe(Id: "probe")).Ok);
        Assert.True(condition: binder.ApplyNonMachineSource(index: BinderScene.None, source: scene.Template.Source).Ok);
        Assert.False(condition: binder.ApplyNonMachineSource(index: BinderScene.None, source: new WorldScreenSource.View(CameraName: "cam")).Ok);
        Assert.False(condition: binder.ApplyNonMachineSource(index: BinderScene.None, source: new WorldScreenSource.Session(Destination: "garden")).Ok);
        Assert.Equal(
            actual: binder.State(index: BinderScene.None)?.Fault,
            expected: "the session to destination 'garden' awaits its authority"
        );
        Assert.True(condition: binder.ApplyNonMachineSource(index: BinderScene.None, source: new WorldScreenSource.None()).Ok);

        // The declared session screen re-points onto the session its authority holds, then leaves it for nothing.
        Assert.True(condition: binder.ApplyNonMachineSource(index: BinderScene.Session, source: new WorldScreenSource.Session(Destination: "garden")).Ok);
        Assert.True(condition: binder.TryDescribeSession(description: out _, index: BinderScene.Session));
        Assert.True(condition: binder.ApplyNonMachineSource(index: BinderScene.Session, source: new WorldScreenSource.None()).Ok);
        Assert.False(condition: binder.TryDescribeSession(description: out _, index: BinderScene.Session));
    }
    [Fact]
    public void ACaptureOpensOnlyWhatThePlatformFinds() {
        var window = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CaptureId, settings: new WorldCaptureSettings(Profile: WorldFeedProfile.Default, WindowTitle: FakeCaptures.Present));
        var absentWindow = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CaptureId, settings: new WorldCaptureSettings(Profile: WorldFeedProfile.Default, WindowTitle: "absent"));
        var monitor = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CaptureId, settings: new WorldCaptureSettings(MonitorIndex: 0, Profile: WorldFeedProfile.Default));
        var absentMonitor = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CaptureId, settings: new WorldCaptureSettings(MonitorIndex: 3, Profile: WorldFeedProfile.Default));
        WorldFrameSource[] captures = [window, absentWindow, monitor, absentMonitor];

        // Live open commands still refuse, but declared sources retain a feed so the capture gate can fill them.
        using (var unsupported = new BinderScene(options: new BinderOptions(CamerasSupported: false, CapturesSupported: false))) {
            Assert.Equal(
                actual: unsupported.Binder.TryCamera(index: BinderScene.None, seat: 1, sensor: WorldCameraSensor.Color),
                expected: (false, "no camera device present")
            );
            Assert.False(condition: unsupported.Binder.TryCapture(index: BinderScene.None, windowTitle: "editor").Ok);
            Assert.False(condition: unsupported.Binder.TryDesktop(index: BinderScene.None, monitorIndex: 0).Ok);

            foreach (var capture in captures) {
                unsupported.Binder.DeclareFrameSource(seat: 1, source: capture);
                var producer = unsupported.Binder.Producers.Producers.Single(predicate: static item => (item.Id == WorldImageProducerSettings.CaptureId));

                Assert.True(condition: producer.TryOpen(source: Assert.IsType<WorldScreenSource.Producer>(@object: capture), feed: out var feed, fault: out _));
                using var opened = Assert.IsAssignableFrom<IWorldImportFeed>(@object: feed);
                Assert.True(condition: new WorldCaptureGate(alwaysFills: false, captureArmed: static () => false).FillsFeed(feed: opened));
            }
        }

        // The Direct3D 12 host defers every open to the first pull, when the render adapter is known.
        using (var direct = new BinderScene(options: new BinderOptions(HostsOnDirectX: true))) {
            foreach (var capture in captures) {
                direct.Binder.DeclareFrameSource(seat: 1, source: capture);
            }

            Assert.True(condition: direct.Binder.TryCapture(index: BinderScene.None, windowTitle: FakeCaptures.Present).Ok);
        }

        using var scene = new BinderScene();
        var binder = scene.Binder;

        foreach (var capture in captures) {
            binder.DeclareFrameSource(seat: 1, source: capture);
        }

        Assert.False(condition: binder.TryCapture(index: 99, windowTitle: "editor").Ok);
        Assert.False(condition: binder.TryDesktop(index: 99, monitorIndex: 0).Ok);
        Assert.Equal(
            actual: binder.TryCapture(index: BinderScene.None, windowTitle: " "),
            expected: (false, "a window title is required")
        );
        Assert.Equal(
            actual: binder.TryCapture(index: BinderScene.None, windowTitle: "absent"),
            expected: (false, "window capture unavailable for 'absent'")
        );
        Assert.Equal(
            actual: binder.TryDesktop(index: BinderScene.None, monitorIndex: -1),
            expected: (false, "monitor -1 is not a valid index")
        );
        Assert.Equal(
            actual: binder.TryDesktop(index: BinderScene.None, monitorIndex: 3),
            expected: (false, "monitor 3 not found")
        );
        Assert.True(condition: binder.TryCapture(index: BinderScene.None, windowTitle: FakeCaptures.Present).Ok);
        Assert.True(condition: binder.TryDesktop(index: BinderScene.Pattern, monitorIndex: 0).Ok);
        Assert.Null(@object: binder.PassthroughWindowOf(fault: out var fault, instance: "nothing"));
        Assert.Equal(actual: fault, expected: "no source instance 'nothing' is running");

        // A capture a verb opened waits one publish for the source instance that shows it, then goes.
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        binder.NotifyDeviceLost();
    }
    [Fact]
    public void AMutatedScreenListRemovesRecreatesAndReappliesSources() {
        using var scene = new BinderScene();
        var binder = scene.Binder;
        var rows = scene.Rows;

        // Every screen but the pattern goes: each slot is dropped, the session released, the camera's view with it.
        binder.ReconcileScreens(screens: [rows[0]]);
        Assert.Null(@object: binder.State(index: BinderScene.Session));
        Assert.Null(@object: binder.InstanceOf(screen: BinderScene.View));

        // A boot index comes back with a new source, and a new index waits for the next boot.
        binder.ReconcileScreens(screens: [rows[0], (rows[BinderScene.Text] with { Source = new WorldScreenSource.Text(Lines: ["again"]) }), (rows[0] with { Index = 40 })]);
        Assert.Equal(actual: binder.TextSourceAt(index: BinderScene.Text)?.Lines, expected: ["again"]);
        Assert.Null(@object: binder.State(index: 40));

        // The same rows again change nothing; a changed row's source applies, a machine's through its host.
        binder.ReconcileScreens(screens: [rows[0], (rows[BinderScene.Text] with { Source = new WorldScreenSource.Text(Lines: ["again"]) })]);
        binder.ReconcileScreens(screens: [(rows[0] with { Source = new WorldScreenSource.None() }), rows[BinderScene.Text], (rows[0] with { Index = BinderScene.None, Source = new WorldScreenSource.Machine(Instance: "cabinet", Output: "video") })]);
        Assert.Null(@object: binder.InstanceOf(screen: BinderScene.Pattern));
        Assert.Equal(actual: binder.TextSourceAt(index: BinderScene.Text)?.Lines, expected: ["hello"]);
        Assert.False(condition: binder.TryExtent(height: out _, screen: BinderScene.None, width: out _));

        // A camera row authors its seat's camera controls; the first row authoring a seat's wins.
        var camera = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings(Controls: new WorldCameraControls(Zoom: 2)));

        binder.ReconcileScreens(screens: [(rows[0] with { Source = camera }), (rows[BinderScene.Text] with { Source = camera })]);
    }
    [Fact]
    public void ConfiguredViewsRegisterTheCamerasTheScreensShow() {
        using var scene = new BinderScene();
        var binder = scene.Binder;
        var registration = WorldSeatAnchors.RegistrationName(camera: scene.Camera, seat: 1);
        var session = WorldViewNames.Session(screen: BinderScene.Session);

        // Before the views are configured, a camera view cannot bind and no view resolves.
        Assert.Equal(actual: binder.TryView(cameraName: "cam", index: BinderScene.None), expected: (false, "the views are not configured"));
        Assert.False(condition: binder.TryResolveView(name: registration, view: out _));
        Assert.Equal(actual: binder.ActiveCameraViewCount, expected: 0);

        scene.ConfigureViews();
        Assert.Equal(actual: binder.ActiveCameraViewCount, expected: 1);
        Assert.False(condition: binder.TryResolveView(name: registration, view: out _));
        Assert.False(condition: binder.TryView(cameraName: "cam", index: 99).Ok);
        Assert.False(condition: binder.TryView(cameraName: "missing", index: BinderScene.None).Ok);
        Assert.True(condition: binder.TryView(cameraName: "cam", index: BinderScene.None).Ok);
        Assert.Equal(actual: binder.InstanceOf(screen: BinderScene.None), expected: registration);

        // A screen showing a camera over its view row gives the row's view back.
        Assert.True(condition: binder.TryQr(ecLevel: null, index: BinderScene.View, payload: "x", quietZoneModules: null).Ok);
        Assert.True(condition: binder.TryShowRow(index: BinderScene.View).Ok);

        // A camera view renders as a view of the world's residency; a session renders a residency of its own, made again
        // when the session re-points.
        using var world = scene.Residency(name: "world");

        binder.ViewHost = world;
        Assert.True(condition: binder.TryResolveView(name: registration, view: out var filmed));
        Assert.Same(actual: filmed.Residency, expected: world);
        Assert.Same(actual: binder.ResolveInstanceView(name: registration, world: world).Residency, expected: world);
        Assert.True(condition: binder.TryResolveView(name: session, view: out var own));
        Assert.True(condition: binder.TryResolveView(name: session, view: out var again));
        Assert.Same(actual: again.Residency, expected: own.Residency);
        Assert.NotSame(actual: own.Residency, expected: world);
        Assert.False(condition: binder.Bakes(view: session));
        Assert.False(condition: binder.Bakes(view: "nothing"));
        Assert.False(condition: binder.TryResolveView(name: "nothing", view: out _));
        Assert.Same(actual: binder.ResolveInstanceView(name: "nothing", world: world).Residency, expected: world);
        Assert.True(condition: binder.TryPlacements(placements: out _, view: session));
        Assert.False(condition: binder.TryPlacements(placements: out _, view: "nothing"));
        Assert.Equal(
            actual: binder.PortalGlass(consumer: WorldViewGraphs.WorldInstance, glass: out _, producer: session),
            expected: WorldPortalGlass.Found
        );
        Assert.Equal(actual: binder.PortalGlass(consumer: "nothing", glass: out _, producer: session), expected: WorldPortalGlass.None);
        Assert.Equal(actual: binder.PortalGlass(consumer: WorldViewGraphs.WorldInstance, glass: out _, producer: "nothing"), expected: WorldPortalGlass.None);
        Assert.False(condition: binder.TryCamera(camera: out _, view: "nothing"));
        _ = binder.TryCamera(camera: out _, view: session);
        Assert.False(condition: binder.TrySurface(point: out _, ray: default, view: "nothing"));
        _ = binder.TrySurface(point: out _, ray: default, view: session);
        Assert.Equal(actual: binder.HostView(view: 3), expected: 0);
        Assert.True(condition: binder.ApplyNonMachineSource(index: BinderScene.Session, source: new WorldScreenSource.Session(Destination: "garden")).Ok);
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        Assert.True(condition: binder.TryResolveView(name: session, view: out _));

        // The display resizes, the refresh divisor moves, and both refuse what is out of range.
        binder.ResizeDisplay(displayHeight: 720, displayWidth: 1280);
        binder.SetViewRefreshDivisor(divisor: 2);
        Assert.Equal(actual: binder.ViewRefreshDivisor, expected: 2);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => binder.SetViewRefreshDivisor(divisor: 9));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => binder.ResizeDisplay(displayHeight: 0, displayWidth: 1));

        // A HUD frame naming the camera keeps its view after both screens let go of it; a release parks it, and
        // forgetting it releases the view.
        var hud = new WorldScreenSource.View(CameraName: "cam");
        var nothing = new WorldScreenSource.View(CameraName: "nothing");

        binder.ForgetFrameSource(seat: 1, source: nothing);
        binder.RetainFrameSource(seat: 1, source: hud);
        binder.RetainFrameSource(seat: 1, source: hud);
        binder.ForgetFrameSource(seat: 1, source: hud);
        Assert.True(condition: binder.ApplyNonMachineSource(index: BinderScene.None, source: new WorldScreenSource.None()).Ok);
        binder.ReconcileScreens(screens: [.. scene.Rows.Where(predicate: static row => (row.Index != BinderScene.View))]);
        Assert.Equal(actual: binder.ActiveCameraViewCount, expected: 1);
        binder.ReleaseFrameSource(seat: 1, source: hud);
        binder.ReleaseFrameSource(seat: 1, source: hud);
        binder.ReleaseFrameSource(seat: 1, source: hud);
        binder.ReleaseFrameSource(seat: 2, source: nothing);
        binder.RetainFrameSource(seat: 2, source: nothing);
        binder.ReleaseFrameSource(seat: 2, source: nothing);
        binder.ForgetFrameSource(seat: 1, source: hud);
        Assert.Equal(actual: binder.ActiveCameraViewCount, expected: 0);

        // The camera rows move: an edit in place, a row the views no longer have, a row arriving for a faulted screen, a
        // row that comes to ride a seat and so registers again.
        binder.ReconcileScreens(screens: scene.Rows);
        Assert.Equal(actual: binder.ActiveCameraViewCount, expected: 1);
        binder.ReconcileCameras(cameras: [(scene.Camera with { RenderWidth = 256U })]);
        binder.ReconcileCameras(cameras: [(scene.Camera with { RenderWidth = 256U }), (scene.Camera with { Name = "missing" })]);
        Assert.Null(@object: binder.State(index: BinderScene.MissingCamera)?.Fault);
        binder.ReconcileCameras(cameras: [(scene.Camera with { Anchor = new WorldAnchor.Seat() }), (scene.Camera with { Name = "missing" })]);
        binder.ReconcileCameras(cameras: []);
        Assert.Equal(actual: binder.ActiveCameraViewCount, expected: 0);
        Assert.Equal(actual: binder.State(index: BinderScene.View)?.Fault, expected: "camera 'cam' not declared");
    }
    [Fact]
    public void EachAnchorKindPosesTheCameraViewItRegisters() {
        static WorldCamera Anchored(string name, WorldAnchor? anchor) => (BinderScene.DeclaredCamera with { Anchor = anchor, Name = name });

        WorldCamera[] cameras = [
            Anchored(name: "seat", anchor: new WorldAnchor.Seat()),
            Anchored(name: "entity", anchor: new WorldAnchor.Entity(Index: 0)),
            Anchored(name: "part", anchor: new WorldAnchor.EntityPart(Index: 0, PartId: "head")),
            Anchored(name: "placement", anchor: new WorldAnchor.Placement(PlacementId: "nowhere", ShapeId: null)),
            Anchored(name: "group", anchor: new WorldAnchor.Group(Indices: [0], SmoothRate: 1f)),
        ];
        using var scene = new BinderScene(options: new BinderOptions(Cameras: [BinderScene.DeclaredCamera, .. cameras]));
        var binder = scene.Binder;

        scene.ConfigureViews();

        foreach (var camera in cameras) {
            Assert.True(condition: binder.TryView(cameraName: camera.Name, index: BinderScene.None).Ok);
        }

        Assert.Equal(actual: binder.ActiveCameraViewCount, expected: 2);
    }
    [Fact]
    public void AFrameSourceOrAProbeSharesTheFeedAScreenWould() {
        using var scene = new BinderScene();
        var binder = scene.Binder;
        var camera = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings());
        var capture = WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CaptureId, settings: new WorldCaptureSettings(Profile: WorldFeedProfile.Default, WindowTitle: FakeCaptures.Present));
        var probe = new WorldScreenSource.Probe(Id: "probe");
        var rich = new WorldFeedProfile(Height: 480, RefreshRateHz: 60U, Width: 640);

        foreach (var source in (WorldFrameSource[])[camera, capture, probe]) {
            binder.DeclareFrameSource(seat: 1, source: source);
            binder.RetainFrameSource(seat: 1, source: source);
            binder.ReleaseFrameSource(seat: 1, source: source);
            binder.ForgetFrameSource(seat: 1, source: source);
        }

        // A capture another retained source shares nothing with is torn down when forgotten; one still declared is
        // disposed with the binder.
        binder.DeclareFrameSource(seat: 1, source: capture);
        binder.RetainFrameSource(seat: 1, source: probe);
        binder.ForgetFrameSource(seat: 1, source: capture);
        binder.DeclareFrameSource(seat: 1, source: capture);
        binder.RetainFrameSource(seat: 2, source: capture);
        binder.ForgetFrameSource(seat: 1, source: capture);

        // A frame recomputes the webcam demand from every consumer: a screen, a probe socket and a HUD frame, each at
        // the richest profile any of them asks.
        binder.RetainProbeCameraDemand(camera: new WorldCameraSettings(Seat: 2), contextSeat: 1);
        binder.RetainProbeCameraDemand(camera: new WorldCameraSettings(Profile: rich), contextSeat: 1);
        binder.RetainProbeCameraDemand(camera: new WorldCameraSettings(), contextSeat: 1);
        binder.RetainFrameSource(seat: 1, source: camera);
        binder.RetainFrameSource(seat: 2, source: camera);
        Assert.True(condition: binder.TryCamera(index: BinderScene.None, seat: 1, sensor: WorldCameraSensor.Color).Ok);
        Assert.True(condition: binder.TryCamera(index: BinderScene.Pattern, seat: 1, sensor: WorldCameraSensor.Color).Ok);
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        binder.ReleaseProbeCameraDemand(camera: new WorldCameraSettings(Profile: rich, Seat: 2), contextSeat: 1);
        binder.ReleaseProbeCameraDemand(camera: new WorldCameraSettings(Seat: 2), contextSeat: 1);
        binder.ReleaseProbeCameraDemand(camera: new WorldCameraSettings(Seat: 3), contextSeat: 1);
        binder.ReleaseProbeCameraDemand(camera: new WorldCameraSettings(Profile: rich), contextSeat: 1);
        binder.ReleaseProbeCameraDemand(camera: new WorldCameraSettings(), contextSeat: 1);

        // A probe's output ring waits for a publish to provision it, on an adapter the render device names.
        Assert.False(condition: binder.TryGetProbeOutput(fault: out var fault, generation: out _, height: 8, id: "fresh", output: out _, width: 8));
        Assert.Equal(actual: fault, expected: "probe output awaiting provisioning");
        Assert.False(condition: binder.TryGetProbeOutput(fault: out _, generation: out _, height: 8, id: "probe", output: out _, width: 8));
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        Assert.False(condition: binder.TryGetProbeOutput(fault: out fault, generation: out _, height: 8, id: "probe", output: out _, width: 8));
        Assert.Equal(actual: fault, expected: "the render adapter reports no LUID");
        binder.BindProbeRun(id: "probe", run: null!);
        binder.ReleaseProbeOutput(id: "probe");
        binder.ReleaseProbeOutput(id: "other");
        Assert.False(condition: binder.TryGetRenderedKernelHost(fault: out fault, host: out _));
        Assert.Equal(actual: fault, expected: "the render adapter reports no LUID yet");

        // A view export waits for the camera's view to render, and is made again for a view released and registered anew.
        Assert.False(condition: binder.TryGetViewExport(cameraName: "missing", fault: out _, generation: out _, ring: out _));
        Assert.False(condition: binder.TryGetViewExport(cameraName: "cam", fault: out fault, generation: out _, ring: out _));
        Assert.Equal(actual: fault, expected: "the views are not configured");
        scene.ConfigureViews();
        Assert.False(condition: binder.TryGetViewExport(cameraName: "cam", fault: out fault, generation: out _, ring: out _));
        Assert.Equal(actual: fault, expected: "view export awaiting a first rendered frame");
        Assert.False(condition: binder.TryGetViewExport(cameraName: "cam", fault: out _, generation: out _, ring: out _));
        binder.ReconcileScreens(screens: [.. scene.Rows.Where(predicate: static row => (row.Index != BinderScene.View))]);
        Assert.False(condition: binder.TryGetViewExport(cameraName: "cam", fault: out _, generation: out _, ring: out _));
        binder.RetainViewExport(cameraName: "cam");
        binder.RetainViewExport(cameraName: "cam");
        binder.ReconcileCameras(cameras: [(scene.Camera with { RenderHeight = 144U })]);
        Assert.False(condition: binder.TryGetViewExport(cameraName: "cam", fault: out _, generation: out _, ring: out _));
        binder.ReleaseViewExport(cameraName: "cam");
        binder.ReleaseViewExport(cameraName: "cam");
        binder.ReleaseViewExport(cameraName: "cam");
        Assert.False(condition: binder.TryGetViewExport(cameraName: "cam", fault: out _, generation: out _, ring: out _));
    }
    [Fact]
    public void ARenderAdapterTheDeviceNamesOpensItsKernelHost() {
        using var scene = new BinderScene();
        var binder = scene.Binder;

        // A frame over screens that show nothing external converts no capture fill until a HUD frame names a webcam.
        binder.ReconcileScreens(screens: [scene.Rows[0]]);
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice { AdapterLuid = 7L }));
        Assert.False(condition: binder.TryGetRenderedKernelHost(fault: out var fault, host: out _));
        Assert.Equal(actual: fault, expected: "this platform hosts no probe kernels");
        binder.RetainFrameSource(seat: 1, source: WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings()));
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice { AdapterLuid = 7L }));
    }
    [Fact]
    public void APublishedFrameFollowsTheSessionsAndTheirWorldsScreens() {
        using var scene = new BinderScene(options: new BinderOptions(NestingDepth: 2));
        var binder = scene.Binder;
        var session = WorldViewNames.Session(screen: BinderScene.Session);

        // A frame with no device publishes nothing; one with a device follows each session's destination's screens.
        binder.Publish(context: scene.Frame(device: null));
        scene.ConfigureViews();
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));

        var nesting = binder.DescribeNesting();

        Assert.StartsWith(actualString: nesting, expectedStartString: "depth 2");
        Assert.Contains(actualString: nesting, expectedSubstring: $"{session} depth 1 destination garden via own screens");
        Assert.Contains(actualString: nesting, expectedSubstring: "destination cellar via own screens none");

        // The garden's session screen stands in the garden, and its camera screen films the garden's camera.
        var nested = WorldViewNames.Nested(screen: BinderScene.GardenSession, view: session);
        var gardenCamera = WorldViewNames.NestedCamera(camera: "cam", view: session);

        Assert.Equal(actual: binder.PortalGlass(consumer: session, glass: out var glass, producer: nested), expected: WorldPortalGlass.Found);
        Assert.Equal(actual: glass?.Index, expected: BinderScene.GardenSession);
        Assert.Equal(actual: binder.PortalGlass(consumer: WorldViewGraphs.WorldInstance, glass: out _, producer: nested), expected: WorldPortalGlass.Elsewhere);
        Assert.Equal(actual: binder.PortalGlass(consumer: nested, glass: out _, producer: session), expected: WorldPortalGlass.Elsewhere);
        Assert.Equal(actual: binder.PortalGlass(consumer: gardenCamera, glass: out _, producer: nested), expected: WorldPortalGlass.Found);
        Assert.True(condition: binder.TryPlacements(placements: out _, view: gardenCamera));

        // The garden retires: the screen holds its last image; the screen goes, and every level beneath it.
        Assert.True(condition: binder.TryDescribeSession(description: out var described, index: BinderScene.Session));
        Assert.True(condition: scene.Host.TryStop(name: described.InstanceName, reason: out _));
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        Assert.True(condition: binder.TryDescribeSession(description: out described, index: BinderScene.Session));
        Assert.True(condition: described.InstanceGone);
        binder.ReconcileScreens(screens: [scene.Rows[0]]);
        binder.PresentFrame(authoritativeTick: 1UL, transforms: []);
    }
    [Fact]
    public void ASessionTheAuthorityRepointsIsFollowedAtTheNextFrame() {
        using var scene = new BinderScene();
        var binder = scene.Binder;
        var repointed = (scene.Rows[BinderScene.Session] with { Source = new WorldScreenSource.Session(Destination: "closed") });

        scene.ConfigureViews();
        binder.ReconcileScreens(screens: [.. scene.Rows.Select(selector: row => ((row.Index == BinderScene.Session) ? repointed : row))]);
        scene.Boot.Server.EnqueueMutation(mutation: new WorldMutation.UpsertScreen(Principal: Principal.Console, Screen: repointed));
        scene.Boot.Server.Advance(stepTicks: BinderScene.Step);
        scene.Host.SettleBootScreenSessions(stepped: true);
        Assert.True(condition: binder.TryDescribeSession(description: out _, index: BinderScene.Session));

        // The destination refuses the session the authority now holds, so the screen keeps showing the one it had.
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        Assert.True(condition: binder.TryDescribeSession(description: out var described, index: BinderScene.Session));
        Assert.Equal(actual: described.Destination, expected: "garden");
    }
    [Fact]
    public void ANestingDepthOfNoneReleasesEverySession() {
        using var scene = new BinderScene(options: new BinderOptions(NestingDepth: 0));

        scene.Binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
        Assert.False(condition: scene.Binder.TryDescribeSession(description: out _, index: BinderScene.Session));
    }
    [Fact]
    public void ADisposedBinderRefusesEveryVerb() {
        using var scene = new BinderScene(options: new BinderOptions(Producers: [HostProducer.Registered()]));
        var binder = scene.Binder;

        binder.Dispose();
        binder.Dispose();
        binder.NotifyDeviceLost();
        binder.ReconcileScreens(screens: []);
        binder.ReconcileCameras(cameras: []);
        binder.DeclareFrameSource(seat: 1, source: new WorldScreenSource.Probe(Id: "probe"));
        binder.RetainFrameSource(seat: 1, source: new WorldScreenSource.Probe(Id: "probe"));
        Assert.Equal(actual: binder.TryQr(ecLevel: null, index: BinderScene.None, payload: "x", quietZoneModules: null), expected: (false, "binder disposed"));
        Assert.Equal(actual: binder.TryCamera(index: BinderScene.None, seat: 1, sensor: WorldCameraSensor.Color), expected: (false, "binder disposed"));
        Assert.Equal(actual: binder.TryCapture(index: BinderScene.None, windowTitle: "editor"), expected: (false, "binder disposed"));
        Assert.Equal(actual: binder.TryDesktop(index: BinderScene.None, monitorIndex: 0), expected: (false, "binder disposed"));
        Assert.Equal(actual: binder.TryView(cameraName: "cam", index: BinderScene.None), expected: (false, "binder disposed"));
        Assert.False(condition: binder.TryGetViewExport(cameraName: "cam", fault: out var fault, generation: out _, ring: out _));
        Assert.Equal(actual: fault, expected: "binder disposed");
        Assert.False(condition: binder.TryGetRenderedKernelHost(fault: out fault, host: out _));
        Assert.Equal(actual: fault, expected: "binder disposed");
        Assert.Equal(actual: binder.ApplyNonMachineSource(index: BinderScene.Session, source: new WorldScreenSource.Session(Destination: "garden")), expected: (false, "binder disposed"));
        binder.Publish(context: scene.Frame(device: new FakeGpuDevice()));
    }

    // A screen source set that implements only what the interface requires.
    private sealed class QuietSources : ISdfScreenSources {
        public IReadOnlyList<int> Screens => [];
        public bool Emits(int screen) => false;
        public Puck.Commands.SourceMapping? MappingOf(int screen) => null;
        public string? ReadOf(int view, int screen) => null;
    }

    // How a scene's boot world and binder are made.
    internal sealed record BinderOptions(
        bool CamerasSupported = true,
        bool CapturesSupported = true,
        bool HostsOnDirectX = false,
        int? NestingDepth = null,
        bool Window = false,
        bool Cabinet = false,
        IReadOnlyList<WorldCamera>? Cameras = null,
        IReadOnlyList<IWorldImageProducer>? Producers = null
    );

    // A boot world whose screens observe the destination 'garden' through sessions, plain, through its camera, through a
    // camera it lacks, and the destination 'closed', which admits nobody; the garden's own screens show the destination
    // 'cellar', which has none, and the garden's camera. The binder is over that world's screens and five more: a view of
    // a declared camera, a view of an undeclared one, text, a probe's output and nothing.
    internal sealed class BinderScene : IDisposable {
        public const int Pattern = 0;
        public const int Session = 1;
        public const int View = 2;
        public const int MissingCamera = 3;
        public const int Text = 4;
        public const int Probe = 5;
        public const int None = 6;
        public const int CameraSession = 7;
        public const int GhostSession = 8;
        public const int ClosedSession = 9;
        public const int Cabinet = 10;
        public const int GardenSession = 1;
        public const int GardenCamera = 2;

        public static readonly ulong Step = FixedTickConversion.PerRate(ratePerSecond: 30u);
        public static readonly WorldCamera DeclaredCamera = new(
            Anchor: null,
            Name: "cam",
            RenderHeight: 72U,
            RenderWidth: 128U,
            Rig: new WorldCameraProgram(
                Name: "cam-rig",
                Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 1f))],
                Version: WorldCameraProgram.CurrentVersion
            )
        );

        private readonly TemporaryDirectory m_bootState = new(prefix: "puck-binder-boot-");
        private readonly TemporaryDirectory m_files = new(prefix: "puck-binder-files-");
        private readonly TemporaryDirectory m_hostState = new(prefix: "puck-binder-host-");

        public BinderScene(BinderOptions? options = null) {
            options ??= new BinderOptions();
            Template = Fixtures.BuildDocument().Screens.Single();
            Camera = DeclaredCamera;

            WorldScreen SessionRow(int index, string destination, string? camera = null) => (Template with {
                Index = index,
                Source = new WorldScreenSource.Session(
                    CameraName: camera,
                    Destination: destination,
                    Projection: (options.Window
                        ? WorldScreenProjection.Window
                        : WorldScreenProjection.Camera)
                ),
            });

            Write(definition: Admitting(definition: (Fixtures.BuildDocument() with { ScreensRaw = [] })), name: "cellar");
            Write(definition: Fixtures.BuildDocument(), name: "closed");
            Write(definition: Observing(definition: (Admitting(definition: Fixtures.BuildDocument()) with {
                CamerasRaw = [Camera],
                ScreensRaw = [Template, SessionRow(destination: "cellar", index: GardenSession), (Template with { Index = GardenCamera, Source = new WorldScreenSource.View(CameraName: "cam") })],
            }), destinations: ["cellar"]), name: "garden");

            var boot = Observing(definition: (Fixtures.BuildDocument() with {
                CamerasRaw = [Camera],
                ScreensRaw = [
                    Template,
                    SessionRow(destination: "garden", index: Session),
                    SessionRow(camera: "cam", destination: "garden", index: CameraSession),
                    SessionRow(camera: "ghost", destination: "garden", index: GhostSession),
                    SessionRow(destination: "closed", index: ClosedSession),
                ],
            }), destinations: ["garden", "closed"]);

            if (options.NestingDepth is { } depth) {
                boot = (boot with { ViewsRaw = (boot.Views with { NestingDepthRaw = depth }) });
            }

            if (options.Cabinet) {
                boot = (boot with {
                    MachinesRaw = [new WorldMachine("cabinet", CabinetEngine.EngineId, CabinetEngine.Configuration())],
                    ScreensRaw = [.. boot.Screens, (Template with { Index = Cabinet, Source = new WorldScreenSource.Machine(Instance: "cabinet", Output: "video") })],
                });
            }
            var bootPath = m_files.PathOf(name: "boot.world.json");

            File.WriteAllBytes(
                bytes: WorldDefinitionSerialization.Serialize(definition: boot),
                path: bootPath
            );
            boot = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: bootPath));

            var cabinet = (Template with { Index = Cabinet, Source = new WorldScreenSource.Machine(Instance: "cabinet", Output: "video") });

            Machines = new WorldMachineHost(
                engines: (options.Cabinet ? [new CabinetEngine()] : []),
                screens: boot.Screens
            );

            var server = new WorldServer(
                definition: boot,
                envelope: new WorldRenderEnvelope(),
                instanceIdentity: WorldInstanceHost.BootInstanceName,
                machines: Machines,
                narrationSink: new WorldConsoleNarrationSink(),
                population: new WorldPopulation(definition: boot),
                profiles: new WorldOwnedWorlds(
                    directory: m_bootState.RootPath,
                    machineId: Guid.NewGuid(),
                    template: boot
                )
            );

            Boot = new WorldInstance(
                documentOrigin: new WorldFileOrigin(resolvedPath: bootPath),
                federation: new WorldFederationIdentity(
                    Authenticator: new InertAuthenticator(),
                    Subject: server.AuthorityIdentity
                ),
                link: new LoopbackTransport(server: server),
                name: WorldInstanceHost.BootInstanceName,
                origin: () => bootPath,
                ownedMachines: Machines,
                server: server
            );
            Host = new WorldInstanceHost(
                applicationStopping: CancellationToken.None,
                machineHostFactory: Fixtures.MachineHostFactory,
                machineId: Guid.NewGuid(),
                resolver: new WorldSessionResolver(),
                seats: WorldEmbodiedSeats.None,
                stateRoot: new WorldStateRoot(path: m_hostState.RootPath)
            );
            Host.AdmitBoot(row: Boot);
            Host.StepInstances(masterDeltaTicks: Step);
            Host.StepInstances(masterDeltaTicks: Step);
            Roster = new PlayerRoster(
                definition: boot,
                link: Boot.Link,
                seatBindings: new WorldSeatBindings(definition: boot)
            );
            Rows = [
                .. boot.Screens.Take(count: 2),
                (Template with { Index = View, Source = new WorldScreenSource.View(CameraName: "cam") }),
                (Template with { Index = MissingCamera, Source = new WorldScreenSource.View(CameraName: "missing") }),
                (Template with { Index = Text, Source = new WorldScreenSource.Text(Lines: ["hello"]) }),
                (Template with { Index = Probe, Source = new WorldScreenSource.Probe(Id: "probe") }),
                (Template with { Index = None, Source = new WorldScreenSource.None() }),
                .. boot.Screens.Skip(count: 2).Where(predicate: static row => (row.Index != Cabinet)),
                cabinet,
            ];
            Binder = new WorldScreenBinder(
                anchors: new WorldClient(
                    composition: new WorldCompositionState(),
                    definition: boot,
                    roster: Roster,
                    seatRouter: new WorldSeatAuthorityRouter()
                ),
                cameraCapture: new FakeCameras(supported: options.CamerasSupported),
                cameras: (options.Cameras ?? boot.Cameras),
                domains: new WorldValueDomainGuard(),
                facts: static () => new NoFacts(),
                hostsOnDirectX: options.HostsOnDirectX,
                instanceHost: Host,
                machines: Machines,
                perception: new WorldPerceptionAnchor(),
                probeKernels: new NullProbeKernelHostService(),
                producers: options.Producers,
                roster: Roster,
                screens: Rows,
                speech: new WorldSpeechClock(),
                stamps: new WorldStampPool(),
                windowCapture: new FakeCaptures(supported: options.CapturesSupported)
            );
        }

        public WorldScreenBinder Binder { get; }
        public WorldInstance Boot { get; }
        public WorldCamera Camera { get; }
        public WorldInstanceHost Host { get; }
        public WorldMachineHost Machines { get; }
        public PlayerRoster Roster { get; }
        // Every screen the binder was built over, in index order.
        public IReadOnlyList<WorldScreen> Rows { get; }
        public WorldScreen Template { get; }

        // Configures the views as a presentation does once its render envelope is known.
        public void ConfigureViews() => Binder.ConfigureViews(
            displayHeight: 1080,
            displayWidth: 1920,
            dynamicTransformCapacity: 64,
            host: new EmptyFrameSource(),
            hostsOnDirectX: false,
            instanceCapacity: 64,
            pipelines: SdfTestPipelines.Cache(),
            programWordCapacity: 4096,
            viewports: new WorldSeatViewports()
        );
        public void Dispose() {
            Binder.Dispose();
            Host.Dispose();
            m_bootState.Dispose();
            m_hostState.Dispose();
            m_files.Dispose();
        }
        // A produced frame whose host resolves a device, or none.
        public FrameContext Frame(IGpuDeviceContext? device) => new(
            AccumulatorTicks: 0UL,
            DeltaTicks: Step,
            ElapsedTicks: Step,
            FrameDeltaTicks: Step,
            Host: new HostContext(capabilities: ((device is null)
                ? new Dictionary<Type, object>()
                : new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device })),
            StepTicks: Step,
            TargetHeight: 1080U,
            TargetWidth: 1920U
        );
        // A residency no device has rendered.
        public SdfWorldResidency Residency(string name) => new(
            frameSource: new EmptyFrameSource(),
            height: 72U,
            kernels: SdfTestPipelines.Kernels(),
            name: name,
            pipelines: SdfTestPipelines.Cache(),
            width: 128U
        );

        // The fixture world, admitting a viewer's session with its whole-world view.
        private static WorldDefinition Admitting(WorldDefinition definition) => (definition with {
            Admission = [new WorldAdmissionEntry(
                Disclosure: WorldDisclosureTier.Replica,
                Domain: WorldAdmissionEntry.AnyAuthority,
                Grants: [new WorldAdmissionGrant(
                    Budget: 64,
                    Capability: WorldCapability.Observe,
                    Subject: GrantSubject.All
                )],
                Mode: WorldAdmissionTrustMode.FederatedAuthority
            )],
        });
        // A world that names ephemeral destinations, each its own document beside it.
        private static WorldDefinition Observing(WorldDefinition definition, IReadOnlyList<string> destinations) => (definition with {
            Destinations = [.. destinations.Select(selector: static name => new WorldDestination(
                Durability: WorldDestinationDurability.Ephemeral,
                Name: SafeName.Parse(candidate: name),
                Reference: name
            ))],
            References = [.. destinations.Select(selector: static name => new WorldReference(Document: name, Name: SafeName.Parse(candidate: name)))],
        });
        private void Write(WorldDefinition definition, string name) => File.WriteAllBytes(
            bytes: WorldDefinitionSerialization.Serialize(definition: definition),
            path: m_files.PathOf(name: WorldDocumentName.DocumentFile(name: name))
        );
    }
    // An image producer a host registers beside the engine's, under a shape of its own.
    private sealed class HostProducer : IWorldImageProducer {
        private const string HostId = "binderHost";

        public ImageContentClass Content => ImageContentClass.Presentation;
        public string Id => HostId;
        public ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

        // The producer, its shape registered once for the process.
        public static HostProducer Registered() {
            if (!WorldImageProducerVocabulary.TryGet(id: HostId, shape: out _)) {
                WorldImageProducerVocabulary.Register(shape: new Shape());
            }

            return new HostProducer();
        }
        public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
            (feed, fault) = (null, "a host producer opens nothing here");

            return false;
        }

        private sealed class Shape : WorldImageProducerShape {
            public override ImageContentClass Content => ImageContentClass.Presentation;
            public override string Id => HostId;
            public override ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

            public override void Validate(WorldScreenSource.Producer source, WorldDefinition definition, string path, List<string> errors) { }
        }
    }
    // A machine whose one video output shows a single pixel.
    private sealed class CabinetEngine : IMachineEngine {
        public const string EngineId = "cabinet-test";

        public MachineEngineDescriptor Descriptor { get; } = new(
            AudioOutputs: [],
            Configuration: new(Fields: [], Id: "puck.cabinet-test.v1"),
            Description: "A one-pixel cabinet",
            Id: EngineId,
            InputPorts: [],
            MemorySpaces: [],
            Operations: [],
            VideoOutputs: [new(Contract: "puck.machine.video.v1", Description: "Screen", Name: "video")]
        );
        public string Id => EngineId;

        public static System.Text.Json.JsonElement Configuration() {
            using var document = System.Text.Json.JsonDocument.Parse(json: "{\"schema\":\"puck.cabinet-test.v1\"}");

            return document.RootElement.Clone();
        }
        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) => new Runtime();
        public IMachineRuntime CreateMachine(MachineCreationRequest request) => new Runtime();

        private sealed class Runtime : IMachineRuntime, IMachineVideoOutputs {
            public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
            public IReadOnlyDictionary<string, IMachineVideoOutput> VideoOutputs { get; } = new Dictionary<string, IMachineVideoOutput> { ["video"] = new Pixel() };

            public bool Advance(ulong deltaTicks) => true;
            public void Dispose() { }
        }
        private sealed class Pixel : IMachineVideoOutput {
            public System.Numerics.Vector3 EmittedLight => System.Numerics.Vector3.Zero;
            public ImagePixelFormat Format => ImagePixelFormat.R8G8B8A8Unorm;
            public int Height => 1;
            public int Width => 1;

            public long WriteFrame(Span<byte> region) => 0L;
        }
    }
    // A frame source that captures nothing.
    private sealed class EmptyFrameSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => throw new InvalidOperationException(message: "no frame is captured here");
    }
    // Overlay facts that hold no predicate.
    private sealed class NoFacts : IOverlayPredicateEvaluator {
        public bool Evaluate(int slot, OverlayPredicate? predicate) => false;
        public bool EvaluateAnySeat(OverlayPredicate? predicate) => false;
    }
    // A camera service that finds no device; supported or not.
    private sealed class FakeCameras(bool supported) : ICameraCaptureService {
        public bool IsSupported => supported;

        public IReadOnlyList<CameraDeviceInfo> EnumerateDevices() => [];
        public bool TryOpenPixels(string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraPixelStream>? graph) {
            graph = null;

            return false;
        }
        public bool TryOpenShared(long adapterLuid, string deviceId, ReadOnlySpan<CameraStreamRequest> streams, [NotNullWhen(true)] out ICameraGraph<ICameraSharedStream>? graph) {
            graph = null;

            return false;
        }
    }
    // A capture service that finds one window and the primary monitor, neither of which hands over a frame; supported
    // or not.
    private sealed class FakeCaptures(bool supported) : INativeImageCaptureService {
        public const string Present = "present";

        public bool IsSupported => supported;

        public bool TryCreateMonitorCapture(int monitorIndex, int width, int height, double refreshRateHz, [NotNullWhen(true)] out INativeImageCaptureFeed? feed, long? adapterLuid = null) {
            feed = ((supported && (monitorIndex == 0))
                ? new Feed()
                : null);

            return (feed is not null);
        }
        public bool TryCreateWindowCapture(string windowTitleFragment, int width, int height, double refreshRateHz, [NotNullWhen(true)] out INativeImageCaptureFeed? feed, long? adapterLuid = null) {
            feed = ((supported && (windowTitleFragment == Present))
                ? new Feed()
                : null);

            return (feed is not null);
        }

        private sealed class Feed : INativeImageCaptureFeed {
            public SharedFenceOrder GpuFenceOrder => default;
            public long GpuRevision => 0L;
            public bool GpuTargetsOutdated => false;
            public bool IsEnded => false;
            public DisplayOutput Output => DisplayOutput.Sdr(format: GpuPixelFormat.B8G8R8A8Unorm);
            public int SourceHeight => 0;
            public int SourceWidth => 0;
            public ISourcePassthroughWindow? Window => null;

            public void AttachGpuTargets(NativeImageGpuCaptureTargets targets) { }
            public void Dispose() { }
            public bool TryCapture(out Surface surface) {
                surface = default;

                return false;
            }
        }
    }
}
