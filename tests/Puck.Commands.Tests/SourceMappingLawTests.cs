using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Maths;
using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Laws for the published source mapping: a screen at an arbitrary pose and a pane at an arbitrary rectangle map
/// known points to known source pixels through every layout, fit, crop and warp; a warp with no inverse refuses as an
/// input path; passthrough needs a local user's source; and the same mapping and input give a bit-identical hit on
/// every evaluation and thread.</summary>
public sealed class SourceMappingLawTests {
    private const int SourceHeight = 240;
    private const int SourceWidth = 320;

    private static readonly SourceHandle Emulator = SourceHandle.Producer(name: "emulator");
    // A face frame with no axis aligned to the world: right (2, 2, 1)/3 and up (-1, 2, -2)/3 are unit and orthogonal.
    private static readonly SourcePlacement.Surface TiltedSurface = new(
        HalfHeight: 1.5f,
        HalfWidth: 2f,
        Origin: new Vector3(
            x: 10f,
            y: 2f,
            z: -5f
        ),
        Right: (new Vector3(
            x: 2f,
            y: 2f,
            z: 1f
        ) / 3f),
        Up: (new Vector3(
            x: -1f,
            y: 2f,
            z: -2f
        ) / 3f)
    );

    private static SourceMapping Surface(SourceUvLayout layout = SourceUvLayout.Identity, SourceFit fit = SourceFit.Stretch, SourcePixelRect? crop = null, SourceWarp? warp = null, SourceDestination destination = SourceDestination.Presentation) => new(
        Crop: (crop ?? SourcePixelRect.Whole(
            height: SourceHeight,
            width: SourceWidth
        )),
        Destination: destination,
        Fit: fit,
        Layout: layout,
        Placement: TiltedSurface,
        Source: Emulator,
        SourceHeight: SourceHeight,
        SourceWidth: SourceWidth,
        Warp: warp
    );
    // A ray from a point two units in front of the face point (u, v), straight back at it, in doubles.
    private static SourceRay RayAt(SourcePlacement.Surface surface, double u, double v) {
        var normal = Vector3.Cross(
            vector1: surface.Right,
            vector2: surface.Up
        );
        var point = ((surface.Origin + (surface.Right * ((float)(((2.0 * u) - 1.0) * surface.HalfWidth)))) - (surface.Up * ((float)(((2.0 * v) - 1.0) * surface.HalfHeight))));

        return new SourceRay(
            Direction: FixedVector3.FromVector3(value: -normal),
            Origin: FixedVector3.FromVector3(value: (point + (normal * 2f)))
        );
    }
    private static FixedVector2 Point(double x, double y) => new(
        X: FixedQ4816.FromDouble(value: x),
        Y: FixedQ4816.FromDouble(value: y)
    );
    private static void AssertPixel(SourceHit hit, long x, long y) {
        Assert.Equal(
            expected: SourceHitOutcome.OnSource,
            actual: hit.Outcome
        );
        Assert.Equal(
            expected: (x, y),
            actual: (hit.PixelX, hit.PixelY)
        );
    }

    [Fact]
    public void ASurfaceAtAnArbitraryPoseMapsPixelCentresToTheirPixels() {
        var mapping = Surface();

        foreach (var (column, row) in new (int, int)[] { (0, 0), (319, 0), (0, 239), (319, 239), (160, 120), (37, 201), (250, 13) }) {
            var hit = mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: ((column + 0.5) / SourceWidth),
                v: ((row + 0.5) / SourceHeight)
            ));

