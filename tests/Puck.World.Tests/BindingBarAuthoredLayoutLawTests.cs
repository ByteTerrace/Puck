using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A bar's shape is the document's, derived once: a layout compiles to frames (anchor groups with their
/// extents) and banks whose plates are normalized into their frame; a control a bank does not place is not shown on
/// it. A tick only looks up, and the writer only scales pitches to the region.</summary>
public sealed class BindingBarAuthoredLayoutLawTests {
    [Fact]
    public void APlacedControlRidesItsPlateAndFrameAndAnUnplacedOneIsHidden() {
        var plates = new Dictionary<string, BindingPlatePlacement>(comparer: StringComparer.Ordinal) {
            [InputSources.Gamepad.DpadUp] = new BindingPlatePlacement(Position: new Vector2(x: -5f, y: 1f), Badge: new Vector2(x: 0f, y: 1f)),
        };
        var destination = new OverlayBindingSlot[2];

        Compose(destination: destination, frame: 3, plates: plates, slotSet: [InputSources.Gamepad.DpadUp, InputSources.Gamepad.Guide]);

        Assert.True(condition: destination[0].Visible);
        Assert.Equal(expected: (-5f, 1f), actual: (destination[0].PitchX, destination[0].PitchY));
        Assert.Equal(expected: (0f, 1f), actual: (destination[0].BadgeX, destination[0].BadgeY));
        Assert.Equal(expected: 3, actual: destination[0].Frame);
        Assert.False(condition: destination[1].Visible);
    }

    [Fact]
    public void AnUnauthoredBadgeIsTheUpRightCornerAndAnAuthoredPairIsItself() {
        Assert.Equal(expected: Vector2.One, actual: new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f).Plate.Badge);
        Assert.Equal(expected: new Vector2(x: -1f, y: 0f), actual: new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadLeft, X: 0f, Y: 0f, Badge: [-1f, 0f]).Plate.Badge);
    }

    [Fact]
    public void CompilingMergesPiecesWithALaterPieceWinningASourceAndNormalizesEachFrame() {
        // One bottom frame holding a resting cross (at −5 and +5) and a wing nested two in and one up, and one left
        // column frame: the bottom frame's lowest plate goes to 0 and its x extent centers; the column's leftmost
        // plate goes to 0 and its y extent centers. A piece naming no table contributes nothing.
        var layout = new WorldBindingBarLayout(
            Anchor: new WorldBindingBarAnchor(Edge: BindingBarEdge.Bottom, Inset: 0.5f),
            Banks: new Dictionary<string, WorldBindingBarBankPlacement>(comparer: StringComparer.Ordinal) {
                ["resting"] = new WorldBindingBarBankPlacement(Pieces: [
                    new WorldBindingBarPiece(Table: "cross", At: [-5f, 0f]),
                    new WorldBindingBarPiece(Table: "cross", At: [5f, 0f], Badge: [-1f, 0f]),
                    new WorldBindingBarPiece(Table: "no-such"),
                ]),
                ["lt"] = new WorldBindingBarBankPlacement(Pieces: [new WorldBindingBarPiece(Table: "cross", At: [-3f, 1f])]),
                ["side"] = new WorldBindingBarBankPlacement(
                    Anchor: new WorldBindingBarAnchor(Edge: BindingBarEdge.Left, Inset: 1f),
                    Pieces: [new WorldBindingBarPiece(Table: "column", At: [2f, 0f])]
                ),
            },
            Tables: new Dictionary<string, IReadOnlyList<WorldBindingBarSlotPlacement>>(comparer: StringComparer.Ordinal) {
                ["cross"] = [
                    new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 1f, Badge: [0f, 1f]),
                    new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadDown, X: 0f, Y: -1f),
                ],
                ["column"] = [
                    new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f),
                    new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadDown, X: 0f, Y: -3f),
                ],
            }
        );

        var compiled = layout.Compile();

        Assert.Equal(expected: 2, actual: compiled.Frames.Length);
        var bottom = compiled.Frames.Span[0];
        var left = compiled.Frames.Span[1];

        Assert.Equal(expected: BindingBarEdge.Bottom, actual: bottom.Edge);
        Assert.Equal(expected: 0.5f, actual: bottom.Inset);
        // y ran −1..2 (resting down to wing up): along 3; x ran −3..5 (the wing's cross to resting's surviving
        // cross — the second piece re-placed both sources): across 8, centered on 1.
        Assert.Equal(expected: 3f, actual: bottom.Along);
        Assert.Equal(expected: 8f, actual: bottom.Across);
        Assert.Equal(expected: BindingBarEdge.Left, actual: left.Edge);
        Assert.Equal(expected: 0f, actual: left.Along);
        Assert.Equal(expected: 3f, actual: left.Across);

        var resting = compiled.Banks["resting"];

        Assert.Equal(expected: 0, actual: resting.Frame);
        // The second cross piece placed dpadUp last: at +5 with the piece-wide badge; then shifted by the frame
        // (x centered: −1; y lifted by 1 so the lowest plate, the cross's down at −1, sits at 0).
        Assert.Equal(expected: new Vector2(x: 4f, y: 2f), actual: resting.Plates[InputSources.Gamepad.DpadUp].Position);
        Assert.Equal(expected: new Vector2(x: -1f, y: 0f), actual: resting.Plates[InputSources.Gamepad.DpadUp].Badge);
        Assert.Equal(expected: new Vector2(x: 4f, y: 0f), actual: resting.Plates[InputSources.Gamepad.DpadDown].Position);
        // The wing shares the frame and the shift.
        Assert.Equal(expected: new Vector2(x: -4f, y: 3f), actual: compiled.Banks["lt"].Plates[InputSources.Gamepad.DpadUp].Position);
        // The column: x 2 → 0 (leftmost at the inset line), y −3..0 centered on −1.5.
        var side = compiled.Banks["side"];

        Assert.Equal(expected: 1, actual: side.Frame);
        Assert.Equal(expected: new Vector2(x: 0f, y: 1.5f), actual: side.Plates[InputSources.Gamepad.DpadUp].Position);
        Assert.Equal(expected: new Vector2(x: 0f, y: -1.5f), actual: side.Plates[InputSources.Gamepad.DpadDown].Position);
        Assert.False(condition: compiled.Banks.ContainsKey(key: "unplaced"));
    }

    [Fact]
    public void TheButtonSizeShrinksUntilEveryFrameFitsTheRegion() {
        // A bottom strip 16 plates wide (along 0, across 15, inset 0.5) and a left column 12 tall (along 0, across 11).
        BindingBarFrame[] frames = [
            new BindingBarFrame(Edge: BindingBarEdge.Bottom, Inset: 0.5f, Along: 0f, Across: 15f),
            new BindingBarFrame(Edge: BindingBarEdge.Left, Inset: 0.5f, Along: 0f, Across: 11f),
        ];

        // Wide region: the strip needs 16 × bs ≤ 1.6 (bs ≤ 0.1); the column needs 12 × bs ≤ 1 → 1/12 binds.
        Assert.Equal(expected: (1f / 12f), actual: CompiledBindingBarLayout.FitButtonSize(frames: frames, buttonSize: 0.1f, aspect: 1.6f), tolerance: 1e-6f);
        // Square region: the strip's 16 plates across now bind the width → 1/16.
        Assert.Equal(expected: (1f / 16f), actual: CompiledBindingBarLayout.FitButtonSize(frames: frames, buttonSize: 0.1f, aspect: 1f), tolerance: 1e-6f);
        // A size that already fits is kept; an empty layout keeps the authored size.
        Assert.Equal(expected: 0.05f, actual: CompiledBindingBarLayout.FitButtonSize(frames: frames, buttonSize: 0.05f, aspect: 1.6f));
        Assert.Equal(expected: 0.1f, actual: CompiledBindingBarLayout.Empty.FitButtonSize(buttonSize: 0.1f, aspect: 1.6f));
    }

    [Fact]
    public void APlateHangsFromItsEdgeWithItsPitchZeroEdgeOnTheInsetLine() {
        // Bottom, inset 0.25 (region-height units here — the writer passes pitches × button size), 1.6 aspect: the
        // anchor is the inset line's midpoint; pitch (−3, 1) is three plates left and one up, and the pitch-0
        // plate's bottom edge sits on the line (so its center is half a plate up).
        var bottom = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: BindingBarEdge.Bottom, inset: 0.25f);

        AssertPoint(expectedX: 0.8f, expectedY: 0.75f, actual: bottom);
        AssertPoint(expectedX: 0.5f, expectedY: 0.6f, actual: BindingBarLayout.PlateCenter(anchor: bottom, buttonSize: 0.1f, edge: BindingBarEdge.Bottom, pitchX: -3f, pitchY: 1f));

        // Left, inset 0.04: the group is centered vertically and the pitch-0 plate's left edge sits on the line;
        // pitch (0, 2) is two plates up from center.
        var left = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: BindingBarEdge.Left, inset: 0.04f);

        AssertPoint(expectedX: 0.04f, expectedY: 0.5f, actual: left);
        AssertPoint(expectedX: 0.09f, expectedY: 0.3f, actual: BindingBarLayout.PlateCenter(anchor: left, buttonSize: 0.1f, edge: BindingBarEdge.Left, pitchX: 0f, pitchY: 2f));

        // Right, inset 0.04: the pitch-0 plate's right edge on the line.
        var right = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: BindingBarEdge.Right, inset: 0.04f);

        AssertPoint(expectedX: 1.56f, expectedY: 0.5f, actual: right);
        AssertPoint(expectedX: 1.51f, expectedY: 0.5f, actual: BindingBarLayout.PlateCenter(anchor: right, buttonSize: 0.1f, edge: BindingBarEdge.Right, pitchX: 0f, pitchY: 0f));
    }

    [Fact]
    public void TheLayoutCellSelectsANamedLayoutThenTheDefaultNameThenNothing() {
        var crossbar = new WorldBindingBarLayout(ButtonSize: 0.1f);
        var linear = new WorldBindingBarLayout(ButtonSize: 0.2f);
        var authoring = new WorldBindingBarAuthoring(
            Banks: [],
            Layout: "crossbar",
            Layouts: new Dictionary<string, WorldBindingBarLayout>(comparer: StringComparer.Ordinal) { ["crossbar"] = crossbar, ["linear"] = linear, },
            SlotSet: []
        );

        Assert.Same(expected: linear, actual: authoring.LayoutNamed(name: "linear"));
        Assert.Same(expected: crossbar, actual: authoring.LayoutNamed(name: "no-such"));
        Assert.Same(expected: crossbar, actual: authoring.LayoutNamed(name: null));
        Assert.Same(expected: WorldBindingBarLayout.Default, actual: (authoring with { Layout = null }).LayoutNamed(name: "no-such"));
        Assert.Empty(collection: WorldBindingBarLayout.Default.Compile().Banks);
    }

    private static void AssertPoint(float expectedX, float expectedY, Vector2 actual) {
        Assert.Equal(expected: expectedX, actual: actual.X, tolerance: 1e-5f);
        Assert.Equal(expected: expectedY, actual: actual.Y, tolerance: 1e-5f);
    }
    private static void Compose(string[] slotSet, IReadOnlyDictionary<string, BindingPlatePlacement> plates, int frame, OverlayBindingSlot[] destination) =>
        BindingBarSeatComposer.ComposeBank(
            bankAlpha: 1f,
            bankOrder: 0,
            destination: destination,
            frame: frame,
            hideUnbound: false,
            isCommandHeld: null,
            isPressed: null,
            plates: plates,
            resolveBadge: static _ => OverlayResolvedGlyph.None,
            resolveIcon: static _ => OverlayResolvedGlyph.None,
            slotSet: slotSet,
            text: true,
            view: new BindingPageView(
                PageId: "base",
                Group: "play",
                Label: null,
                Icon: null,
                Buttons: [],
                Modifiers: [],
                CommandChords: []
            )
        );
}
