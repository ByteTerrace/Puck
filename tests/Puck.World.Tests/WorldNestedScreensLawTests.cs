using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Assets.Documents;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for nesting worlds through their screens: a world's session screens show views one level deeper to the
/// presentation's nesting depth, so a portal seen through a portal renders recursively, two portals facing each other
/// end at the depth, and a face past it shows its session's fallback colour; a portal face no camera sees is judged
/// unseen.</summary>
public sealed class WorldNestedScreensLawTests {
    // A world named by its document id, whose one screen shows a session of the next world.
    private static WorldDefinition World(string name, string next, string? fallback = null) => (Fixtures.BuildDocument() with {
        DocumentId = name,
        ScreensRaw = [Screen(source: new WorldScreenSource.Session(
            Destination: next,
            Fallback: fallback
        ))],
    });
    private static WorldScreen Screen(WorldScreenSource source) => new(
        HalfDepth: 0.1f,
        HalfHeight: 0.9f,
        HalfWidth: 1.2f,
        Index: 3,
        Origin: new DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: 0f)),
        Right: new DocumentVector3(value: Vector3.UnitX),
        Round: 0f,
        Route: WorldScreenRoute.Passive,
        Source: source,
        Up: new DocumentVector3(value: Vector3.UnitY)
    );
    // Every level beneath a presented world, opened to a nesting depth: the screens of each view, keyed by its name.
    private static Dictionary<string, WorldNestedScreens<Level>> Nest(IReadOnlyDictionary<string, WorldDefinition> worlds, string root, int nestingDepth) {
        var sessions = new Sessions(worlds: worlds);
        var screens = new WorldNestedScreens<Level>(
            definition: () => worlds[root],
            depth: 0,
            head: "routed$root",
            shares: static _ => true,
            world: root
        );
        var levels = new Dictionary<string, WorldNestedScreens<Level>>(comparer: StringComparer.Ordinal) {
            [screens.Head] = screens,
        };
        var pending = new Queue<WorldNestedScreens<Level>>();

        pending.Enqueue(item: screens);

        while (pending.TryDequeue(result: out var level)) {
            _ = level.Reconcile(
                nestingDepth: nestingDepth,
                sessions: sessions
            );

            foreach (var child in level.Views) {
                levels[child.Screens.Head] = child.Screens;
                pending.Enqueue(item: child.Screens);
            }
        }

        return levels;
    }

    // THE LAW: a chain of worlds nests to the depth and no further. Four worlds each show the next; from the first, at
    // depth 2, views open for the second and third world only, the third's screen shows its fallback colour, and the
    // fourth world is never asked for; at depth 3 the fourth opens too.
    [Fact]
    public void AChainNestsToTheDepthAndNoFurther() {
        var worlds = new Dictionary<string, WorldDefinition>(comparer: StringComparer.Ordinal) {
            ["a"] = World(name: "a", next: "b"),
            ["b"] = World(name: "b", next: "c"),
            ["c"] = World(fallback: "#102030", name: "c", next: "d"),
            ["d"] = World(name: "d", next: "a"),
        };
        var two = Nest(
            nestingDepth: 2,
            root: "a",
            worlds: worlds
        );

        Assert.Equal(
            actual: two.Keys.Order(comparer: StringComparer.Ordinal),
            expected: ["routed$root", "routed$root$3", "routed$root$3$3"]
        );
        Assert.Equal(
            actual: two.Values.Select(selector: static level => level.Depth).Order(),
            expected: [0, 1, 2]
        );
        Assert.Empty(collection: two["routed$root$3$3"].Children);
        Assert.Equal(
            actual: two["routed$root$3$3"].InstanceOf(screen: 3),
            expected: WorldViewNames.Source(
                producer: WorldImageProducerSettings.ColorId,
                settings: WorldPortalFallback.SourceOf(session: new WorldScreenSource.Session(Destination: "d", Fallback: "#102030")).Settings
            )
        );
        Assert.Equal(
            actual: two["routed$root$3"].InstanceOf(screen: 3),
            expected: "routed$root$3$3"
        );

        var three = Nest(
            nestingDepth: 3,
            root: "a",
            worlds: worlds
        );

        Assert.Equal(expected: 4, actual: three.Count);
        Assert.Equal(
            actual: three["routed$root$3$3"].InstanceOf(screen: 3),
            expected: "routed$root$3$3$3"
        );
    }
    // THE LAW: two portals facing each other end at the depth. Each of two worlds shows the other; the levels alternate
    // between them, one view a level, and stop at the depth, whose face shows the default fallback colour, black. The red
    // leg is a depth one deeper, which opens one view more.
    [Fact]
    public void TwoPortalsFacingEachOtherEndAtTheDepth() {
        var worlds = new Dictionary<string, WorldDefinition>(comparer: StringComparer.Ordinal) {
            ["a"] = World(name: "a", next: "b"),
            ["b"] = World(name: "b", next: "a"),
        };

        foreach (var depth in ((int[])[0, 1, 3, 5])) {
            var levels = Nest(
                nestingDepth: depth,
                root: "a",
                worlds: worlds
            );

            Assert.Equal(expected: (depth + 1), actual: levels.Count);

            var deepest = levels.Values.Single(predicate: level => (level.Depth == depth));

            Assert.Empty(collection: deepest.Children);
            Assert.Equal(
                actual: deepest.InstanceOf(screen: 3),
                expected: WorldViewNames.Source(
                    producer: WorldImageProducerSettings.ColorId,
                    settings: WorldPortalFallback.SourceOf(session: new WorldScreenSource.Session(Destination: "a")).Settings
                )
            );
            Assert.Equal(
                actual: WorldPortalFallback.SourceOf(session: new WorldScreenSource.Session(Destination: "a")).Settings!["color"].GetString(),
                expected: WorldPortalFallback.DefaultColor
            );
        }
    }
    // THE LAW: lowering the depth closes the levels past it, every one beneath them, and a steady frame changes nothing.
    // A world shown three deep is reconciled again at depth one: the first level's view closes its view, which closes the
    // one beneath it, and its face shows the fallback; a reconcile that moves nothing reports no change, allocates
    // nothing and keeps the reads it published.
    [Fact]
    public void LoweringTheDepthClosesEveryLevelPastItAndASteadyFrameMovesNothing() {
        var worlds = new Dictionary<string, WorldDefinition>(comparer: StringComparer.Ordinal) {
            ["a"] = World(name: "a", next: "b"),
            ["b"] = World(name: "b", next: "a"),
        };
        var levels = Nest(
            nestingDepth: 3,
            root: "a",
            worlds: worlds
        );
        var root = levels["routed$root"];
        var first = levels["routed$root$3"];
        var sessions = new Sessions(worlds: worlds);
        var reads = root.Reads;

        Assert.False(condition: root.Reconcile(
            nestingDepth: 3,
            sessions: sessions
        ));

        var before = GC.GetAllocatedBytesForCurrentThread();

        _ = root.Reconcile(
            nestingDepth: 3,
            sessions: sessions
        );

        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));
        Assert.Same(actual: root.Reads, expected: reads);
        Assert.True(condition: first.Reconcile(
            nestingDepth: 1,
            sessions: sessions
        ));
        Assert.Empty(collection: first.Children);
        Assert.Equal(expected: 2, actual: sessions.Closed);
        Assert.StartsWith(
            actualString: first.InstanceOf(screen: 3),
            expectedStartString: "source$color$"
        );
    }
    // THE LAW: a world shown through a screen shows its own sources and nothing of the local device. A producer whose
    // content is a function of its settings shows its shared instance; a machine and a probe show a source instance of
    // that world's own host, which another world's equal row never shares; a camera view shows a view of that world
    // through its own camera, named under the level; a producer of the local device's content (a camera) shows nothing.
    // The red leg is the boot world's machine row, whose instance is another.
    [Fact]
    public void AWorldShownThroughAScreenShowsItsOwnSourcesAndNothingOfTheLocalDevice() {
        WorldScreenSource[] sources = [
            WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.TestPatternId, settings: new WorldTestPatternSettings(Height: 4, Width: 4)),
            WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.CameraId, settings: new WorldCameraSettings()),
            new WorldScreenSource.Machine(Instance: "cabinet", Output: "video"),
            new WorldScreenSource.Probe(Id: "feed"),
            new WorldScreenSource.View(CameraName: "billboard"),
        ];
        var world = (Fixtures.BuildDocument() with {
            CamerasRaw = [Camera(name: "billboard")],
            ScreensRaw = [.. sources.Select(selector: static (source, index) => (Screen(source: source) with { Index = index }))],
        });
        var screens = new WorldNestedScreens<Level>(
            definition: () => world,
            depth: 1,
            head: "session$0",
            shares: static id => (id == WorldImageProducerSettings.TestPatternId),
            world: "garden"
        );

        _ = screens.Reconcile(
            nestingDepth: 3,
            sessions: new Sessions(worlds: new Dictionary<string, WorldDefinition>())
        );

        Assert.StartsWith(
            actualString: screens.InstanceOf(screen: 0),
            expectedStartString: "source$testPattern$"
        );
        Assert.Null(@object: screens.InstanceOf(screen: 1));
        Assert.StartsWith(
            actualString: screens.InstanceOf(screen: 2),
            expectedStartString: "source$machine$"
        );
        Assert.NotEqual(
            actual: screens.InstanceOf(screen: 2),
            expected: WorldViewNames.Source(
                producer: WorldImageProducerSettings.MachineId,
                settings: RenderGraphSettingsOf(source: new WorldScreenSource.Machine(Instance: "cabinet", Output: "video"))
            )
        );
        Assert.StartsWith(
            actualString: screens.InstanceOf(screen: 3),
            expectedStartString: "source$probe$"
        );
        Assert.Equal(
            actual: screens.InstanceOf(screen: 4),
            expected: "session$0$camera$billboard"
        );
        Assert.Equal(
            actual: screens.Sources.Where(predicate: static source => (source.SourceProducer is WorldImageProducerSettings.MachineId or WorldImageProducerSettings.ProbeId)).Select(selector: WorldSourceInstances.WorldOf),
            expected: ["garden", "garden"]
        );
        // The camera view is filmed under the level, and a camera view reads it, and every camera view, at its previous
        // frame, never within one.
        Assert.Equal(
            actual: screens.Cameras.Select(selector: static camera => (camera.Name, camera.Camera.Name)),
            expected: [("session$0$camera$billboard", "billboard")]
        );
        Assert.Contains(collection: screens.Reads, expected: "session$0$camera$billboard");
        Assert.DoesNotContain(collection: screens.FilmReads, expected: "session$0$camera$billboard");
        Assert.Equal(actual: screens.CameraReads, expected: ["session$0$camera$billboard"]);
    }

    private static WorldCamera Camera(string name) => new(
        Anchor: null,
        Name: name,
        RenderHeight: 72U,
        RenderWidth: 96U,
        Rig: new WorldCameraProgram(
            Name: "fixed",
            Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f))],
            Version: WorldCameraProgram.CurrentVersion
        )
    );
    // The settings the boot world's own row of a source reads its source instance through.
    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement>? RenderGraphSettingsOf(WorldScreenSource source) =>
        WorldSourceInstances.Of(shown: [source], world: WorldDefinitionLoader.BootInstanceName).Instances[0].Settings;

    // THE LAW: a sampled slab can be seen from either side; only a slab wholly outside the frustum is culled.
    [Fact]
    public void AFaceIsSeenFromEitherSideWithinTheFrustum() {
        var glass = Screen(source: new WorldScreenSource.None());

        CameraSnapshot Looking(Vector3 from, Vector3 at) => CameraSnapshot.LookAt(
            fieldOfViewRadians: 1f,
            position: from,
            target: at,
            viewportHeight: 64,
            viewportWidth: 64
        );

        Assert.True(condition: WorldPortalVisibility.Sees(camera: Looking(from: new Vector3(x: 0f, y: 1f, z: 5f), at: new Vector3(x: 0f, y: 1f, z: 0f)), glass: glass));
        Assert.True(condition: WorldPortalVisibility.Sees(camera: Looking(from: new Vector3(x: 0f, y: 1f, z: 5f), at: new Vector3(x: 3f, y: 1f, z: 0f)), glass: glass));
        // Turned away, the glass is behind the camera.
        Assert.False(condition: WorldPortalVisibility.Sees(camera: Looking(from: new Vector3(x: 0f, y: 1f, z: 5f), at: new Vector3(x: 0f, y: 1f, z: 10f)), glass: glass));
        // Looking well to the side, every corner is past the frustum's left side.
        Assert.False(condition: WorldPortalVisibility.Sees(camera: Looking(from: new Vector3(x: 0f, y: 1f, z: 5f), at: new Vector3(x: 20f, y: 1f, z: 5f)), glass: glass));
        // The shader samples the back and sides with the same screen material as the front.
        Assert.True(condition: WorldPortalVisibility.Sees(camera: Looking(from: new Vector3(x: 0f, y: 1f, z: -5f), at: new Vector3(x: 0f, y: 1f, z: 0f)), glass: glass));
        Assert.True(condition: WorldPortalVisibility.Sees(camera: Looking(from: new Vector3(x: 2f, y: 1f, z: -0.1f), at: new Vector3(x: 0f, y: 1f, z: -0.1f)), glass: glass));
        Assert.True(condition: WorldPortalVisibility.Sees(camera: (Looking(from: new Vector3(x: 0f, y: 1f, z: -0.1f), at: new Vector3(x: 0f, y: 1f, z: -1f)) with { Near = 0.05f }), glass: glass));
    }
    // THE LAW: the slab spans from the face at its origin to its back plane one full depth behind, and a camera whose
    // near plane clips either plane away still sees the slab through the other. Testing only one plane's four corners
    // would cull a slab that is in view.
    [Fact]
    public void ASlabIsSeenThroughEitherPlaneOfItsCornersAlone() {
        var glass = Screen(source: new WorldScreenSource.None());

        CameraSnapshot Looking(float z, float towardZ) => (CameraSnapshot.LookAt(
            fieldOfViewRadians: 1f,
            position: new Vector3(x: 0f, y: 1f, z: z),
            target: new Vector3(x: 0f, y: 1f, z: towardZ),
            viewportHeight: 64,
            viewportWidth: 64
        ) with { Near = 0.2f });

        // Behind the slab, looking at it: the back plane (z = -0.2) lies inside the near plane; only the face corners
        // (z = 0) lie beyond it.
        Assert.True(condition: WorldPortalVisibility.Sees(camera: Looking(towardZ: 1f, z: -0.3f), glass: glass));
        // In front of the face, looking at it: the face corners lie inside the near plane; only the back corners lie
        // beyond it.
        Assert.True(condition: WorldPortalVisibility.Sees(camera: Looking(towardZ: -1f, z: 0.1f), glass: glass));
    }
    // THE LAW: an authority can keep a session while its screen changes projection, camera or resolution. Its view
    // must reopen with those settings even when the session provider still holds the same destination.
    [Fact]
    public void EditingASessionsRenderingReplacesItsViewWithoutChangingItsDestination() {
        var world = World(name: "a", next: "b");
        var sessions = new Sessions(worlds: new Dictionary<string, WorldDefinition> {
            ["b"] = World(name: "b", next: "a"),
        });
        var screens = new WorldNestedScreens<Level>(
            definition: () => world,
            depth: 1,
            head: "session$3",
            shares: static _ => true,
            world: "a"
        );

        Assert.True(condition: screens.Reconcile(nestingDepth: 3, sessions: sessions));
        var before = Assert.Single(collection: screens.Views);
        var source = ((WorldScreenSource.Session)world.Screens[0].Source);

        world = world with {
            ScreensRaw = [Screen(source: source with {
                CameraName = "other",
                Resolution = new WorldScreenResolution(Height: 96, Width: 128),
            })],
        };

        Assert.True(condition: screens.Reconcile(nestingDepth: 3, sessions: sessions));
        Assert.NotSame(expected: before, actual: Assert.Single(collection: screens.Views));
        Assert.Equal(expected: 1, actual: sessions.Closed);
        Assert.False(condition: screens.Reconcile(nestingDepth: 3, sessions: sessions));
    }

    // One session view: the destination's screens one level deeper.
    private sealed record Level(string Destination, WorldNestedScreens<Level> Screens);
    // Opens a view of every session whose destination exists, and counts the views closed.
    private sealed class Sessions(IReadOnlyDictionary<string, WorldDefinition> worlds) : IWorldNestedSessions<Level> {
        public int Closed { get; private set; }

        public Level? Open(WorldNestedScreens<Level> screens, int screen, WorldScreenSource.Session source, string name) => (worlds.TryGetValue(
            key: source.Destination,
            value: out var destination
        )
            ? new Level(
                Destination: source.Destination,
                Screens: new WorldNestedScreens<Level>(
                    definition: () => destination,
                    depth: (screens.Depth + 1),
                    head: name,
                    shares: static _ => true,
                    world: source.Destination
                )
            )
            : null);
        public bool Holds(Level child, WorldNestedScreens<Level> screens, int screen, WorldScreenSource.Session source) => string.Equals(
            a: child.Destination,
            b: source.Destination,
            comparisonType: StringComparison.Ordinal
        );
        public void Close(Level child) {
            child.Screens.Close(sessions: this);
            Closed++;
        }
    }
}
