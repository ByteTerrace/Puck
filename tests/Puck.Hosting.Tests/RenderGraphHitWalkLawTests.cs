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
            [Screen(source: SourceHandle.Producer(name: "desktop"))],
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
            expected: SourceHandle.Producer(name: "desktop"),
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
    // A ray that meets no placement ends on the nested world, at the surface the scene finds along it when the scene
    // answers for that world, and with no surface when it does not.
    [Fact]
    public void ANestedWorldWithNothingUnderThePickEndsOnThatWorldsSurface() {
        var set = Set(
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "room")
            ),
            Instance(name: "room")
        );
        var surface = new FixedVector3(
            X: FixedQ4816.FromDouble(value: 0.25),
            Y: FixedQ4816.FromDouble(value: -0.5),
            Z: FixedQ4816.FromInteger(value: -3)
        );

        RenderGraphHitPath Pick(FixedVector3? found) => RenderGraphHitWalk.Walk(
            instance: Main,
            maxDepth: set.NestingDepth,
            ray: Ray(
                x: 0.49,
                y: 0.51
            ),
            scene: new Scene(
                placements: [
                    [Screen(source: SourceHandle.Instance(name: "room"))],
                    [],
                ],
                surface: found
            ),
            set: set
        );

        var path = Pick(found: surface);

        Assert.Equal(
            expected: (RenderGraphHitEnd.World, Room, 1),
            actual: (path.End, path.Instance, path.Steps.Count)
        );
        Assert.Equal(expected: surface, actual: path.Surface);
        Assert.Null(@object: Pick(found: null).Surface);
    }
    // A continued ray starts on the producer camera's near plane: the room's screen, four units ahead of its camera, is
    // met from a near plane three units ahead and passed from one five units ahead, so the walk ends on the room's world.
    [Fact]
    public void AContinuedRayStartsOnTheProducerCamerasNearPlane() {
        var set = Set(
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "room")
            ),
            Instance(name: "room")
        );

        RenderGraphHitPath Pick(float near) => RenderGraphHitWalk.Walk(
            instance: Main,
            maxDepth: set.NestingDepth,
            ray: Ray(
                x: 0.51,
                y: 0.51
            ),
            scene: new Scene(
                near: near,
                placements: [
                    [Screen(source: SourceHandle.Instance(name: "room"))],
                    [Screen(source: SourceHandle.Producer(name: "desktop"))],
                ]
            ),
            set: set
        );

        var met = Pick(near: 3f);
        var passed = Pick(near: 5f);

        Assert.Equal(
            expected: (RenderGraphHitEnd.Producer, Room, 2),
            actual: (met.End, met.Instance, met.Steps.Count)
        );
        Assert.Equal(
            expected: (RenderGraphHitEnd.World, Room, 1),
            actual: (passed.End, passed.Instance, passed.Steps.Count)
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
                [Screen(source: SourceHandle.Producer(name: "desktop"))],
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
            display: null,
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
                [Screen(source: SourceHandle.Producer(name: "desktop"))],
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
    // THE LAW: the nesting depth is declared, never read off the reads. A set of three chained instances, one that reads
    // itself and two that read each other's previous frame all nest the depth they declare, the default when they
    // declare none; a depth below zero or past the cap is refused by name before anything else is checked.
    [Fact]
    public void TheNestingDepthIsDeclaredAndRefusedPastItsCap() {
        RenderGraphInstance[] chained = [
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "tv")
            ),
            Instance(
                name: "tv",
                reads: new RenderGraphRead(Producer: "inner")
            ),
            Instance(name: "inner"),
        ];
        RenderGraphInstance[] mirror = [Instance(
            name: "mirror",
            reads: new RenderGraphRead(Producer: "mirror")
        )];

        Assert.Equal(
            expected: RenderGraphInstanceSet.DefaultNestingDepth,
            actual: Set(chained).NestingDepth
        );
        Assert.Equal(
            expected: RenderGraphInstanceSet.DefaultNestingDepth,
            actual: Set(mirror).NestingDepth
        );

        foreach (var depth in ((int[])[0, 1, RenderGraphInstanceSet.MaxNestingDepth])) {
            Assert.True(
                condition: RenderGraphInstanceSet.TryCreate(
                    instances: chained,
                    nestingDepth: depth,
                    refusal: out var accepted,
                    set: out var declared
                ),
                userMessage: accepted?.Message
            );
            Assert.Equal(expected: depth, actual: declared.NestingDepth);
        }

        foreach (var depth in ((int[])[-1, (RenderGraphInstanceSet.MaxNestingDepth + 1)])) {
            Assert.False(condition: RenderGraphInstanceSet.TryCreate(
                instances: chained,
                nestingDepth: depth,
                refusal: out var refusal,
                set: out _
            ));
            Assert.Equal(expected: RenderGraphInstanceRefusalCode.NestingDepthInvalid, actual: refusal.Code);
            Assert.Contains(expectedSubstring: $"nest {depth} deep", actualString: refusal.Message);
        }
    }
    // THE LAW: a hit walks through two levels of nesting, each world tested against its own screens. The display's pane
    // shows the main view; the main world's screen shows the portal's view, whose world stands its own screen a unit to
    // the right of where the main world's stands, showing the inner view, whose world shows a desktop. A walk under a
    // limit of two reaches the desktop two levels deep, the second hop landing on the portal world's own screen, which
    // the main world's mapping, a unit to its left, could not have met; the red leg, the same walk under a limit of one,
    // stops at the portal world's screen by its depth limit; the pane is no level, so even a limit of zero enters the
    // main world and stops only at its first screen.
    [Fact]
    public void AHitWalksThroughTwoNestedLevelsEachWorldAgainstItsOwnScreens() {
        RenderGraphInstance[] instances = [
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "portal")
            ),
            Instance(
                name: "portal",
                reads: new RenderGraphRead(Producer: "inner")
            ),
            Instance(name: "inner"),
        ];
        var shifted = (Screen(source: SourceHandle.Instance(name: "inner")) with {
            Placement = new SourcePlacement.Surface(
                HalfHeight: 1f,
                HalfWidth: 1f,
                Origin: Vector3.UnitX,
                Right: Vector3.UnitX,
                Up: Vector3.UnitY
            ),
        });
        var scene = new Scene(placements: [
            [Screen(source: SourceHandle.Instance(name: "portal"))],
            [shifted],
            [Screen(source: SourceHandle.Producer(name: "desktop"))],
        ]);
        var pane = SourceMapping.WholePane(
            height: 256,
            region: new NormalizedRect(
                Height: 1f,
                Width: 1f,
                X: 0f,
                Y: 0f
            ),
            source: SourceHandle.Instance(name: "main"),
            width: 256
        );

        // The pane fills a 256 square display, so the display point 0.5 + 0.6/16 across is the main camera's image
        // point there, which lands four times as far from the centre of the main screen: the portal's image 0.65
        // across. The portal camera's ray through it meets z = 0 at x = 1.2, which the main world's screen, spanning
        // -1 to 1, misses, and the portal world's own, spanning 0 to 2, meets 0.6 across: the inner image's point,
        // which lands on the inner world's screen 0.9 across, the desktop.
        RenderGraphHitPath Walk(int limit) {
            Assert.True(
                condition: RenderGraphInstanceSet.TryCreate(
                    instances: instances,
                    nestingDepth: limit,
                    refusal: out var refusal,
                    set: out var set
                ),
                userMessage: refusal?.Message
            );

            return RenderGraphHitWalk.WalkDisplay(
                display: null,
                displayHeight: 256,
                displayWidth: 256,
                maxDepth: set.NestingDepth,
                panes: [pane],
                point: new FixedVector2(
                    X: FixedQ4816.FromDouble(value: (256 * (0.5 + (0.6 / 16)))),
                    Y: FixedQ4816.FromDouble(value: ((256 * 0.5) + 0.5))
                ),
                scene: scene,
                set: set
            );
        }

        var deep = Walk(limit: 2);

        Assert.Equal(
            expected: (RenderGraphHitEnd.Producer, 2),
            actual: (deep.End, deep.Instance)
        );
        Assert.Equal(
            expected: [-1, 0, 1, 2],
            actual: deep.Steps.Select(selector: static step => step.Instance)
        );
        Assert.Equal(
            expected: SourceHandle.Instance(name: "inner"),
            actual: deep.Steps[2].Mapping.Source
        );
        Assert.Equal(
            expected: SourceHandle.Producer(name: "desktop"),
            actual: deep.Steps[^1].Mapping.Source
        );

        var shallow = Walk(limit: 1);

        Assert.Equal(
            expected: (RenderGraphHitEnd.DepthLimit, 1, 3),
            actual: (shallow.End, shallow.Instance, shallow.Steps.Count)
        );

        var paneOnly = Walk(limit: 0);

        Assert.Equal(
            expected: (RenderGraphHitEnd.DepthLimit, 0, 2),
            actual: (paneOnly.End, paneOnly.Instance, paneOnly.Steps.Count)
        );
    }

    private sealed class Scene(IReadOnlyList<SourceMapping>[] placements, bool camera = true, FixedVector3? surface = null, float near = 0f) : IRenderGraphHitScene {
        public IReadOnlyList<SourceMapping> Placements(int instance) => placements[instance];
        public bool TryCamera(int instance, out CameraSnapshot snapshot) {
            snapshot = (Camera with { Near = near });

            return camera;
        }
        public bool TrySurface(int instance, SourceRay ray, out FixedVector3 point) {
            point = surface.GetValueOrDefault();

            return surface.HasValue;
        }
    }
}
