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
        var scene = new WorldRoutedScene(domains: new WorldValueDomainGuard(), bodyColor: north.Mirror.BodyColor, endpoint: north, hostFrame: static () => null);
        var frame = Capture(source: scene.FrameSource);
        var sky = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(
            definition: destination,
            mirror: north.FollowState(),
            revision: north.Mirror.DefinitionRevision
        );

        Assert.Equal(actual: frame.Sky.Layers.ToArray(), expected: sky.Sky.Layers.ToArray());
        Assert.Equal(actual: frame.Sky.Block, expected: sky.Sky.Block);
        Assert.NotEqual(expected: new SdfSky().Layers.ToArray(), actual: frame.Sky.Layers.ToArray());

        var retained = frame.Sky.Layers.ToArray();

        north.Mirror.DeliverDefinition(definition: AwayDocument(), version: default);
        _ = Capture(source: scene.FrameSource);
        var changed = Capture(source: scene.FrameSource);

        Assert.NotEqual(expected: retained, actual: changed.Sky.Layers.ToArray());
    }
    // A routed view guards the values its destination binds as the viewer's own world does: a cloud scale that goes to
    // zero, which its field does not admit, holds the last value the view presented and is reported.
    [Fact]
    public void ARoutedViewsBoundCloudScaleThatGoesToZeroHoldsItsLastValueAndIsReported() {
        static WorldDefinition Scaled(double value) => WorldValueDomainLawTests.WithRow(
            definition: (AwayDocument() with {
                RenderRaw = (WorldRenderDefaults.Absent with {
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar(binding: $"state.{WorldValueDomainLawTests.Row}"))]),
                }),
            }),
            value: value
        );

        var domains = new WorldValueDomainGuard();
        var reports = new List<string>();

        domains.Report = reports.Add;

        using var north = Endpoint(definition: Scaled(value: 0.5d), identity: Away, position: AwayPose);
        var scene = new WorldRoutedScene(domains: domains, bodyColor: north.Mirror.BodyColor, endpoint: north, hostFrame: static () => null);

        Assert.Equal(expected: 0.5f, actual: Capture(source: scene.FrameSource).Sky.First<SdfSkyClouds>().Scale);

        north.Mirror.DeliverDefinition(definition: Scaled(value: 0d), version: default);
        _ = Capture(source: scene.FrameSource);

        Assert.Equal(expected: 0.5f, actual: Capture(source: scene.FrameSource).Sky.First<SdfSkyClouds>().Scale);
        Assert.Single(collection: reports);
        Assert.Contains(expectedSubstring: $"render.sky.layers[0].scale reads 0 from state.{WorldValueDomainLawTests.Row}", actualString: reports[0]);
    }
    // A destination replaced by another world, a different activation delivering a document, starts a routed view's
    // bindings fresh: the new world's cloud scale, out of range at its first look, does not wear the old world's last value.
    [Fact]
    public void AReplacedDestinationStartsItsRoutedViewsBindingsFresh() {
        static WorldDefinition Scaled(double value) => WorldValueDomainLawTests.WithRow(
            definition: (AwayDocument() with {
                RenderRaw = (WorldRenderDefaults.Absent with {
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar(binding: $"state.{WorldValueDomainLawTests.Row}"))]),
                }),
            }),
            value: value
        );

        var domains = new WorldValueDomainGuard();
        var first = new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 0L);
        var second = new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 0L);

        domains.Report = static _ => { };

        using var north = Endpoint(definition: Scaled(value: 0.5d), identity: Away, position: AwayPose);
        var scene = new WorldRoutedScene(domains: domains, bodyColor: north.Mirror.BodyColor, endpoint: north, hostFrame: static () => null);

        north.Mirror.DeliverDefinition(definition: Scaled(value: 0.5d), version: first);

        Assert.Equal(expected: 0.5f, actual: Capture(source: scene.FrameSource).Sky.First<SdfSkyClouds>().Scale);

        // The same world, written out of range: the view keeps what it last presented.
        north.Mirror.DeliverDefinition(definition: Scaled(value: 0d), version: new WorldDocumentVersion(Activation: first.Activation, Sequence: 1L));
        _ = Capture(source: scene.FrameSource);

        Assert.Equal(expected: 0.5f, actual: Capture(source: scene.FrameSource).Sky.First<SdfSkyClouds>().Scale);

        // Another world answers at the destination: it has presented nothing yet, so it shows the engine default.
        north.Mirror.DeliverDefinition(definition: Scaled(value: 0d), version: second);
        _ = Capture(source: scene.FrameSource);

        Assert.Equal(expected: SdfSkyClouds.DefaultScale, actual: Capture(source: scene.FrameSource).Sky.First<SdfSkyClouds>().Scale);
    }
    // A world's lifetime and its document are one publication: a reader of the destination's delivered document sees, in
    // one snapshot, the document and the number of replacements that brought it, so it can never pair a new world's
    // lifetime with the old world's document. A writer replaces the world over and over while a reader samples.
    [Fact]
    public void AReplacementIsPublishedWithItsLifetimeInOneSnapshot() {
        var activations = Enumerable.Range(count: 2000, start: 0).Select(selector: static _ => Guid.NewGuid()).ToArray();
        var expected = new Dictionary<Guid, int>(capacity: activations.Length);

        for (var index = 0; (index < activations.Length); index++) {
            expected[activations[index]] = (index + 1);
        }

        var worlds = activations.Select(selector: static (_, index) => WorldValueDomainLawTests.WithRow(
            definition: AwayDocument(),
            value: index
        )).ToArray();

        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);

        var finished = 0;
        var torn = 0;
        var samples = 0L;
        var writer = new Thread(start: () => {
            for (var index = 0; (index < activations.Length); index++) {
                north.Mirror.DeliverDefinition(definition: worlds[index], version: new WorldDocumentVersion(Activation: activations[index], Sequence: 0L));
            }

            Volatile.Write(location: ref finished, value: 1);
        });

        writer.Start();

        do {
            var delivered = north.Mirror.Document;

            samples++;

            // Each activation's first document arrives with exactly the lifetime of its place in the sequence, and the
            // definition it carries is that world's own.
            if (
                expected.TryGetValue(key: delivered.Version.Activation, value: out var lifetime) &&
                ((delivered.Lifetime != lifetime) || !ReferenceEquals(objA: delivered.Definition, objB: worlds[(lifetime - 1)]))
            ) {
                torn++;
            }
        } while (Volatile.Read(location: ref finished) == 0);

        writer.Join();

        Assert.True(condition: (samples > 0L));
        Assert.Equal(actual: torn, expected: 0);
        Assert.Equal(expected: activations.Length, actual: north.Mirror.Document.Lifetime);
    }
    // The type system makes the guard a required argument of every consumer; a null reaching one through reflection, a
    // default path or a nullable-oblivious caller is refused at the door, naming the argument, never stored to fail on a
    // later frame.
    [Fact]
    public void EveryConsumerOfTheValueDomainGuardRefusesNullByName() {
        var document = AwayDocument();
        var mirror = ClientFixtures.StateMirror(definition: document);
        var program = new WorldCameraProgram(
            Name: "p",
            Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 1f))],
            Version: WorldCameraProgram.CurrentVersion
        );
        using var north = Endpoint(definition: document, identity: Away, position: AwayPose);

        static void Names(Action refusing) {
            var thrown = Assert.Throws<ArgumentNullException>(testCode: refusing);

            Assert.Equal(expected: "domains", actual: thrown.ParamName);
        }

        Names(refusing: () => _ = new WorldEnvironmentResolve(domains: null!));
        Names(refusing: () => _ = new WorldThemeResolve(domains: null!));
        Names(refusing: () => _ = WorldMarkerAlphas.Resolve(domains: null!, index: 0, marker: new WorldMarkerRow(Id: "m", Source: new WorldMarkerSource.Speakers(), Icon: "i", Ring: null, Style: new WorldMarkerStyle(ChipAlpha: 1f, Size: 1f, RingAlpha: 1f, RingColor: new BindableColor(Raw: "#000000"))), mirror: mirror));
        Names(refusing: () => _ = WorldCameraRigCompiler.Compile(definition: document, domains: null!, mirror: mirror, program: program));
        Names(refusing: () => _ = new WorldCameraRigCompiler.Cache().Resolve(definition: document, domains: null!, mirror: mirror, program: program));
        Names(refusing: () => _ = new WorldSeatViewState().ResolveChase(bodyOrientation: System.Numerics.Quaternion.Identity, definition: document, domains: null!, mirror: mirror, views: WorldViewDefaults.Absent));
        Names(refusing: () => _ = WorldSessionSceneEmitter.ResolveCamera(cameraName: null, domains: null!, height: 1u, mirror: north.Mirror, width: 1u));
        Names(refusing: () => _ = new WorldSessionSceneEmitter(domains: null!, effectiveCameraName: null, mirror: north.Mirror));
        Names(refusing: () => _ = new WorldRoutedScene(bodyColor: north.Mirror.BodyColor, domains: null!, endpoint: north, hostFrame: static () => null));

        // The two roots that take a long list of services refuse the guard before anything else.
        foreach (var type in new[] { typeof(WorldFramePresenter), Assert.IsAssignableFrom<Type>(@object: Type.GetType(typeName: "Puck.World.WorldScreenBinder, Puck.World")) }) {
            var constructor = Assert.Single(collection: type.GetConstructors());
            var arguments = constructor.GetParameters().Select(selector: static parameter => (parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(type: parameter.ParameterType)
                : null)).ToArray();
            var thrown = Assert.IsType<ArgumentNullException>(@object: Assert.IsType<System.Reflection.TargetInvocationException>(@object: Record.Exception(testCode: () => constructor.Invoke(parameters: arguments))).InnerException);

            Assert.Equal(expected: "domains", actual: thrown.ParamName);
        }
    }
    // A routed view's sky clock (its stars' twinkle, its clouds' drift, its media's motion) is the destination's presented
    // tick: the sky it shows moves as its world does, whatever the viewer's own world has reached. Its presentation time
    // is still the viewer's frame's.
    [Fact]
    public void ARoutedViewsSkyClockIsTheDestinationsTick() {
        using var north = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        SdfFrame? viewer = null;
        var scene = new WorldRoutedScene(domains: new WorldValueDomainGuard(), bodyColor: north.Mirror.BodyColor, endpoint: north, hostFrame: () => viewer);

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
