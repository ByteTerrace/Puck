using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A binding overlay's own <c>When</c> gates whether it composes into a routed seat's bindings, evaluated
/// against the routed document's live state at the routed engine tick (<see cref="WorldSeatBindings.SyncSeat"/>) —
/// never per frame, but re-evaluated whenever the routed definition changes. Over an advancing state row, the
/// overlay flips into (and only into) the composed profile at the exact engine tick the row's live value crosses
/// the overlay's threshold.</summary>
public sealed class BindingOverlayRoutedSeatGateLawTests {
    private const string OverlayId = "reveal";
    private const string ChordGroup = "revealed-only";

    private static WorldStateRow GaugeRow() => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Advance: new StateAdvance(
            PerSecondNumerator: 1,
            PerSecondDenominator: 1
        ),
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: 0
            )]
    );
    private static WorldBindingOverlay GatedOverlay() => new(
        Id: OverlayId,
        Document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                    Group: ChordGroup,
                    Page: new BindingPageDefinition(
                        Id: "revealed-page",
                        Entries: []
                    )
                )]
        ),
        When: new WorldPlacementResponseCondition.StateCondition(
            State: "gauge",
            Comparison: ActionStateComparison.GreaterOrEqual,
            Value: 5f
        )
    );
    // A fresh top-level document object every call (a new WorldStateSection.World array), mirroring a live routed
    // document's own reference identity changing on every applied tick — the trigger WorldSeatBindings.SyncSeat's
    // stateChanged check reads. The overlay list itself is untouched by these `with` copies, so bindingsChanged
    // never fires here; only the gate's own state-driven re-evaluation is under test.
    private static WorldDefinition AtEngineTick(WorldDefinition baseDocument) => baseDocument with {
        StateRaw = new WorldStateSection(World: [GaugeRow()]),
    };
    private static bool RevealedOverlayComposed(WorldSeatBindings bindings, int slot) =>
        bindings.ComposedDocument(slot: slot).Chords.Any(predicate: chord => (chord.Group == ChordGroup));

    [Fact]
    public void TheOverlayComposesOnlyOnceTheAdvancingRowsLiveValueCrossesItsThreshold() {
        var document = Fixtures.BuildDocument() with { BindingOverlaysRaw = [GatedOverlay()] };
        var bindings = new WorldSeatBindings(definition: document);

        // Independently derived crossing point: value(t) = floor(elapsed / EngineTicksPerSecond) at a 1/second
        // rate from epoch 0, so it first reaches 5 at exactly 5 * EngineTicks.PerSecond engine ticks.
        var crossingTick = (5UL * Puck.Hosting.EngineTicks.PerSecond);

        bindings.SyncSeat(
            definition: AtEngineTick(baseDocument: document),
            engineTick: (crossingTick - 1UL),
            entityIndex: 0,
            nextInputTick: 1,
            slot: 0
        );

        Assert.False(condition: RevealedOverlayComposed(
            bindings: bindings,
            slot: 0
        ));

        bindings.SyncSeat(
            definition: AtEngineTick(baseDocument: document),
            engineTick: crossingTick,
            entityIndex: 0,
            nextInputTick: 1,
            slot: 0
        );

        Assert.True(condition: RevealedOverlayComposed(
            bindings: bindings,
            slot: 0
        ));

        // The mirror: stepping back below the threshold (a routed re-point to an earlier moment, or a decaying
        // row) drops the overlay again, proving the gate reads live state rather than latching once true.
        bindings.SyncSeat(
            definition: AtEngineTick(baseDocument: document),
            engineTick: (crossingTick - 1UL),
            entityIndex: 0,
            nextInputTick: 1,
            slot: 0
        );

        Assert.False(condition: RevealedOverlayComposed(
            bindings: bindings,
            slot: 0
        ));
    }
}
