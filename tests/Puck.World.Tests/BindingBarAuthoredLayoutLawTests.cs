using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A bar's shape is the document's: a plate sits at its authored pitch position plus its bank's offset —
/// outward per plate when the bank mirrors — and a control the layout does not place lands in the unplaced row.
/// Each anchor group then hangs from its edge by its own extent, and the writer only scales pitches to the
/// region.</summary>
public sealed class BindingBarAuthoredLayoutLawTests {
    [Fact]
    public void AnAuthoredPlacementRidesTheComposedSlotPlusItsBankOffset() {
        var slot = ComposeOne(
            bankMirror: false,
            bankOffset: new Vector2(x: 1f, y: -2f),
            placements: new Dictionary<string, Vector2>(comparer: StringComparer.Ordinal) { [InputSources.Gamepad.DpadUp] = new Vector2(x: -5f, y: 1f), },
            source: InputSources.Gamepad.DpadUp
        );

        Assert.Equal(expected: -4f, actual: slot.PitchX);
        Assert.Equal(expected: -1f, actual: slot.PitchY);
    }

    [Fact]
    public void AMirroredBankFansOutwardPerPlateAndLeavesTheAnchorLineStill() {
        var placements = new Dictionary<string, Vector2>(comparer: StringComparer.Ordinal) {
            [InputSources.Gamepad.DpadLeft] = new Vector2(x: -6f, y: 0f),
            [InputSources.Gamepad.ButtonEast] = new Vector2(x: 6f, y: 0f),
            [InputSources.Gamepad.Guide] = new Vector2(x: 0f, y: 2f),
        };

        Assert.Equal(expected: -8f, actual: ComposeOne(bankMirror: true, bankOffset: new Vector2(x: 2f, y: 1f), placements: placements, source: InputSources.Gamepad.DpadLeft).PitchX);
        Assert.Equal(expected: 8f, actual: ComposeOne(bankMirror: true, bankOffset: new Vector2(x: 2f, y: 1f), placements: placements, source: InputSources.Gamepad.ButtonEast).PitchX);
        Assert.Equal(expected: 0f, actual: ComposeOne(bankMirror: true, bankOffset: new Vector2(x: 2f, y: 1f), placements: placements, source: InputSources.Gamepad.Guide).PitchX);
        Assert.Equal(expected: 3f, actual: ComposeOne(bankMirror: true, bankOffset: new Vector2(x: 2f, y: 1f), placements: placements, source: InputSources.Gamepad.Guide).PitchY);
    }

    [Fact]
    public void UnplacedControlsFormACenteredRowInSlotSetOrder() {
        var destination = new OverlayBindingSlot[3];

        Compose(
            bankMirror: false,
            bankOffset: Vector2.Zero,
            destination: destination,
            placements: new Dictionary<string, Vector2>(comparer: StringComparer.Ordinal),
            slotSet: [InputSources.Mouse.LeftButton, InputSources.Mouse.RightButton, InputSources.Gamepad.Guide],
            unplacedRowLift: 3f,
            unplacedSlotSpacing: 2f
        );

        Assert.Equal(expected: -2f, actual: destination[0].PitchX);
        Assert.Equal(expected: 0f, actual: destination[1].PitchX);
        Assert.Equal(expected: 2f, actual: destination[2].PitchX);
        Assert.All(collection: destination, action: slot => Assert.Equal(expected: 3f, actual: slot.PitchY));
    }

    [Fact]
    public void APlateHangsFromItsEdgeWithItsPitchZeroEdgeOnTheMarginLine() {
        // Bottom, 25% in, 1.6 aspect: the anchor is the margin line's midpoint; pitch (−3, 1) is three plates left
        // and one up, and the pitch-0 plate's BOTTOM edge sits on the line (so its center is half a plate up).
        var bottom = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: OverlayBarEdge.Bottom, margin: 0.25f);

        AssertPoint(expectedX: 0.8f, expectedY: 0.75f, actual: bottom);
        AssertPoint(expectedX: 0.5f, expectedY: 0.6f, actual: BindingBarLayout.PlateCenter(anchor: bottom, buttonSize: 0.1f, edge: OverlayBarEdge.Bottom, pitchX: -3f, pitchY: 1f));

