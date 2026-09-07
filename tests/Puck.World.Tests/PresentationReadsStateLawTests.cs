using System.Numerics;

using Puck.Commands;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: presentation reads state through the same two doors every other bindable field uses — no third reading
/// of "compare a state cell" and no second color grammar. The sky's authored colors speak <see cref="BindableColor"/>
/// (alpha accepted, ignored by the opaque render path) instead of <c>WorldColor</c>'s own narrower dialect, and a
/// binding overlay's optional <see cref="WorldBindingOverlay.When"/> reuses
/// <see cref="WorldPlacementResponseCondition.StateCondition"/> — the placement response facet's own shape — so the
/// active overlay for a seat is the first whose condition holds.
/// </summary>
public sealed class PresentationReadsStateLawTests {
    private const string AwakenedRow = "awakened";
    private const string RevealedGroup = "revealed";
    private const string RevealedPageId = "revealed-base";

    private static WorldStateRow AwakenedCell(long value) => new(
        Name: CellName.Parse(candidate: AwakenedRow),
        Kind: CellKind.Int,
        Cells: [new StateCell(WorldStateRow.SlotKey, value)]
    );
    private static WorldPlacementResponseCondition.StateCondition Awakened() => new(
        State: AwakenedRow,
        Comparison: ActionStateComparison.GreaterOrEqual,
        Value: 1
    );
    private static WorldBindingOverlay BaseOverlay() => new(
        Id: "base",
        Document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(Group: "resting", Page: new BindingPageDefinition(Id: "resting-base", Entries: []))]
        )
    );
    private static WorldBindingOverlay RevealedOverlay(WorldPlacementResponseCondition.StateCondition? when) => new(
        Id: "revealed",
        When: when,
        Document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(Group: RevealedGroup, Page: new BindingPageDefinition(Id: RevealedPageId, Entries: []))]
        )
    );
    private static WorldDefinition DocumentWithGate(long awakened) => Fixtures.BuildDocument().WithWorldState(rows: [AwakenedCell(value: awakened)]) with {
        BindingOverlaysRaw = [BaseOverlay(), RevealedOverlay(when: Awakened())],
    };

    // --- The sky reads a cell ---

    [Fact]
    public void SkyZenith_BoundToStateTextCell_RecolorsOnTheNextEmit_WithNoRebake() {
        var track = new WorldRenderCycleTrack();
        var sky = new WorldRenderSky(Zenith: new BindableColor(Raw: "state.colors.zenith"));
        var colorsRow = new WorldStateRow(Name: CellName.Parse(candidate: "colors"), Kind: CellKind.Text, Cells: [new StateCell(Key: CellName.Parse(candidate: "zenith"), Text: "#112233")]);
        var first = track.Resolve(definition: (Fixtures.BuildDocument().WithWorldState(rows: [colorsRow]) with { RenderRaw = WorldRenderDefaults.Absent with { Sky = sky } }), revision: 1, tick: 0UL);

        Assert.Equal(expected: new Vector3(x: (0x11 / 255f), y: (0x22 / 255f), z: (0x33 / 255f)), actual: first.SkyZenithColor);

        var moved = new WorldStateRow(Name: CellName.Parse(candidate: "colors"), Kind: CellKind.Text, Cells: [new StateCell(Key: CellName.Parse(candidate: "zenith"), Text: "#AABBCC")]);
        var second = track.Resolve(definition: (Fixtures.BuildDocument().WithWorldState(rows: [moved]) with { RenderRaw = WorldRenderDefaults.Absent with { Sky = sky } }), revision: 2, tick: 0UL);

        // No re-bake: the SAME track instance, told only that the revision moved, reads the new cell straight through.
        Assert.Equal(expected: new Vector3(x: (0xAA / 255f), y: (0xBB / 255f), z: (0xCC / 255f)), actual: second.SkyZenithColor);
    }
    // BindableColor's grammar carries an accepted alpha suffix WorldColor's own #RRGGBB-only dialect refused —
    // resolved as opaque (alpha ignored) here, matching every other opaque render-path color.
    [Fact]
    public void SkyZenith_AcceptsAnAlphaSuffixLiteral_IgnoringAlpha() {
        var track = new WorldRenderCycleTrack();
        var definition = Fixtures.BuildDocument() with { RenderRaw = WorldRenderDefaults.Absent with { Sky = new WorldRenderSky(Zenith: new BindableColor(Raw: "#1B2350FF")) } };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);

        var settings = track.Resolve(definition: definition, revision: 1, tick: 0UL);

        Assert.Equal(expected: new Vector3(x: (0x1B / 255f), y: (0x23 / 255f), z: (0x50 / 255f)), actual: settings.SkyZenithColor);
    }

    // --- The binding overlay swapped on a fact ---

    [Fact]
    public void BindingOverlay_WithWhenOnAStateRow_ActivatesOnTheTickTheRowCrosses() {
        var bindings = new WorldSeatBindings(definition: DocumentWithGate(awakened: 0));

        Assert.False(condition: bindings.TryPageView(slot: 0, pageId: RevealedPageId, view: out _));

        bindings.SyncSeat(slot: 0, definition: DocumentWithGate(awakened: 1), entityIndex: 0, nextInputTick: 1UL);

        Assert.True(condition: bindings.TryPageView(slot: 0, pageId: RevealedPageId, view: out _));

        // The reverse crossing retires it just as promptly.
        bindings.SyncSeat(slot: 0, definition: DocumentWithGate(awakened: 0), entityIndex: 0, nextInputTick: 2UL);

        Assert.False(condition: bindings.TryPageView(slot: 0, pageId: RevealedPageId, view: out _));
    }
    [Fact]
    public void BindingOverlay_WhenUndeclaredRow_RefusesByName_ControlDeclaredRowClean() {
        Laws.RefusalWithControl(
            lawId: "binding-overlay.when-state-row",
            deniedOutcome: static () => WorldDefinitionValidator.TryValidateLocally(
                definition: (Fixtures.BuildDocument().WithWorldState(rows: [AwakenedCell(value: 0)]) with {
                    BindingOverlaysRaw = [BaseOverlay(), RevealedOverlay(when: new WorldPlacementResponseCondition.StateCondition(State: "not-declared", Comparison: ActionStateComparison.GreaterOrEqual, Value: 1))],
                }),
                reason: out _
            ),
            controlOutcome: static () => WorldDefinitionValidator.TryValidateLocally(definition: DocumentWithGate(awakened: 0), reason: out _)
        );
    }
}
