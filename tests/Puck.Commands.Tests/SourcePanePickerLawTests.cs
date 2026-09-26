using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Maths;
using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Laws for the presentation destination's CPU picker (<see cref="SourcePanePicker"/>) and the one topmost rule
/// it and the hit walk share (<see cref="SourcePanes.Topmost"/>): of overlapping panes the last in drawing order whose
/// face holds the point wins, a point off every face picks nothing, a topmost face's letterbox bar covers the panes
/// beneath rather than letting the point through, and a world-surface placement is never picked by a display
/// point.</summary>
public sealed class SourcePanePickerLawTests {
    private const int DisplayHeight = 600;
    private const int DisplayWidth = 800;

    private static readonly SourceMapping Back = SourceMapping.WholePane(
        height: 100,
        region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f),
        source: SourceHandle.Producer(name: "back"),
        width: 100
    );
    private static readonly SourceMapping Front = SourceMapping.WholePane(
        height: 100,
        region: new NormalizedRect(Height: 0.5f, Width: 0.5f, X: 0.25f, Y: 0.25f),
        source: SourceHandle.Producer(name: "front"),
        width: 100
    );

    private static SourcePanePicker Picker(params SourceMapping[] panes) {
        var picker = new SourcePanePicker();

        picker.Publish(
            displayHeight: DisplayHeight,
            displayWidth: DisplayWidth,
            panes: panes
        );

        return picker;
    }
    private static (string Source, long X, long Y) Picked(SourcePanePicker picker, float x, float y) {
        Assert.True(condition: picker.TryPick(
            pick: out var pick,
            point: new Vector2(x: x, y: y)
        ));

        return (pick.Source.Name, pick.Hit.PixelX, pick.Hit.PixelY);
    }

    [Fact]
    public void OfOverlappingPanesTheLastInDrawingOrderWhoseFaceHoldsThePointIsPicked() {
        var frontOnTop = Picker(Back, Front);

        // The overlap's centre is the front pane's centre; outside the front pane the back pane shows.
        Assert.Equal(actual: Picked(picker: frontOnTop, x: 400f, y: 300f), expected: ("front", 50L, 50L));
        Assert.Equal(actual: Picked(picker: frontOnTop, x: 100f, y: 100f), expected: ("back", 12L, 16L));

        // Drawn the other way round, the back pane covers the front one everywhere.
        Assert.Equal(actual: Picked(picker: Picker(Front, Back), x: 400f, y: 300f), expected: ("back", 50L, 50L));
    }
    [Fact]
    public void APointOffEveryFacePicksNothing() {
        Assert.False(condition: Picker().TryPick(
            pick: out _,
            point: new Vector2(x: 400f, y: 300f)
        ));
        Assert.False(condition: Picker(Front).TryPick(
            pick: out _,
            point: new Vector2(x: 100f, y: 100f)
        ));
    }
    /// <summary>A square source fitted inside the front pane's 400x300 face shows at 300x300, centred, with bars 50
    /// pixels wide at each side; a point on a bar is the front pane's, off its source, so nothing beneath is
    /// picked.</summary>
    [Fact]
    public void ATopmostFacesLetterboxBarCoversThePanesBeneath() {
        var fitted = (Front with { Fit = SourceFit.Contain });
        var point = new FixedVector2(
            X: FixedQ4816.FromInteger(value: 220),
            Y: FixedQ4816.FromInteger(value: 300)
        );

        Assert.Equal(
            actual: SourcePanes.Topmost(
                displayHeight: DisplayHeight,
                displayWidth: DisplayWidth,
                hit: out var hit,
                panes: [Back, fitted],
                point: point
            ),
            expected: 1
        );
        Assert.Equal(actual: hit.Outcome, expected: SourceHitOutcome.Letterbox);
        Assert.False(condition: Picker(Back, fitted).TryPick(
            pick: out _,
            point: new Vector2(x: 220f, y: 300f)
        ));
    }
    [Fact]
    public void AWorldSurfacePlacementIsNeverPickedByADisplayPoint() {
        var surface = (Front with {
            Placement = new SourcePlacement.Surface(
                HalfHeight: 1f,
                HalfWidth: 1f,
                Origin: Vector3.Zero,
                Right: Vector3.UnitX,
                Up: Vector3.UnitY
            ),
        });

        Assert.Equal(actual: Picked(picker: Picker(Back, surface), x: 400f, y: 300f), expected: ("back", 50L, 50L));
    }
}
