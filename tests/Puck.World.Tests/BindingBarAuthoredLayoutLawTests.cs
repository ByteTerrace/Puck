using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A bar's shape is the document's: a bank places its plates at authored pitches and hangs from its edge by
/// its own extent; a control it does not place is not shown on it. The writer then only scales pitches to the
/// region.</summary>
public sealed class BindingBarAuthoredLayoutLawTests {
    [Fact]
    public void APlacedControlRidesItsAuthoredPitchesAndBadge() {
        var plates = new Dictionary<string, BindingPlatePlacement>(comparer: StringComparer.Ordinal) {
            [InputSources.Gamepad.DpadUp] = new BindingPlatePlacement(Position: new Vector2(x: -5f, y: 1f), Badge: new Vector2(x: 0f, y: 1f)),
        };
        var slot = ComposeOne(plates: plates, source: InputSources.Gamepad.DpadUp);

        Assert.True(condition: slot.Visible);
        Assert.Equal(expected: (-5f, 1f), actual: (slot.PitchX, slot.PitchY));
        Assert.Equal(expected: (0f, 1f), actual: (slot.BadgeX, slot.BadgeY));
    }

    [Fact]
    public void AControlTheBankDoesNotPlaceIsNotShownOnIt() {
        var plates = new Dictionary<string, BindingPlatePlacement>(comparer: StringComparer.Ordinal) {
            [InputSources.Gamepad.DpadUp] = new BindingPlatePlacement(Position: Vector2.Zero, Badge: Vector2.One),
        };
        var destination = new OverlayBindingSlot[2];

        Compose(destination: destination, plates: plates, slotSet: [InputSources.Gamepad.DpadUp, InputSources.Gamepad.Guide]);

        Assert.True(condition: destination[0].Visible);
        Assert.False(condition: destination[1].Visible);
    }