            AssertPixel(
                hit: hit,
                x: column,
                y: row
            );
            Assert.InRange(
                actual: ((double)hit.Distance),
                high: 2.001,
                low: 1.999
            );
        }
    }
    [Fact]
    public void ARayOffTheFaceOrAwayFromItMapsToNoPixel() {
        var mapping = Surface();

        Assert.Equal(
            expected: SourceHitOutcome.OutsidePlacement,
            actual: mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: 1.2,
                v: 0.5
            )).Outcome
        );

        var toward = RayAt(
            surface: TiltedSurface,
            u: 0.5,
            v: 0.5
        );

        Assert.Equal(
            expected: SourceHitOutcome.NoIntersection,
            actual: mapping.MapRay(ray: toward with { Direction = -toward.Direction }).Outcome
        );
    }
    [Fact]
    public void EveryLayoutTurnsTheFaceOntoItsImageCorner() {
        // A face point near the face's top-left corner, clear of every pixel edge, and the image point each layout
        // names for it.
        const double U = 0.103;
        const double V = 0.207;
        var expected = new Dictionary<SourceUvLayout, (double X, double Y)> {
            [SourceUvLayout.Identity] = (U, V),
            [SourceUvLayout.Rotate90] = (V, (1 - U)),
            [SourceUvLayout.Rotate180] = ((1 - U), (1 - V)),
            [SourceUvLayout.Rotate270] = ((1 - V), U),
            [SourceUvLayout.MirrorHorizontal] = ((1 - U), V),
            [SourceUvLayout.MirrorVertical] = (U, (1 - V)),
            [SourceUvLayout.Transpose] = (V, U),
            [SourceUvLayout.Transverse] = ((1 - V), (1 - U)),
        };

        Assert.Equal(
            expected: Enum.GetValues<SourceUvLayout>().Length,
            actual: expected.Count
        );

        foreach (var (layout, (x, y)) in expected) {
            AssertPixel(
                hit: Surface(layout: layout).MapRay(ray: RayAt(
                    surface: TiltedSurface,
                    u: U,
                    v: V
                )),
                x: ((long)Math.Floor(d: (x * SourceWidth))),
                y: ((long)Math.Floor(d: (y * SourceHeight)))
            );
        }
    }
    [Fact]
    public void ACropPlacesTheFaceInsideIt() {
        var mapping = Surface(crop: new SourcePixelRect(
            Height: 100,
            Width: 200,
            X: 100,
            Y: 50
        ));

        AssertPixel(
            hit: mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: (100.5 / 200),
                v: (50.5 / 100)
            )),
            x: 200,
            y: 100
        );
        AssertPixel(
            hit: mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: 0.001,
                v: 0.999
            )),
            x: 100,
            y: 149
        );
    }
    [Fact]
    public void APaneAtAnArbitraryRectangleLetterboxesAndMapsKnownPoints() {
        const int DisplayHeight = 1080;
        const int DisplayWidth = 1920;
        const int PaneSourceHeight = 480;
        const int PaneSourceWidth = 640;
        var region = new NormalizedRect(
            Height: 0.4f,
            Width: 0.25f,
            X: 0.3f,
            Y: 0.2f
        );
        var mapping = new SourceMapping(
            Crop: SourcePixelRect.Whole(
                height: PaneSourceHeight,
                width: PaneSourceWidth
            ),
            Fit: SourceFit.Contain,
            Placement: new SourcePlacement.Pane(Region: region),
            Source: Emulator,
            SourceHeight: PaneSourceHeight,
            SourceWidth: PaneSourceWidth
        );
        // The pane is 480 by 432 pixels, 10:9; the source is 4:3, 1.2 times wider, so it fills the pane's width and
        // leaves a bar above and below, each a twelfth of the pane's height.
        var paneLeft = (region.X * ((double)DisplayWidth));
        var paneTop = (region.Y * ((double)DisplayHeight));
        var paneWidth = (region.Width * ((double)DisplayWidth));
        var paneHeight = (region.Height * ((double)DisplayHeight));

        FixedVector2 DisplayPointOf(double column, double row) => Point(
            x: (paneLeft + (((column + 0.5) / PaneSourceWidth) * paneWidth)),
            y: (paneTop + ((((((row + 0.5) / PaneSourceHeight) - 0.5) / 1.2) + 0.5) * paneHeight))
        );

        foreach (var (column, row) in new (int, int)[] { (0, 0), (639, 479), (320, 240), (17, 400) }) {
            AssertPixel(
                hit: mapping.MapDisplayPoint(
                    displayHeight: DisplayHeight,
                    displayWidth: DisplayWidth,
                    point: DisplayPointOf(
                        column: column,
                        row: row
                    )
                ),
                x: column,
                y: row
            );
        }

        Assert.Equal(
            expected: SourceHitOutcome.Letterbox,
            actual: mapping.MapDisplayPoint(
                displayHeight: DisplayHeight,
                displayWidth: DisplayWidth,
                point: Point(
                    x: (paneLeft + (paneWidth / 2)),
                    y: (paneTop + (paneHeight * 0.02))
                )
            ).Outcome
        );
        Assert.Equal(
            expected: SourceHitOutcome.OutsidePlacement,
            actual: mapping.MapDisplayPoint(
                displayHeight: DisplayHeight,
                displayWidth: DisplayWidth,
                point: Point(
                    x: (paneLeft - 1),
                    y: (paneTop + (paneHeight / 2))
                )
            ).Outcome
        );
    }
    [Fact]
    public void CoverFillsTheFaceWithTheCropsMiddle() {
        // The face is 4:3 and the crop 1:1, so cover shows the crop's middle three quarters top to bottom.
        var mapping = Surface(
            crop: new SourcePixelRect(
                Height: 200,
                Width: 200,
                X: 0,
                Y: 0
            ),
            fit: SourceFit.Cover
        );

        AssertPixel(
            hit: mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: (100.5 / 200),
                v: ((((100.5 / 200) - 0.5) / 0.75) + 0.5)
            )),
            x: 100,
            y: 100
        );
        AssertPixel(
            hit: mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: (100.5 / 200),
                v: 0.001
            )),
            x: 100,
            y: 25
        );
    }
    [Fact]
    public void AnInsetWarpMapsItsBorderToNothingAndItsInsideOntoTheSource() {
        var mapping = Surface(warp: new SourceWarp(
            Inverse: SourceWarpInverse.Affine.Inset(border: 0.1f),
            Pass: "glass"
        ));

        Assert.Equal(
            expected: SourceHitOutcome.OutsideWarp,
            actual: mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: 0.05,
                v: 0.5
            )).Outcome
        );
        AssertPixel(
            hit: mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: (0.1 + (0.8 * (160.5 / SourceWidth))),
                v: (0.1 + (0.8 * (120.5 / SourceHeight)))
            )),
            x: 160,
            y: 120
        );
        // Face u = 0.1 + 0.8·(10.5 / 320) is image x = 10.5 / 320 once the inset is undone.
        AssertPixel(
            hit: mapping.MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: (0.1 + (0.8 * (10.5 / SourceWidth))),
                v: (0.1 + (0.8 * (120.5 / SourceHeight)))
            )),
            x: 10,
            y: 120
        );
    }
    [Fact]
    public void AWarpWithNoInverseRefusesAsAnInputPathButStillDraws() {
        var drawOnly = new SourceWarp(Pass: "ripple");

        Assert.True(condition: Surface(warp: drawOnly).TryValidate(refusal: out _));
        Assert.False(condition: Surface(warp: drawOnly).AcceptsInput);
        Assert.Equal(
            expected: SourceHitOutcome.WarpNotInvertible,
            actual: Surface(warp: drawOnly).MapRay(ray: RayAt(
                surface: TiltedSurface,
                u: 0.5,
                v: 0.5
            )).Outcome
        );
        Assert.False(condition: Surface(
            destination: SourceDestination.Simulation,
            warp: drawOnly
        ).TryValidate(refusal: out var refusal));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "'ripple', which declares no inverse, so it cannot be an input path"
        );
    }
    [Fact]
    public void PassthroughNeedsASourceTheLocalUserOpened() {
        var fromDocument = Surface(destination: SourceDestination.Passthrough);

        Assert.False(condition: fromDocument.TryValidate(refusal: out var refusal));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "exists only for a source the local user opened on their own machine"
        );
        Assert.True(condition: (fromDocument with { Opener = SourceOpener.LocalUser }).TryValidate(refusal: out _));
        Assert.False(condition: SourcePassthrough.IsPermitted(opener: SourceOpener.Document));
        Assert.True(condition: SourcePassthrough.IsPermitted(opener: SourceOpener.LocalUser));
    }
    [Fact]
    public void TheSimulationMapsOnlyASurface() {
        var pane = SourceMapping.WholePane(
            height: 100,
            region: new NormalizedRect(
                Height: 1f,
                Width: 1f,
                X: 0f,
                Y: 0f
            ),
            source: Emulator,
            width: 100
        ) with { Destination = SourceDestination.Simulation };

        Assert.False(condition: pane.TryValidate(refusal: out var refusal));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "the simulation maps only a surface placement"
        );
        Assert.True(condition: Surface(destination: SourceDestination.Simulation).TryValidate(refusal: out _));
    }
    [Fact]
    public void PassthroughCoordinatesScaleToTheWindowsClientAreaAndItsDpi() {
        var point = SourcePassthrough.ToClient(
            coordinate: Point(
                x: 160,
                y: 120
            ),
            sourceHeight: 240,
            sourceWidth: 320,
            window: new SourcePassthroughWindow(
                ClientHeight: 960,
                ClientWidth: 1280,
                DpiScale: 1.5f
            )
        );

        Assert.Equal(
            expected: new Vector2(
                x: 640f,
                y: 480f
            ),
            actual: point.Physical
        );
        Assert.Equal(
            expected: new Vector2(
                x: (640f / 1.5f),
                y: 320f
            ),
            actual: point.Logical
        );
    }
    [Fact]
    public void TheSameMappingAndRaysGiveBitIdenticalHitsOnEveryEvaluationAndThread() {
        var mapping = Surface(
            crop: new SourcePixelRect(
                Height: 180,
                Width: 300,
                X: 7,
                Y: 30
            ),
            fit: SourceFit.Contain,
            layout: SourceUvLayout.Rotate90,
            warp: new SourceWarp(
                Inverse: SourceWarpInverse.Affine.Inset(border: 0.03f),
                Pass: "glass"
            )
        );
        var rays = new SourceRay[512];
        var state = 0x2545F491u;

        for (var index = 0; (index < rays.Length); index++) {
            rays[index] = RayAt(
                surface: TiltedSurface,
                u: ((Next() * 1.2) - 0.1),
                v: ((Next() * 1.2) - 0.1)
            );
        }

        var first = rays.Select(selector: ray => mapping.MapRay(ray: ray)).ToArray();
        var parallel = new SourceHit[rays.Length];

        Parallel.For(
            body: index => parallel[index] = (mapping with { }).MapRay(ray: rays[index]),
            fromInclusive: 0,
            toExclusive: rays.Length
        );

        Assert.Equal(
            expected: first,
            actual: rays.Select(selector: ray => mapping.MapRay(ray: ray)).ToArray()
        );
        Assert.Equal(
            actual: parallel,
            expected: first
        );
        Assert.Contains(
            collection: first,
            filter: static hit => hit.IsOnSource
        );
        Assert.Contains(
            collection: first,
            filter: static hit => (hit.Outcome == SourceHitOutcome.Letterbox)
        );

        double Next() {
            state ^= (state << 13);
            state ^= (state >> 17);
            state ^= (state << 5);

            return (state / ((double)uint.MaxValue));
        }
    }
}
