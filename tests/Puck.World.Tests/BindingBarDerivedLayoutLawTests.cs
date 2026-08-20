using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Overlays;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the derived bank stack: a bank declares only its order, and the engine arranges the fan from that
/// order plus the authored theme's spacing grid — deterministically, symmetrically, and only when the world authors
/// a grid at all. An authored offset override still wins, carried through the composer untouched.</summary>
public sealed class BindingBarDerivedLayoutLawTests {
    private const float ButtonSize = 0.075f;

    private static readonly OverlayThemeValues.SpaceSet Grid = new(
        HeightBadge: 24f, HeightBindBar: 64f, HeightChip: 40f, HeightConsoleHead: 38f, HeightModeRow: 30f,
        HeightPromptRow: 34f, HeightTrackerBar: 52f, HeightTrackerCell: 26f,
        Space0: 0f, Space1: 4f, Space2: 8f, Space3: 12f, Space4: 16f, Space5: 20f, Space6: 24f, Space8: 32f
    );

    /// <summary>Order 0 sits on the shared anchor: the resting bank needs no authoring to be where the bar is.</summary>
    [Fact]
    public void OrderZeroSitsOnTheAnchor() =>
        Assert.Equal(expected: Vector2.Zero, actual: Offset(order: 0));

    /// <summary>The fan alternates left/right within a row and climbs one row per pair — the shape five stacked
    /// banks need, derived rather than authored five times.</summary>
    [Fact]
    public void TheFanAlternatesSidesAndClimbsOneRowPerPair() {
        var lt = Offset(order: 1);
        var rt = Offset(order: 2);
        var ltrt = Offset(order: 3);
        var rtlt = Offset(order: 4);

        Assert.True(condition: (lt.X < 0f));
        Assert.Equal(expected: -lt.X, actual: rt.X);
        Assert.Equal(expected: lt.Y, actual: rt.Y);
        Assert.True(condition: (lt.Y < 0f));

        Assert.True(condition: (ltrt.X < 0f));
        Assert.Equal(expected: -ltrt.X, actual: rtlt.X);
        Assert.Equal(expected: ltrt.Y, actual: rtlt.Y);

        // The second row draws in half as far and rises twice as high.
        Assert.Equal(expected: (lt.X * 0.5f), actual: ltrt.X, tolerance: 1e-6f);
        Assert.Equal(expected: (lt.Y * 2f), actual: ltrt.Y, tolerance: 1e-6f);
    }

    /// <summary>Same order, same grid, same button size — same offset, every call. The stack is a pure function of
    /// what the document declares, so two seats showing the same bar cannot drift.</summary>
    [Fact]
    public void TheArrangementIsAPureFunctionOfOrderAndGrid() {
        for (var order = 0; (order < 8); order++) {
            Assert.Equal(expected: Offset(order: order), actual: Offset(order: order));
        }
    }

    /// <summary>A world with no authored spacing grid (the zeroed absent theme) stacks every bank on the anchor
    /// rather than dividing by a zero grid unit.</summary>
    [Fact]
    public void AnUngriddedThemeStacksEveryBankOnTheAnchor() {
        for (var order = 0; (order < 5); order++) {
            Assert.Equal(
                actual: BindingBarLayout.BankOffset(
                    buttonSize: ButtonSize,
                    order: order,
                    space: in Unset
                ),
                expected: Vector2.Zero
            );
        }
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

    private static readonly OverlayThemeValues.SpaceSet Unset = default;

    private static OverlayBindingSlot ComposeOne(int bankOrder, Vector2? bankOffsetOverride) {
        var destination = new OverlayBindingSlot[1];

        BindingBarSeatComposer.ComposeBank(
            bankAlpha: 1f,
            bankOffsetOverride: bankOffsetOverride,
            bankOrder: bankOrder,
            destination: destination,
            hideUnbound: false,
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
    private static Vector2 Offset(int order) => BindingBarLayout.BankOffset(
        buttonSize: ButtonSize,
        order: order,
        space: in Grid
    );
}
