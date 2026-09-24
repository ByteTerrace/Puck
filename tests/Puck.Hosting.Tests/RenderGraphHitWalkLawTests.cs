using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;

namespace Puck.Hosting.Tests;

/// <summary>Laws for following a hit through nested render-graph instances: a pick through a screen showing another
/// instance continues along that instance's camera and reaches the nested world's surface, up to the graph's nesting
/// depth, and a pick stops by name where the showing instance does not read what it shows or the producer has no
/// camera.</summary>
public sealed class RenderGraphHitWalkLawTests {
    private const int Main = 0;
    private const int Room = 1;

    // Both worlds stand a 256 by 256 screen two units wide in the plane z = 0, facing +z, seen by a camera four units
    // back on +z with a 90 degree field of view.
    private static readonly CameraSnapshot Camera = CameraSnapshot.LookAt(
        fieldOfViewRadians: (MathF.PI / 2f),
        position: new Vector3(
            x: 0f,
            y: 0f,
            z: 4f
        ),
        target: Vector3.Zero,
        viewportHeight: 256,
        viewportWidth: 256
    );

    private static SourceMapping Screen(SourceHandle source) => new(
        Crop: SourcePixelRect.Whole(
            height: 256,
            width: 256
        ),
        Placement: new SourcePlacement.Surface(
            HalfHeight: 1f,
            HalfWidth: 1f,
            Origin: Vector3.Zero,
            Right: Vector3.UnitX,
            Up: Vector3.UnitY
        ),
        Source: source,
        SourceHeight: 256,
        SourceWidth: 256
    );
    private static RenderGraphInstanceSet Set(params RenderGraphInstance[] instances) {
        Assert.True(
            condition: RenderGraphInstanceSet.TryCreate(
                instances: instances,
                refusal: out var refusal,
                set: out var set
            ),
            userMessage: refusal?.Message
        );

        return set;
    }
    private static RenderGraphInstance Instance(string name, params RenderGraphRead[] reads) => new(
        Name: name,
        Passes: 1,
        Reads: reads,
        Refresh: RenderGraphRefresh.EveryFrame
    );
    // The main view's ray through a point of its own image, in [0, 1] across and down.
    private static SourceRay Ray(double x, double y) => SourceRay.Through(
        camera: Camera,
        image: new FixedVector2(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y)
        )
    );

    [Fact]
    public void APickThroughANestedInstanceReachesTheNestedWorldsSurface() {
        var set = Set(
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "room")
            ),
            Instance(name: "room")
        );
        var scene = new Scene(placements: [
            [Screen(source: SourceHandle.Instance(name: "room"))],
            [Screen(source: SourceHandle.Producer(id: "desktop"))],
        ]);
        // A screen fills the middle quarter of each camera's image, so a point e from the image's centre lands 4e from
        // the screen's centre, and after the second hop 16e from the centre of the room's screen.
        var path = RenderGraphHitWalk.Walk(
            instance: Main,
            maxDepth: set.NestingDepth,
            ray: Ray(
                x: (0.5 + (0.1 / 16)),
                y: (0.5 + (0.05 / 16))
            ),
            scene: scene,
            set: set
        );

        Assert.Equal(
            expected: RenderGraphHitEnd.Producer,
            actual: path.End
        );
        Assert.Equal(
            expected: Room,
            actual: path.Instance
        );
        Assert.Equal(
            expected: [Main, Room],
            actual: path.Steps.Select(selector: static step => step.Instance)
        );
        Assert.Equal(
            expected: SourceHandle.Producer(id: "desktop"),
            actual: path.Steps[^1].Mapping.Source
        );
        // (0.5 + 0.1)·256 and (0.5 + 0.05)·256.
        Assert.Equal(
            expected: (153L, 140L),
            actual: (path.Steps[^1].Hit.PixelX, path.Steps[^1].Hit.PixelY)
        );
        Assert.Equal(
            expected: path.Steps,
            actual: RenderGraphHitWalk.Walk(
                instance: Main,
                maxDepth: set.NestingDepth,
                ray: Ray(
                    x: (0.5 + (0.1 / 16)),
                    y: (0.5 + (0.05 / 16))
                ),
                scene: scene,
                set: set
            ).Steps
        );
    }
    [Fact]
    public void ANestedWorldWithNothingUnderThePickEndsOnThatWorld() {
        var set = Set(
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "room")
            ),
            Instance(name: "room")
        );
        var path = RenderGraphHitWalk.Walk(
            instance: Main,
            maxDepth: set.NestingDepth,
            ray: Ray(
                x: 0.49,
                y: 0.51
            ),
            scene: new Scene(placements: [
                [Screen(source: SourceHandle.Instance(name: "room"))],
                [],
            ]),
            set: set
        );

        Assert.Equal(
            expected: (RenderGraphHitEnd.World, Room, 1),
            actual: (path.End, path.Instance, path.Steps.Count)
        );
    }
    [Fact]
    public void TheWalkStopsAtTheDepthLimit() {
        var set = Set(
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "room")
            ),
            Instance(name: "room")
        );
        var path = RenderGraphHitWalk.Walk(
            instance: Main,
            maxDepth: 0,
            ray: Ray(
                x: 0.51,
                y: 0.51
            ),
            scene: new Scene(placements: [
                [Screen(source: SourceHandle.Instance(name: "room"))],
                [Screen(source: SourceHandle.Producer(id: "desktop"))],
            ]),
            set: set
        );

        Assert.Equal(
            expected: (RenderGraphHitEnd.DepthLimit, Main, 1),
            actual: (path.End, path.Instance, path.Steps.Count)
        );
    }
    [Fact]
    public void ThePickStopsAtAnImageTheShowingInstanceDoesNotReadOrThatHasNoCamera() {
        var unread = Set(
            Instance(name: "main"),
            Instance(name: "room")
        );
        var shown = new Scene(placements: [
            [Screen(source: SourceHandle.Instance(name: "room"))],
            [],
        ]);

        Assert.Equal(
            expected: RenderGraphHitEnd.Unread,
            actual: RenderGraphHitWalk.Walk(
                instance: Main,
                maxDepth: 4,
                ray: Ray(
                    x: 0.51,
                    y: 0.51
                ),
                scene: shown,
                set: unread
            ).End
        );

        var read = Set(
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "room")
            ),
            Instance(name: "room")
        );

        Assert.Equal(
            expected: RenderGraphHitEnd.NoCamera,
            actual: RenderGraphHitWalk.Walk(
                instance: Main,
                maxDepth: 4,
                ray: Ray(
                    x: 0.51,
                    y: 0.51
                ),
                scene: new Scene(
                    camera: false,
                    placements: [
                        [Screen(source: SourceHandle.Instance(name: "room"))],
                        [],
                    ]
                ),
                set: read
            ).End
        );
    }
    [Fact]
    public void APaneShowingAnInstanceContinuesIntoItsWorld() {
        var set = Set(
            Instance(name: "main"),
            Instance(name: "room")
        );
        var pane = SourceMapping.WholePane(
            height: 256,
            region: new NormalizedRect(
                Height: 0.5f,
                Width: 0.5f,
                X: 0.5f,
                Y: 0.5f
            ),
            source: SourceHandle.Instance(name: "room"),
            width: 256
        );
        // The pane fills the display's lower right quarter, so the display point 768 + 768·0.55 across is the room
        // camera's image point 0.55 across, which lands 4·0.05 right of the centre of the room's screen: 0.7·256.
        var path = RenderGraphHitWalk.WalkDisplay(
            displayHeight: 1080,
            displayWidth: 1536,
            maxDepth: 1,
            panes: [pane],
            point: new FixedVector2(
                X: FixedQ4816.FromDouble(value: (768 + (768 * 0.55))),
                Y: FixedQ4816.FromDouble(value: ((540 + (540 * 0.5)) + 1))
            ),
            scene: new Scene(placements: [
                [],
                [Screen(source: SourceHandle.Producer(id: "desktop"))],
            ]),
            set: set
        );

        Assert.Equal(
            expected: (RenderGraphHitEnd.Producer, Room),
            actual: (path.End, path.Instance)
        );
        Assert.Equal(
            expected: [-1, Room],
            actual: path.Steps.Select(selector: static step => step.Instance)
        );
        Assert.Equal(
            expected: 179L,
            actual: path.Steps[^1].Hit.PixelX
        );
    }
    [Fact]
    public void TheNestingDepthCountsTheLongestChainOfSameFrameReads() {
        Assert.Equal(
            expected: 2,
            actual: Set(
                Instance(
                    name: "main",
                    reads: new RenderGraphRead(Producer: "tv")
                ),
                Instance(
                    name: "tv",
                    reads: new RenderGraphRead(Producer: "inner")
                ),
                Instance(name: "inner"),
                Instance(
                    name: "pane",
                    reads: new RenderGraphRead(Producer: "inner")
                )
            ).NestingDepth
        );
        Assert.Equal(
            expected: 0,
            actual: Set(Instance(
                name: "mirror",
                reads: new RenderGraphRead(Producer: "mirror")
            )).NestingDepth
        );
        Assert.Equal(
            expected: 0,
            actual: Set(
                Instance(
                    name: "left",
                    reads: new RenderGraphRead(
                        PreviousFrame: true,
                        Producer: "right"
                    )
                ),
                Instance(
                    name: "right",
                    reads: new RenderGraphRead(
                        PreviousFrame: true,
                        Producer: "left"
                    )
                )
            ).NestingDepth
        );
    }

    private sealed class Scene(IReadOnlyList<SourceMapping>[] placements, bool camera = true) : IRenderGraphHitScene {
        public IReadOnlyList<SourceMapping> Placements(int instance) => placements[instance];
        public bool TryCamera(int instance, out CameraSnapshot snapshot) {
            snapshot = Camera;

            return camera;
        }
    }
}
