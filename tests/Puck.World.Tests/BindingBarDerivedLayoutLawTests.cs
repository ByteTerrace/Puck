using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the derived bank stack: a bank declares only its order, and the engine nests each compass cluster
/// inward on a fixed button-pitch grid — deterministically and symmetrically. An authored offset override still
/// wins, carried through the composer untouched.</summary>
public sealed class BindingBarDerivedLayoutLawTests {
    private const float ButtonSize = 0.075f;

    /// <summary>Order 0 sits on the shared anchor: the resting bank needs no authoring to be where the bar is.</summary>
    [Fact]
    public void OrderZeroSitsOnTheAnchor() =>
        Assert.Equal(expected: Vector2.Zero, actual: Offset(order: 0));

    /// <summary>The first four wings use the settled cross-nesting table: orders 1/2 pull both compass clusters two
    /// pitches inward while stepping above/below, and orders 3/4 stay horizontally aligned four pitches above/below.</summary>
    [Fact]
    public void TheFirstFourWingsNestOnTheButtonPitchGrid() {
        Assert.Equal(expected: new Vector2(x: (2f * ButtonSize), y: -ButtonSize), actual: Offset(order: 1, side: -1));
        Assert.Equal(expected: new Vector2(x: (-2f * ButtonSize), y: -ButtonSize), actual: Offset(order: 1, side: 1));
        Assert.Equal(expected: new Vector2(x: (2f * ButtonSize), y: (2f * ButtonSize)), actual: Offset(order: 2, side: -1));
        Assert.Equal(expected: new Vector2(x: (-2f * ButtonSize), y: (2f * ButtonSize)), actual: Offset(order: 2, side: 1));
        Assert.Equal(expected: new Vector2(x: 0f, y: (-4f * ButtonSize)), actual: Offset(order: 3, side: -1));
        Assert.Equal(expected: new Vector2(x: 0f, y: (4f * ButtonSize)), actual: Offset(order: 4, side: 1));
    }

    /// <summary>Same order, side, and button size — same offset, every call. Two seats showing the same bar cannot
    /// drift.</summary>
    [Fact]
    public void TheArrangementIsAPureFunctionOfOrderSideAndButtonSize() {
        for (var order = 0; (order < 8); order++) {
            Assert.Equal(expected: Offset(order: order, side: -1), actual: Offset(order: order, side: -1));
            Assert.Equal(expected: Offset(order: order, side: 1), actual: Offset(order: order, side: 1));
        }
    }

    /// <summary>Orders beyond the four-entry nesting table remain deterministic and alternate farther above and
    /// below without a horizontal shift.</summary>
    [Fact]
    public void LaterOrdersAlternateFartherAboveAndBelow() {
        Assert.Equal(expected: new Vector2(x: 0f, y: (-6f * ButtonSize)), actual: Offset(order: 5, side: -1));
        Assert.Equal(expected: new Vector2(x: 0f, y: (6f * ButtonSize)), actual: Offset(order: 6, side: 1));
        Assert.Equal(expected: new Vector2(x: 0f, y: (-8f * ButtonSize)), actual: Offset(order: 7, side: 0));
    }

    /// <summary>An authored override rides the composed slot untouched, so the writer can prefer it over the derived
    /// arrangement; a bank that authors none carries null and takes the derivation.</summary>
    [Fact]
    public void AnAuthoredOverrideRidesTheComposedSlot() {
        var overridden = ComposeOne(bankOffsetOverride: new Vector2(x: -0.24f, y: -0.18f), bankOrder: 3);
        var derived = ComposeOne(bankOffsetOverride: null, bankOrder: 3);

        Assert.Equal(expected: new Vector2(x: -0.24f, y: -0.18f), actual: overridden.BankOffsetOverride);
        Assert.Equal(expected: 3, actual: overridden.BankOrder);
        Assert.Null(@object: derived.BankOffsetOverride);
        Assert.Equal(expected: 3, actual: derived.BankOrder);
    }

    /// <summary>The slot set is read as input source ids: a classic compass source lands in the compass cluster, and
    /// a source no gamepad flag names still places, in the exotics row.</summary>
    [Fact]
    public void SourceIdsCategorizeWithoutADeviceEnum() {
        Assert.Equal(
            actual: BindingBarLayout.Categorize(classicIndex: out var classicIndex, source: InputSources.Gamepad.DpadUp),
            expected: BindingSlotCategory.Classic
        );
        Assert.Equal(expected: 0, actual: classicIndex);
        Assert.Equal(
            actual: BindingBarLayout.Categorize(classicIndex: out _, source: InputSources.Gamepad.Guide),
            expected: BindingSlotCategory.Center
        );
        Assert.Equal(
            actual: BindingBarLayout.Categorize(classicIndex: out var exoticIndex, source: InputSources.Mouse.LeftButton),
            expected: BindingSlotCategory.Exotic
        );
        Assert.Equal(expected: -1, actual: exoticIndex);
    }

    private static OverlayBindingSlot ComposeOne(int bankOrder, Vector2? bankOffsetOverride) {
        var destination = new OverlayBindingSlot[1];

        BindingBarSeatComposer.ComposeBank(
            bankAlpha: 1f,
            bankOffsetOverride: bankOffsetOverride,
            bankOrder: bankOrder,
            destination: destination,
            hideUnbound: false,
            isCommandHeld: null,
            isPressed: null,
            resolveBadge: static _ => OverlayResolvedGlyph.None,
            resolveIcon: static _ => OverlayResolvedGlyph.None,
            slotSet: new[] { InputSources.Gamepad.DpadUp },
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

        return destination[0];
    }
    private static Vector2 Offset(int order, int side = -1) => BindingBarLayout.BankOffset(
        buttonSize: ButtonSize,
        order: order,
        side: side
    );
}
