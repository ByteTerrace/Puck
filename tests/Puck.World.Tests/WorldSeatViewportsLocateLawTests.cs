using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="WorldSeatViewports.Locate"/> is the one client-pixel to seat-local mapping the drawn cursor and
/// the pointer-ray capture share, and it answers exactly what the cursor feed's own mapping answered before the two
/// shared it: the client-to-frame stretch, the seat's viewport rect, and the no-view and outside-viewport verdicts.
/// </summary>
public sealed class WorldSeatViewportsLocateLawTests {
    // The cursor feed's mapping as it stood before it moved into Locate, kept verbatim as the oracle.
    private static string? Oracle(uint clientWidth, uint clientHeight, Vector2 position, in WorldSeatView view, out Vector2 framePosition, out float localX, out float localY) {
        framePosition = position;
        localX = 0f;
        localY = 0f;

        if (!view.Present) {
            return "no-view";
        }

        if (
            (clientWidth > 0) &&
            (clientHeight > 0)
        ) {
            framePosition = new Vector2(
                x: (position.X * (view.Width / ((float)clientWidth))),
                y: (position.Y * (view.Height / ((float)clientHeight)))
            );
        }

        var regionWidthPx = (view.Region.Width * view.Width);
        var regionHeightPx = (view.Region.Height * view.Height);

        if (
            (regionWidthPx < 1f) ||
            (regionHeightPx < 1f)
        ) {
            return "no-view";
        }

        localX = ((framePosition.X - (view.Region.X * view.Width)) / regionWidthPx);
        localY = ((framePosition.Y - (view.Region.Y * view.Height)) / regionHeightPx);

        if (
            (localX < 0f) ||
            (localX > 1f) ||
            (localY < 0f) ||
            (localY > 1f)
        ) {
            return "outside-viewport";
        }

        return null;
    }
    // Client positions along one axis: a coarse sweep past both window edges, plus each of the viewport's two edges in
    // client pixels exactly and a quarter pixel either side of them, where the inside/outside verdict turns.
    private static IEnumerable<float> Axis(float start, float extent, uint frame, uint client) {
        var scale = ((client > 0u)
            ? (client / ((float)frame))
            : 1f
        );

        for (var value = -100f; (value <= 2100f); value += 137.5f) {
            yield return value;
        }

        foreach (var edge in new[] { (start * frame), ((start + extent) * frame) }) {
            var clientEdge = (edge * scale);

            yield return (clientEdge - 0.25f);
            yield return clientEdge;
            yield return (clientEdge + 0.25f);
        }
    }
    private static string? Word(WorldSeatPointerPlace place) => place switch {
        WorldSeatPointerPlace.NoView => "no-view",
        WorldSeatPointerPlace.OutsideViewport => "outside-viewport",
        _ => null,
    };