    [Fact]
    public void AnUnauthoredBadgeIsTheUpRightCornerAndAnAuthoredPairIsItself() {
        Assert.Equal(expected: Vector2.One, actual: new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f).Plate.Badge);
        Assert.Equal(expected: new Vector2(x: -1f, y: 0f), actual: new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadLeft, X: 0f, Y: 0f, Badge: [-1f, 0f]).Plate.Badge);
    }

    [Fact]
    public void ABankIsItsPiecesTablesMovedByAtWithALaterPieceWinningASource() {
        var layout = new WorldBindingBarLayout(
            Banks: new Dictionary<string, WorldBindingBarBankPlacement>(comparer: StringComparer.Ordinal) {
                ["resting"] = new WorldBindingBarBankPlacement(Pieces: [
                    new WorldBindingBarPiece(Table: "cross", At: [-5f, 0f]),
                    new WorldBindingBarPiece(Table: "cross", At: [5f, 0f], Badge: [-1f, 0f]),
                    new WorldBindingBarPiece(Table: "no-such"),
                ]),
            },
            Tables: new Dictionary<string, IReadOnlyList<WorldBindingBarSlotPlacement>>(comparer: StringComparer.Ordinal) {
                ["cross"] = [new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 1f, Badge: [0f, 1f])],
            }
        );

        var plates = layout.Plates(bankId: "resting");

        Assert.Same(expected: plates, actual: layout.Plates(bankId: "resting"));
        // The second piece placed the same source last: its position and its piece-wide badge win.
        var up = Assert.Single(collection: plates);
        Assert.Equal(expected: new Vector2(x: 5f, y: 1f), actual: up.Value.Position);
        Assert.Equal(expected: new Vector2(x: -1f, y: 0f), actual: up.Value.Badge);
        Assert.Empty(collection: layout.Plates(bankId: "unplaced"));
    }

    [Fact]
    public void TheButtonSizeShrinksUntilEveryAnchorGroupFitsItsRegion() {
        // A bottom strip 16 plates wide (normalized: across −7.5..7.5, along 0) and a left column 12 tall.
        var slots = new List<OverlayBindingSlot>();

        for (var index = 0; (index < 16); index++) {
            slots.Add(item: Slot(edge: OverlayBarEdge.Bottom, inset: 0.5f, x: (index - 7.5f), y: 0f));
        }

        for (var index = 0; (index < 12); index++) {
            slots.Add(item: Slot(edge: OverlayBarEdge.Left, inset: 0.5f, x: 0f, y: (5.5f - index)));
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: slots);

        // Wide region: the strip's 16 plates need 16 × bs ≤ 1.6 (bs ≤ 0.1); the column's 12 need 12 × bs ≤ 1 → 1/12 binds.
        Assert.Equal(expected: (1f / 12f), actual: BindingBarLayout.FitButtonSize(slots: span, buttonSize: 0.1f, aspect: 1.6f), tolerance: 1e-6f);
        // Square region: the strip's 16 plates across now bind the width → 1/16.
        Assert.Equal(expected: (1f / 16f), actual: BindingBarLayout.FitButtonSize(slots: span, buttonSize: 0.1f, aspect: 1f), tolerance: 1e-6f);
        // A size that already fits is kept; an empty bar keeps the authored size.
        Assert.Equal(expected: 0.05f, actual: BindingBarLayout.FitButtonSize(slots: span, buttonSize: 0.05f, aspect: 1.6f));
        Assert.Equal(expected: 0.1f, actual: BindingBarLayout.FitButtonSize(slots: [], buttonSize: 0.1f, aspect: 1.6f));
    }

    [Fact]
    public void APlateHangsFromItsEdgeWithItsPitchZeroEdgeOnTheInsetLine() {
        // Bottom, inset 0.25 (region-height units here — the writer passes pitches × button size), 1.6 aspect: the
        // anchor is the inset line's midpoint; pitch (−3, 1) is three plates left and one up, and the pitch-0
        // plate's bottom edge sits on the line (so its center is half a plate up).
        var bottom = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: OverlayBarEdge.Bottom, inset: 0.25f);

        AssertPoint(expectedX: 0.8f, expectedY: 0.75f, actual: bottom);
        AssertPoint(expectedX: 0.5f, expectedY: 0.6f, actual: BindingBarLayout.PlateCenter(anchor: bottom, buttonSize: 0.1f, edge: OverlayBarEdge.Bottom, pitchX: -3f, pitchY: 1f));

        // Left, inset 0.04: the group is centered vertically and the pitch-0 plate's left edge sits on the line;
        // pitch (0, 2) is two plates up from center.
        var left = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: OverlayBarEdge.Left, inset: 0.04f);

        AssertPoint(expectedX: 0.04f, expectedY: 0.5f, actual: left);
        AssertPoint(expectedX: 0.09f, expectedY: 0.3f, actual: BindingBarLayout.PlateCenter(anchor: left, buttonSize: 0.1f, edge: OverlayBarEdge.Left, pitchX: 0f, pitchY: 2f));

        // Right, inset 0.04: the pitch-0 plate's right edge on the line.
        var right = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: OverlayBarEdge.Right, inset: 0.04f);

        AssertPoint(expectedX: 1.56f, expectedY: 0.5f, actual: right);
        AssertPoint(expectedX: 1.51f, expectedY: 0.5f, actual: BindingBarLayout.PlateCenter(anchor: right, buttonSize: 0.1f, edge: OverlayBarEdge.Right, pitchX: 0f, pitchY: 0f));
    }

    [Fact]
    public void AnAnchorGroupHangsByItsOwnExtentAndCentersAcrossTheEdge() {
        // One bottom group (a nested crossbar: resting at 0, a wing down to −3) and one left column, in one bar.
        OverlayBindingSlot[] slots = [
            Slot(edge: OverlayBarEdge.Bottom, inset: 0.5f, x: -2f, y: 0f),
            Slot(edge: OverlayBarEdge.Bottom, inset: 0.5f, x: 4f, y: 1f),
            Slot(edge: OverlayBarEdge.Bottom, inset: 0.5f, x: 0f, y: -3f),
            Slot(edge: OverlayBarEdge.Bottom, inset: 0.5f, x: 9f, y: -9f, visible: false),
            Slot(edge: OverlayBarEdge.Left, inset: 0.5f, x: 0f, y: 0f),
            Slot(edge: OverlayBarEdge.Left, inset: 0.5f, x: 0f, y: -2f),
        ];

        BindingBarSeatComposer.AnchorGroups(slots: slots);

        // Bottom: lowest visible plate (−3) to 0, x extent [−2, 4] centered on 1 → shifted by −1.
        Assert.Equal(expected: (-3f, 3f), actual: (slots[0].PitchX, slots[0].PitchY));
        Assert.Equal(expected: (3f, 4f), actual: (slots[1].PitchX, slots[1].PitchY));
        Assert.Equal(expected: (-1f, 0f), actual: (slots[2].PitchX, slots[2].PitchY));
        // The hidden plate rides the same shift and never set the extent.
        Assert.Equal(expected: (8f, -6f), actual: (slots[3].PitchX, slots[3].PitchY));
        // Left: leftmost to 0, y extent [−2, 0] centered on −1.
        Assert.Equal(expected: (0f, 1f), actual: (slots[4].PitchX, slots[4].PitchY));
        Assert.Equal(expected: (0f, -1f), actual: (slots[5].PitchX, slots[5].PitchY));
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
        Assert.Null(@object: WorldBindingBarLayout.Default.Banks);
    }

    private static void AssertPoint(float expectedX, float expectedY, Vector2 actual) {
        Assert.Equal(expected: expectedX, actual: actual.X, tolerance: 1e-5f);
        Assert.Equal(expected: expectedY, actual: actual.Y, tolerance: 1e-5f);
    }
    private static OverlayBindingSlot Slot(OverlayBarEdge edge, float inset, float x, float y, bool visible = true) => new(
        Alpha: 1f,
        AnchorEdge: edge,
        AnchorInset: inset,
        BadgeGlyph0: 0,
        BadgeGlyph1: 0,
        BankOrder: 0,
        IconGlyph0: 0,
        IconGlyph1: 0,
        PitchX: x,
        PitchY: y,
        Pressed: false,
        Visible: visible
    );
    private static OverlayBindingSlot ComposeOne(string source, IReadOnlyDictionary<string, BindingPlatePlacement> plates) {
        var destination = new OverlayBindingSlot[1];

        Compose(destination: destination, plates: plates, slotSet: [source]);

        return destination[0];
    }
    private static void Compose(string[] slotSet, IReadOnlyDictionary<string, BindingPlatePlacement> plates, OverlayBindingSlot[] destination) =>
        BindingBarSeatComposer.ComposeBank(
            anchorEdge: OverlayBarEdge.Bottom,
            anchorInset: 0f,
            bankAlpha: 1f,
            bankOrder: 0,
            destination: destination,
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
