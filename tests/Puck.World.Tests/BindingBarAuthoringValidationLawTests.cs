using Puck.Commands;
using Puck.Input;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins every binding-bar authoring refusal to its field name and a one-value-different passing control.</summary>
public sealed class BindingBarAuthoringValidationLawTests {
    public static IEnumerable<object[]> Cases() {
        var layout = WorldBindingBarLayout.Default;

        yield return ["visible.windowSeconds", Policy(layout: layout) with { Visible = new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: -1f) }, Policy(layout: layout) with { Visible = new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: 3f) }];
        yield return ["iconRow 'actionIcons' must be spelled state.<row>", Policy(layout: layout) with { IconRow = "actionIcons" }, Policy(layout: layout) with { IconRow = "state.actionIcons" }];
        yield return ["iconRow 'state.noSuch' names no declared state row", Policy(layout: layout) with { IconRow = "state.noSuch" }, Policy(layout: layout) with { IconRow = "state.actionIcons" }];
        yield return ["iconRow 'state.numericIcons' names a Int row", Policy(layout: layout) with { IconRow = "state.numericIcons" }, Policy(layout: layout) with { IconRow = "state.actionIcons" }];
        yield return ["iconRow 'state.scalarIcons' names a scalar row", Policy(layout: layout) with { IconRow = "state.scalarIcons" }, Policy(layout: layout) with { IconRow = "state.actionIcons" }];
        yield return ["visible.predicates[0].windowSeconds", Policy(layout: layout) with { Visible = new OverlayPredicate.Any(Predicates: [new OverlayPredicate.Recently(Fact: OverlayFact.SeatInput, WindowSeconds: float.NaN)]) }, Policy(layout: layout) with { Visible = new OverlayPredicate.Any(Predicates: [new OverlayPredicate.Now(Fact: OverlayFact.WheelOpen)]) }];
        yield return ["layouts['only'].buttonSize", Policy(layout: layout with { ButtonSize = 0f }), Policy(layout: layout with { ButtonSize = 0.01f })];
        yield return ["layouts['only'].anchor.inset", Policy(layout: layout with { Anchor = new WorldBindingBarAnchor(Inset: -0.1f) }), Policy(layout: layout with { Anchor = new WorldBindingBarAnchor(Inset: 0f) })];
        yield return ["layouts['only'].banks['resting'].anchor.inset", Policy(layout: layout with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [], Anchor: new WorldBindingBarAnchor(Edge: WorldBindingBarEdge.Left, Inset: float.NaN))) }), Policy(layout: layout with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [], Anchor: new WorldBindingBarAnchor(Edge: WorldBindingBarEdge.Left, Inset: 0f))) })];
        yield return ["layouts['only'].tables['cross'][0].badge needs exactly", Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f, Badge: [1f]))), Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f, Badge: [1f, 0f])))];
        yield return ["layouts['only'].tables['cross'][0].badge [1.5, 0]", Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f, Badge: [1.5f, 0f]))), Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f, Badge: [1f, 0f])))];
        yield return ["layouts['only'].tables['cross'][0].source 'mouse.button1' is not in", Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Mouse.Button(number: 1), X: 0f, Y: 0f))), Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f)))];
        yield return ["layouts['only'].tables['cross'][1].source 'gamepad.dpadUp' is placed twice", Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f), new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 1f, Y: 0f))), Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f)))];
        yield return ["layouts['only'].tables['cross'][0] needs finite x and y", Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: float.NaN, Y: 0f))), Policy(layout: Tabled(layout, new WorldBindingBarSlotPlacement(Source: InputSources.Gamepad.DpadUp, X: 0f, Y: 0f)))];
        yield return ["layouts['only'].banks['ghost'] names no bank", Policy(layout: layout with { Banks = new Dictionary<string, WorldBindingBarBankPlacement>(comparer: StringComparer.Ordinal) { ["ghost"] = new WorldBindingBarBankPlacement(Pieces: []) } }), Policy(layout: layout with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [])) })];
        yield return ["layouts['only'].banks['resting'].pieces is required", Policy(layout: layout with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: null!)) }), Policy(layout: layout with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [])) })];
        yield return ["layouts['only'].banks['resting'].pieces[0].table 'ghost' names no entry", Policy(layout: Tabled(layout) with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [new WorldBindingBarPiece(Table: "ghost")])) }), Policy(layout: Tabled(layout) with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [new WorldBindingBarPiece(Table: "cross")])) })];
        yield return ["layouts['only'].banks['resting'].pieces[0].at needs finite", Policy(layout: Tabled(layout) with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [new WorldBindingBarPiece(Table: "cross", At: [1f])])) }), Policy(layout: Tabled(layout) with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [new WorldBindingBarPiece(Table: "cross", At: [1f, 2f])])) })];
        yield return ["layouts['only'].banks['resting'].pieces[0].badge needs", Policy(layout: Tabled(layout) with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [new WorldBindingBarPiece(Table: "cross", Badge: [2f, 0f])])) }), Policy(layout: Tabled(layout) with { Banks = Banks(new WorldBindingBarBankPlacement(Pieces: [new WorldBindingBarPiece(Table: "cross", Badge: [1f, 0f])])) })];
        yield return ["layout 'ghost' names no entry", Policy(layout: layout) with { Layout = "ghost" }, Policy(layout: layout)];
        yield return ["layout 'only' names no entry of bindingOverlays[0].bindingBar.layouts — none are authored", Policy(layout: layout) with { Layouts = null }, Policy(layout: layout) with { Layouts = null, Layout = null }];
        yield return ["layoutCell 'bar' must be spelled state.<row>.<key>", Policy(layout: layout) with { LayoutCell = "bar" }, Policy(layout: layout) with { LayoutCell = "state.bar.layout" }];
        yield return ["layouts['only'].glyphOffsetRatio", Policy(layout: layout with { GlyphOffsetRatio = -0.01f }), Policy(layout: layout with { GlyphOffsetRatio = 0f })];
        yield return ["layouts['only'].glyphSizeRatio", Policy(layout: layout with { GlyphSizeRatio = 0f }), Policy(layout: layout with { GlyphSizeRatio = 0.01f })];
        yield return ["layouts['only'].modifierHalfRatio", Policy(layout: layout with { ModifierHalfRatio = 0f }), Policy(layout: layout with { ModifierHalfRatio = 0.35f })];
        yield return ["layouts['only'].modifierSpacingRatio", Policy(layout: layout with { ModifierSpacingRatio = 0f }), Policy(layout: layout with { ModifierSpacingRatio = 1.1f })];
        yield return ["layouts['only'].modifierGlyphRatio", Policy(layout: layout with { ModifierGlyphRatio = 0f }), Policy(layout: layout with { ModifierGlyphRatio = 0.8f })];
        yield return ["layouts['only'].labelCellRatio", Policy(layout: layout with { LabelCellRatio = 0f }), Policy(layout: layout with { LabelCellRatio = 1.9f })];
        yield return ["layouts['only'].labelCellMinPx", Policy(layout: layout with { LabelCellMinPx = 0f }), Policy(layout: layout with { LabelCellMinPx = 12f })];
        yield return ["layouts['only'].labelGapRatio", Policy(layout: layout with { LabelGapRatio = float.NaN }), Policy(layout: layout with { LabelGapRatio = 1.4f })];
        yield return ["layouts['only'].hintCellRatio", Policy(layout: layout with { HintCellRatio = 0f }), Policy(layout: layout with { HintCellRatio = 1.6f })];
        yield return ["layouts['only'].hintCellMinPx", Policy(layout: layout with { HintCellMinPx = 0f }), Policy(layout: layout with { HintCellMinPx = 10f })];
        yield return ["layouts['only'].hintLineStepRatio", Policy(layout: layout with { HintLineStepRatio = 0f }), Policy(layout: layout with { HintLineStepRatio = 1.3f })];
        yield return ["layouts['only'].hintBaseGapRatio", Policy(layout: layout with { HintBaseGapRatio = float.NaN }), Policy(layout: layout with { HintBaseGapRatio = 2.2f })];
        yield return ["slotSet[0]", Policy(layout: layout) with { SlotSet = ["NotARealSource"] }, Policy(layout: layout) with { SlotSet = [InputSources.Gamepad.DpadUp] }];
        yield return ["slotSet[1]", Policy(layout: layout) with { SlotSet = [InputSources.Gamepad.DpadUp, InputSources.Gamepad.DpadUp] }, Policy(layout: layout) with { SlotSet = [InputSources.Gamepad.DpadUp, InputSources.Gamepad.DpadRight] }];
        yield return [$"slotSet declares {(WorldBindingBarCapacity.MaxSlots + 1)} entries", Policy(layout: layout) with { SlotSet = OverlongSlotSet() }, Policy(layout: layout)];
        yield return ["banks must declare at least one bank", Policy(layout: layout) with { Banks = [] }, Policy(layout: layout)];
        yield return ["banks declares 6 entries", Policy(layout: layout) with { Banks = [.. Enumerable.Range(start: 0, count: 6).Select(selector: static index => (OneBank[0] with { Id = $"bank{index}" }))] }, Policy(layout: layout)];
        yield return ["banks[1].id", Policy(layout: layout) with { Banks = [OneBank[0], OneBank[0]] }, Policy(layout: layout) with { Banks = [OneBank[0], (OneBank[0] with { Id = "second" })] }];
        yield return ["banks[0].pageId 'no-such-page'", Policy(layout: layout) with { Banks = [OneBank[0] with { PageId = "no-such-page" }] }, Policy(layout: layout)];
        yield return ["banks[0].alpha", Policy(layout: layout) with { Banks = [OneBank[0] with { Alpha = 1.5f }] }, Policy(layout: layout)];
    }
    [MemberData(nameof(Cases))]
    [Theory]
    public void InvalidValueRefusesByNameBesidePassingControl(string field, WorldBindingBarAuthoring invalid, WorldBindingBarAuthoring control) {
        var denied = WithPolicy(policy: invalid);
        var admitted = WithPolicy(policy: control);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: $"bindingOverlays[0].bindingBar.{field}");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    /// <summary>An omitted layout override resolves to its engine default rather than zero — the property the
    /// nullable override surface rests on.</summary>
    [Fact]
    public void OmittedLayoutOverridesResolveToTheirDefaults() {
        var bare = new WorldBindingBarLayout();

        Assert.Equal(expected: WorldBindingBarLayout.DefaultButtonSize, actual: bare.ResolvedButtonSize);
        Assert.Equal(expected: WorldBindingBarLayout.DefaultModifierHalfRatio, actual: bare.ResolvedModifierHalfRatio);
        Assert.Equal(expected: WorldBindingBarLayout.DefaultHintBaseGapRatio, actual: bare.ResolvedHintBaseGapRatio);
        Assert.Same(expected: WorldBindingBarLayout.DefaultAnchor, actual: bare.ResolvedAnchor);

        var authored = (bare with { ButtonSize = 0.5f });

        Assert.Equal(expected: 0.5f, actual: authored.ResolvedButtonSize);
        Assert.Equal(expected: WorldBindingBarLayout.DefaultGlyphSizeRatio, actual: authored.ResolvedGlyphSizeRatio);
    }

    private static readonly WorldBindingBarBank[] OneBank = [new WorldBindingBarBank(Id: "resting", PageId: "base", Alpha: 1f)];

    private static Dictionary<string, WorldBindingBarBankPlacement> Banks(WorldBindingBarBankPlacement resting) => new(comparer: StringComparer.Ordinal) { ["resting"] = resting, };
    private static WorldBindingBarLayout Tabled(WorldBindingBarLayout layout, params WorldBindingBarSlotPlacement[] rows) => layout with {
        Tables = new Dictionary<string, IReadOnlyList<WorldBindingBarSlotPlacement>>(comparer: StringComparer.Ordinal) { ["cross"] = rows, },
    };

    private static string[] OverlongSlotSet() {
        var sources = new string[(WorldBindingBarCapacity.MaxSlots + 1)];

        for (var index = 0; (index < sources.Length); index++) {
            sources[index] = InputSources.Mouse.Button(number: (index + 1));
        }

        return sources;
    }
    private static WorldBindingBarAuthoring Policy(WorldBindingBarLayout layout) => new(
        Banks: OneBank,
        Layout: "only",
        Layouts: new Dictionary<string, WorldBindingBarLayout>(comparer: StringComparer.Ordinal) { ["only"] = layout, },
        SlotSet: [InputSources.Gamepad.DpadUp]
    );
    private static WorldDefinition WithPolicy(WorldBindingBarAuthoring policy) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "actionIcons"), Kind: CellKind.Text, Capacity: 8, Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "jump"), Text: "known.icon")]),
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "numericIcons"), Kind: CellKind.Int, Capacity: 8),
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "scalarIcons"), Kind: CellKind.Text, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Text: "known.icon")]),
        ]),
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
                Id: "binding-bar-law",
                Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion,
                    Modifiers: [],
                    Chords: [
                        new BindingChordDefinition(
                            Group: "bindingBarLaw",
                            Page: new BindingPageDefinition(Id: "base", Entries: [])
                        ),
                    ]
                ),
                BindingBar: policy
            ),
        ],
    };
}
