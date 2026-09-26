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
    private static string? Word(WorldSeatPointerPlace place) => place switch {
        WorldSeatPointerPlace.NoView => "no-view",
        WorldSeatPointerPlace.OutsideViewport => "outside-viewport",
        _ => null,
    };

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
                            height: 900u,
                            region: region,
                            slot: 2,
                            width: 1600u
                        );
                    }

                    var view = viewports.Seat(slot: 2);

                    for (var x = -100f; (x <= 2000f); x += 137.5f) {
                        for (var y = -50f; (y <= 1300f); y += 91.25f) {
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

        // Every verdict is exercised, so the agreement is not vacuous.
        Assert.True(condition: (checkedInside > 0));
        Assert.True(condition: (checkedOutside > 0));
        Assert.True(condition: (checkedNoView > 0));
    }
}
