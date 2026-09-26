using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the mappings the screen binder publishes through <see cref="WorldScreenMappingSet"/>: each screen row's
/// <see cref="WorldScreenMappings.Of"/> mapping, named by its source instance's handle, at the extent its image has. A
/// screen at an arbitrary pose maps known points to known source pixels in fixed point, identically on every run; a
/// view and a session name their instances with document extents; a live presentation source, an unknown extent or no
/// source publishes nothing and says why; and a steady frame publishes without allocating.
/// </summary>
public sealed class WorldScreenMappingLawTests {
    private const int Height = 144;
    private const int Width = 160;

    // A pose no axis lines up with: the face turned, tilted and rolled.
    private static readonly Quaternion Pose = Quaternion.CreateFromYawPitchRoll(
        pitch: 0.3f,
        roll: 0.2f,
        yaw: 0.7f
    );
    private static readonly Vector3 Origin = new(x: 4f, y: 2.5f, z: -3f);
    private static readonly WorldScreenSource.Producer Pattern = WorldImageProducerSettings.SourceOf(
        id: WorldImageProducerSettings.TestPatternId,
        settings: new WorldTestPatternSettings(
            Height: Height,
            Width: Width
        )
    );

    private static WorldCamera Camera(string name) => new(
        Anchor: null,
        Name: name,
        RenderHeight: 72U,
        RenderWidth: 128U,
        Rig: new WorldCameraProgram(
            Name: $"{name}-rig",
            Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f))],
            Version: WorldCameraProgram.CurrentVersion
        )
    );
    // A screen at the arbitrary pose, 2.4 units wide and 1.8 tall, showing a source.
    private static WorldScreen Screen(int index, WorldScreenSource source) => new(
        HalfDepth: 0.1f,
        HalfHeight: 0.9f,
        HalfWidth: 1.2f,
        Index: index,
        Origin: new DocumentVector3(value: Origin),
        Right: new DocumentVector3(value: Vector3.Transform(value: Vector3.UnitX, rotation: Pose)),
        Round: 0f,
        Route: new WorldScreenRoute(
            EngageRadius: 0f,
            Engageable: false,
            Input: SourceDestination.Simulation
        ),
        Source: source,
        Up: new DocumentVector3(value: Vector3.Transform(value: Vector3.UnitY, rotation: Pose))
    );
    // A ray along the face's inward normal onto the centre of source pixel (x, y), through the glass's bezel inset.
    private static SourceRay RayAt(double x, double y) {
        const double Inner = (1.0 - (2.0 * WorldScreenMappings.Bezel));
        var u = (WorldScreenMappings.Bezel + (Inner * ((x + 0.5) / Width)));
        var v = (WorldScreenMappings.Bezel + (Inner * ((y + 0.5) / Height)));
        var right = Vector3.Transform(value: Vector3.UnitX, rotation: Pose);
        var up = Vector3.Transform(value: Vector3.UnitY, rotation: Pose);
        var normal = Vector3.Normalize(value: Vector3.Cross(vector1: right, vector2: up));
        var point = ((Origin + (right * ((float)(((2.0 * u) - 1.0) * 1.2)))) - (up * ((float)(((2.0 * v) - 1.0) * 0.9))));

        return new SourceRay(
            Direction: FixedVector3.FromVector3(value: -normal),
            Origin: FixedVector3.FromVector3(value: (point + (normal * 6f)))
        );
    }

    // A screen at an arbitrary pose publishes its row's mapping named by its source instance, and maps a ray onto the
    // centre of a known pixel to that pixel, identically every time it is published.
    [Fact]
    public void AScreenAtAnArbitraryPoseMapsKnownPointsToKnownPixels() {
        var images = new Images();
        var set = new WorldScreenMappingSet();
        var screens = new[] { Screen(index: 3, source: Pattern) };

        images.Extents[3] = (Width, Height);
        set.Reconcile(cameras: [], screens: screens);
        set.Publish(images: images);

        Assert.True(condition: set.TryGet(mapping: out var mapping, screen: 3));
        Assert.Equal(
            actual: mapping.Source,
            expected: WorldSourceInstances.Of(shown: [Pattern]).HandleOf(screen: 0)
        );
        Assert.StartsWith(
            actualString: mapping.Source.Name,
            expectedStartString: $"source${WorldImageProducerSettings.TestPatternId}$"
        );
        Assert.Equal(
            actual: (mapping.SourceWidth, mapping.SourceHeight, mapping.Destination, mapping.Warp?.Pass),
            expected: (Width, Height, SourceDestination.Simulation, WorldScreenMappings.GlassPass)
        );

        (int X, int Y)[] pixels = [(0, 0), ((Width - 1), (Height - 1)), (37, 101), (122, 9)];
        var first = pixels.Select(selector: pixel => mapping.MapRay(ray: RayAt(x: pixel.X, y: pixel.Y))).ToArray();

        Assert.All(
            action: pair => Assert.Equal(
                actual: (pair.Hit.IsOnSource, pair.Hit.PixelX, pair.Hit.PixelY),
                expected: (true, ((long)pair.Pixel.X), ((long)pair.Pixel.Y))
            ),
            collection: first.Zip(resultSelector: static (hit, pixel) => (Hit: hit, Pixel: pixel), second: pixels)
        );

        // Reconciled and published again from the same rows, the mapping maps every point to the identical hit.
        var again = new WorldScreenMappingSet();

        again.Reconcile(cameras: [], screens: screens);
        again.Publish(images: images);
        Assert.True(condition: again.TryGet(mapping: out var republished, screen: 3));
        Assert.Equal(
            actual: pixels.Select(selector: pixel => republished.MapRay(ray: RayAt(x: pixel.X, y: pixel.Y))),
            expected: first
        );

        // The glass's bezel maps to no pixel.
        Assert.False(condition: mapping.MapRay(ray: RayAt(x: -2.0, y: 40.0)).IsOnSource);
    }
    // Screens showing equal sources name one source instance; a view names its camera's registration at the camera's
    // extent and a session its screen's session view at the engine default, both without asking the running images.
    [Fact]
    public void EachSourceKindNamesItsInstance() {
        var images = new Images();
        var set = new WorldScreenMappingSet();

        images.Extents[0] = (Width, Height);
        images.Extents[1] = (Width, Height);
        set.Reconcile(
            cameras: [Camera(name: "eye")],
            screens: [
                Screen(index: 0, source: Pattern),
                Screen(index: 1, source: Pattern),
                Screen(index: 2, source: new WorldScreenSource.View(CameraName: "eye")),
                Screen(index: 5, source: new WorldScreenSource.Session(Destination: "arena")),
            ]
        );
        set.Publish(images: images);

        Assert.Equal(
            actual: set.Mappings.Select(selector: static mapping => (mapping.Source.Kind, mapping.SourceWidth, mapping.SourceHeight)),
            expected: [
                (SourceHandleKind.Producer, Width, Height),
                (SourceHandleKind.Producer, Width, Height),
                (SourceHandleKind.Instance, 128, 72),
                (SourceHandleKind.Instance, 160, 144),
            ]
        );
        Assert.Equal(actual: set.Mappings[1].Source, expected: set.Mappings[0].Source);
        Assert.Equal(
            actual: (set.Mappings[2].Source.Name, set.Mappings[3].Source.Name),
            expected: ("eye", WorldViewNames.Session(screen: 5))
        );
    }
    // A screen showing no image, a live presentation source no row names, or an image of unknown extent publishes no
    // mapping, and the line world.screens prints says why; a screen that publishes prints the pane line's form.
    [Fact]
    public void AScreenWithoutAMappingSaysWhy() {
        var images = new Images();
        var set = new WorldScreenMappingSet();

        images.Extents[0] = (Width, Height);
        images.Extents[1] = (Width, Height);
        images.Live.Add(item: 1);
        set.Reconcile(
            cameras: [],
            screens: [
                Screen(index: 0, source: Pattern),
                Screen(index: 1, source: Pattern),
                Screen(index: 2, source: Pattern),
                Screen(index: 4, source: new WorldScreenSource.None()),
                Screen(index: 6, source: new WorldScreenSource.View(CameraName: "missing")),
            ]
        );

        Assert.Equal(actual: set.Describe(screen: 0), expected: "none (not published)");

        set.Publish(images: images);

        Assert.StartsWith(
            actualString: set.Describe(screen: 0),
            expectedStartString: $"producer:{set.Mappings[0].Source.Name} surface origin 4,2.5,-3 "
        );
        Assert.EndsWith(
            actualString: set.Describe(screen: 0),
            expectedEndString: $"source {Width}x{Height} crop 0,0 {Width}x{Height} layout Identity fit Stretch warp {WorldScreenMappings.GlassPass} inverse destination Simulation"
        );
        Assert.Equal(
            actual: new[] { 1, 2, 4, 6, 9 }.Select(selector: screen => set.Describe(screen: screen)),
            expected: [
                "none (a live presentation source no row names)",
                "none (the image's extent is not known yet)",
                "none (no image source)",
                "none (camera 'missing' not declared)",
                null,
            ]
        );
        Assert.Single(collection: set.Mappings);
    }
    // A frame whose handles and extents hold publishes the mappings it published before, allocating nothing; a new
    // extent publishes a new mapping.
    [Fact]
    public void ASteadyFramePublishesWithoutAllocating() {
        var images = new Images();
        var set = new WorldScreenMappingSet();

        images.Extents[0] = (Width, Height);
        set.Reconcile(
            cameras: [Camera(name: "eye")],
            screens: [
                Screen(index: 0, source: Pattern),
                Screen(index: 2, source: new WorldScreenSource.View(CameraName: "eye")),
            ]
        );
        set.Publish(images: images);

        var published = set.Mappings.ToArray();

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => set.Publish(images: images)),
            expected: 0L
        );
        Assert.Equal(actual: set.Mappings.Count, expected: published.Length);

        for (var index = 0; (index < published.Length); index++) {
            Assert.Same(actual: set.Mappings[index], expected: published[index]);
        }

        images.Extents[0] = ((Width * 2), (Height * 2));
        set.Publish(images: images);
        Assert.NotSame(actual: set.Mappings[0], expected: published[0]);
        Assert.Equal(actual: set.Mappings[0].SourceWidth, expected: (Width * 2));
        Assert.Same(actual: set.Mappings[1], expected: published[1]);
    }

    // The running images a test hands the set: extents by screen, and the screens showing a live source.
    private sealed class Images : IWorldScreenImages {
        public Dictionary<int, (int Width, int Height)> Extents { get; } = [];
        public HashSet<int> Live { get; } = [];

        public bool ShowsRow(int screen) => !Live.Contains(item: screen);
        public bool TryExtent(int screen, out int width, out int height) {
            if (Extents.TryGetValue(key: screen, value: out var extent)) {
                (width, height) = extent;

                return true;
            }

            (width, height) = (0, 0);

            return false;
        }
    }
}