    [InlineData(0u, 0u, 800f, 450f)]
    [InlineData(800u, 450u, 400f, 225f)]
    [Theory]
    public void AViewportsEdgesAreInsideAndAQuarterPixelPastThemIsNot(uint clientWidth, uint clientHeight, float leftEdge, float bottomEdge) {
        // The right half of a 1600 by 900 frame, shown at the frame's own size or stretched over a client half as big.
        var viewports = new WorldSeatViewports();

        viewports.PublishClientExtent(
            height: clientHeight,
            width: clientWidth
        );
        viewports.Publish(
            camera: default(CameraSnapshot),
            height: 900u,
            region: new NormalizedRect(
                Height: 1f,
                Width: 0.5f,
                X: 0.5f,
                Y: 0f
            ),
            slot: 0,
            width: 1600u
        );

        var view = viewports.Seat(slot: 0);
        var rightEdge = (leftEdge * 2f);

        WorldSeatPointerPlace Place(float x, float y) => viewports.Locate(
            framePosition: out _,
            local: out _,
            position: new Vector2(
                x: x,
                y: y
            ),
            view: in view
        );

        Assert.Equal(
            actual: Place(
                x: leftEdge,
                y: 0f
            ),
            expected: WorldSeatPointerPlace.Inside
        );
        Assert.Equal(
            actual: Place(
                x: rightEdge,
                y: (bottomEdge * 2f)
            ),
            expected: WorldSeatPointerPlace.Inside
        );
        Assert.Equal(
            actual: Place(
                x: (leftEdge - 0.25f),
                y: 0f
            ),
            expected: WorldSeatPointerPlace.OutsideViewport
        );
        Assert.Equal(
            actual: Place(
                x: (rightEdge + 0.25f),
                y: 0f
            ),
            expected: WorldSeatPointerPlace.OutsideViewport
        );
        Assert.Equal(
            actual: Place(
                x: leftEdge,
                y: ((bottomEdge * 2f) + 0.25f)
            ),
            expected: WorldSeatPointerPlace.OutsideViewport
        );
        _ = viewports.Locate(
            framePosition: out var frame,
            local: out var local,
            position: new Vector2(
                x: rightEdge,
                y: bottomEdge
            ),
            view: in view
        );
        Assert.Equal(
            actual: (frame, local),
            expected: (new Vector2(
                x: 1600f,
                y: 450f
            ), new Vector2(
                x: 1f,
                y: 0.5f
            ))
        );
    }
    [Fact]
    public void LocateAnswersWhatTheCursorFeedsOwnMappingAnswered() {
        var regions = new[] {
            new NormalizedRect(
                Height: 1f,
                Width: 1f,
                X: 0f,
                Y: 0f
            ),
            new NormalizedRect(
                Height: 1f,
                Width: 0.5f,
                X: 0.5f,
                Y: 0f
            ),
            new NormalizedRect(
                Height: 0.5f,
                Width: 0.5f,
                X: 0f,
                Y: 0.5f
            ),
            new NormalizedRect(
                Height: 0.0001f,
                Width: 0.5f,
                X: 0.25f,
                Y: 0.25f
            ),
        };
        var extents = new (uint Width, uint Height)[] { (0u, 0u), (1600u, 900u), (800u, 450u), (1920u, 1200u) };
        var checkedInside = 0;
        var checkedOutside = 0;
        var checkedNoView = 0;

        var frames = new (uint Width, uint Height)[] { (1600u, 900u), (1280u, 1024u) };

        foreach (var (frameWidth, frameHeight) in frames) {
            foreach (var (clientWidth, clientHeight) in extents) {
                foreach (var region in regions) {
                    foreach (var present in new[] { true, false }) {
                        var viewports = new WorldSeatViewports();

                        viewports.PublishClientExtent(
                            height: clientHeight,
                            width: clientWidth
                        );

                        if (present) {
                            viewports.Publish(
                                camera: default(CameraSnapshot),
                                height: frameHeight,
                                region: region,
                                slot: 2,
                                width: frameWidth
                            );
                        }

                        var view = viewports.Seat(slot: 2);

                        foreach (var x in Axis(start: region.X, extent: region.Width, frame: frameWidth, client: clientWidth)) {
                            foreach (var y in Axis(start: region.Y, extent: region.Height, frame: frameHeight, client: clientHeight)) {
                                var position = new Vector2(
                                    x: x,
                                    y: y
                                );
                                var expected = Oracle(
                                    clientHeight: clientHeight,
                                    clientWidth: clientWidth,
                                    framePosition: out var expectedFrame,
                                    localX: out var expectedX,
                                    localY: out var expectedY,
                                    position: position,
                                    view: in view
                                );
                                var place = viewports.Locate(
                                    framePosition: out var frame,
                                    local: out var local,
                                    position: position,
                                    view: in view
                                );

                                Assert.Equal(
                                    actual: Word(place: place),
                                    expected: expected
                                );
                                Assert.Equal(
                                    actual: frame,
                                    expected: expectedFrame
                                );
                                Assert.Equal(
                                    actual: local,
                                    expected: new Vector2(
                                        x: expectedX,
                                        y: expectedY
                                    )
                                );

                                switch (place) {
                                    case WorldSeatPointerPlace.Inside:
                                        checkedInside++;
                                        break;
                                    case WorldSeatPointerPlace.OutsideViewport:
                                        checkedOutside++;
                                        break;
                                    default:
                                        checkedNoView++;
                                        break;
                                }
                            }
                        }
                    }
                }
            }

        }

        // Every verdict is exercised, so the agreement is not vacuous.
        Assert.True(condition: (checkedInside > 0));
        Assert.True(condition: (checkedOutside > 0));
        Assert.True(condition: (checkedNoView > 0));
    }
}