        // Left, 2.5% in: the margin is a fraction of the WIDTH (1.6 × 0.025 = 0.04), the group is centered vertically,
        // and the pitch-0 plate's LEFT edge sits on the line; pitch (0, 2) is two plates up from center.
        var left = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: OverlayBarEdge.Left, margin: 0.025f);

        AssertPoint(expectedX: 0.04f, expectedY: 0.5f, actual: left);
        AssertPoint(expectedX: 0.09f, expectedY: 0.3f, actual: BindingBarLayout.PlateCenter(anchor: left, buttonSize: 0.1f, edge: OverlayBarEdge.Left, pitchX: 0f, pitchY: 2f));

        // Right, 2.5% in: the pitch-0 plate's RIGHT edge on the line.
        var right = BindingBarLayout.BarAnchor(aspect: 1.6f, edge: OverlayBarEdge.Right, margin: 0.025f);

        AssertPoint(expectedX: 1.56f, expectedY: 0.5f, actual: right);
        AssertPoint(expectedX: 1.51f, expectedY: 0.5f, actual: BindingBarLayout.PlateCenter(anchor: right, buttonSize: 0.1f, edge: OverlayBarEdge.Right, pitchX: 0f, pitchY: 0f));
    }

    [Fact]
    public void AnAnchorGroupHangsByItsOwnExtentAndCentersAcrossTheEdge() {
        // One bottom group (the nested crossbar: resting at 0, a wing down to −3) and one left column, in one bar.
        OverlayBindingSlot[] slots = [
            Slot(edge: OverlayBarEdge.Bottom, margin: 0.05f, x: -2f, y: 0f),
            Slot(edge: OverlayBarEdge.Bottom, margin: 0.05f, x: 4f, y: 1f),
            Slot(edge: OverlayBarEdge.Bottom, margin: 0.05f, x: 0f, y: -3f),
            Slot(edge: OverlayBarEdge.Bottom, margin: 0.05f, x: 9f, y: -9f, visible: false),
            Slot(edge: OverlayBarEdge.Left, margin: 0.025f, x: 0f, y: 0f),
            Slot(edge: OverlayBarEdge.Left, margin: 0.025f, x: 0f, y: -2f),
        ];

        BindingBarSeatComposer.AnchorGroups(slots: slots);

        // Bottom: lowest VISIBLE plate (−3) to 0, x extent [−2, 4] centered on 1 → shifted by −1.
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
    public void TheLayoutCellSelectsANamedLayoutAndFallsBackToTheRowsOwn() {
        var crossbar = new WorldBindingBarLayout(Scale: 1f);
        var linear = new WorldBindingBarLayout(Scale: 2f);
        var authoring = new WorldBindingBarAuthoring(
            Banks: [],
            Layout: crossbar,
            Layouts: new Dictionary<string, WorldBindingBarLayout>(comparer: StringComparer.Ordinal) { ["linear"] = linear, },
            SlotSet: []
        );

        Assert.Same(expected: linear, actual: authoring.LayoutNamed(name: "linear"));
        Assert.Same(expected: crossbar, actual: authoring.LayoutNamed(name: "no-such"));
        Assert.Same(expected: crossbar, actual: authoring.LayoutNamed(name: null));
    }

    private static void AssertPoint(float expectedX, float expectedY, Vector2 actual) {
        Assert.Equal(expected: expectedX, actual: actual.X, tolerance: 1e-5f);
        Assert.Equal(expected: expectedY, actual: actual.Y, tolerance: 1e-5f);
    }
    private static OverlayBindingSlot Slot(OverlayBarEdge edge, float margin, float x, float y, bool visible = true) => new(
        Alpha: 1f,
        AnchorEdge: edge,
        AnchorMargin: margin,
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
    private static OverlayBindingSlot ComposeOne(string source, IReadOnlyDictionary<string, Vector2> placements, Vector2 bankOffset, bool bankMirror) {
        var destination = new OverlayBindingSlot[1];

        Compose(
            bankMirror: bankMirror,
            bankOffset: bankOffset,
            destination: destination,
            placements: placements,
            slotSet: [source],
            unplacedRowLift: 0f,
            unplacedSlotSpacing: 1f
        );

        return destination[0];
    }
    private static void Compose(string[] slotSet, IReadOnlyDictionary<string, Vector2> placements, Vector2 bankOffset, bool bankMirror, float unplacedRowLift, float unplacedSlotSpacing, OverlayBindingSlot[] destination) =>
        BindingBarSeatComposer.ComposeBank(
            anchorEdge: OverlayBarEdge.Bottom,
            anchorMargin: 0f,
            bankAlpha: 1f,
            bankMirror: bankMirror,
            bankOffset: bankOffset,
            bankOrder: 0,
            destination: destination,
            hideUnbound: false,
            isCommandHeld: null,
            isPressed: null,
            placements: placements,
            resolveBadge: static _ => OverlayResolvedGlyph.None,
            resolveIcon: static _ => OverlayResolvedGlyph.None,
            slotSet: slotSet,
            text: true,
            unplacedRowLift: unplacedRowLift,
            unplacedSlotSpacing: unplacedSlotSpacing,
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
