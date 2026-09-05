using Xunit;

using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves an explicit write against a cell carrying a <see cref="WorldStateDynamics"/> easing trait
/// rebases the trait to the applying tick (<c>Server.WorldServer.RebaseCellTraits</c>) rather than replacing it
/// wholesale: the follower keeps chasing from wherever it actually was, receives a velocity kick signed by the
/// referenced <c>dynamics</c> row's own <c>r</c>, and <c>world.undo</c> rebases the same way.</summary>
public sealed class StateDynamicsRebaseLawTests {
    private static readonly WorldPrincipal Actor = WorldPrincipal.Seat(slot: 0);
    private static readonly WorldDynamicsRow KickPositive = new(Damping: 1f, Frequency: 1f, Name: "kickPos", Response: 1f);
    private static readonly WorldDynamicsRow KickZero = new(Damping: 1f, Frequency: 1f, Name: "kickZero", Response: 0f);
    private static readonly WorldDynamicsRow KickNegative = new(Damping: 1f, Frequency: 1f, Name: "kickNeg", Response: -1f);

    private static WorldDefinition BuildDocument(string dynamicsRow) {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "gauge"),
            Kind: CellKind.Int,
            Capacity: 8,
            Cells: [
                new WorldStateCell(Key: CellName.Parse(candidate: "0"), Value: 0, Dynamics: new WorldStateDynamics(EpochTick: 0, Row: dynamicsRow, V0: 0, Y0: 0)),
            ]
        );

        return (Fixtures.BuildDocument().WithWorldState(rows: [row]) with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, KickPositive, KickZero, KickNegative],
        });
    }
    private static WorldStateDynamics ReadTrait(WorldDefinition definition) {
        var row = WorldDefinitionRows.FindStateRow(rows: definition.State, name: "gauge")!;

        foreach (var cell in row.Cells!) {
            if (string.Equals(a: cell.Key.Value, b: "0", comparisonType: System.StringComparison.Ordinal)) {
                return cell.Dynamics!;
            }
        }

        throw new System.InvalidOperationException(message: "cell '0' not found");
    }

    [InlineData("kickPos", 1)]
    [InlineData("kickZero", 0)]
    [InlineData("kickNeg", -1)]
    [Theory]
    public void UpsertStateCell_FromRest_KicksTheVelocitySignedByTheDynamicsRowsResponse(string dynamicsRow, int expectedSign) {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(dynamicsRow: dynamicsRow));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 300, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        // A mutation submitted before a Step() call composes against the tick counter AS IT STOOD before that
        // call, then the call completes and advances it — so the tick a just-applied write rebased at trails
        // NextInputTick by two, not one (one Step call: composes at 0, NextInputTick reads 2 afterward).
        var appliedTick = (fixture.Server.NextInputTick - 2UL);
        var trait = ReadTrait(definition: fixture.Server.Definition);

        // The cell was already at rest at its OLD target (0), so the eased sample the rebase captures is exactly
        // (0, 0) before the kick — the whole velocity is the retarget impulse.
        Assert.Equal(actual: trait.Y0, expected: 0L);
        Assert.Equal(actual: trait.EpochTick, expected: unchecked((long)appliedTick));
        Assert.Equal(actual: System.Math.Sign(value: trait.V0), expected: expectedSign);

        // Truth moved to exactly what was written — the trait rebases, it never overrides the write.
        Assert.True(condition: WorldStateReader.TryRead(definition: fixture.Server.Definition, key: "0", rawValue: out var truth, row: out _, rowName: "gauge", text: out _, tick: appliedTick));
        Assert.Equal(actual: truth, expected: 300L);
    }
    [Fact]
    public void MidFlightRewrite_RebasesFromTheLiveEasedPositionRatherThanEitherEndpoint_WithNoJumpAtTheRebaseTick() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(dynamicsRow: "kickZero"));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 300, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        // 24 further ticks (0.1s at 240 Hz) — well inside the ~1.9s settle horizon for f=1 Hz, ζ=1 — so the second
        // write below rebases from a GENUINELY mid-flight position, never a value already pinned to an endpoint.
        for (var index = 0; (index < 24); index++) {
            fixture.Step();
        }

        var beforeRewriteTick = (fixture.Server.NextInputTick - 1UL);

        Assert.True(condition: WorldStateReader.TryReadEased(definition: fixture.Server.Definition, key: "0", rawValue: out var midFlight, row: out _, rowName: "gauge", text: out _, tick: beforeRewriteTick));
        Assert.InRange(actual: midFlight!.Value, low: 1L, high: 299L);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 600, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var rewriteTick = (fixture.Server.NextInputTick - 2UL);
        var trait = ReadTrait(definition: fixture.Server.Definition);

        // The rebased Y0 is the SAME live eased value just sampled above (bit-exact, since no tick elapsed between
        // the sample and the write applying) — a genuine capture of where the follower actually was, never the old
        // truth (300) nor the new one (600).
        var rebasedRowValue = (FixedQ4816.Round(value: FixedQ4816.FromRawBits(value: trait.Y0)).Value >> FixedQ4816.FractionBitCount);

        Assert.Equal(actual: rebasedRowValue, expected: midFlight);
        Assert.NotEqual(expected: 0L, actual: (trait.Y0 & (FixedQ4816.One.Value - 1L)));
        Assert.Equal(actual: trait.EpochTick, expected: unchecked((long)rewriteTick));

        // Continuity: reading right back at the rebase tick reports EXACTLY Y0 — the write never jumps the follower.
        Assert.True(condition: WorldStateReader.TryReadEased(definition: fixture.Server.Definition, key: "0", rawValue: out var atRebase, row: out _, rowName: "gauge", text: out _, tick: rewriteTick));
        Assert.Equal(actual: atRebase, expected: rebasedRowValue);

        // Chasing the NEW target: read far in the future (the closed form needs no further Step() calls) and
        // confirm convergence to 600, never back to the old truth of 300.
        Assert.True(condition: WorldStateReader.TryReadEased(definition: fixture.Server.Definition, key: "0", rawValue: out var settled, row: out _, rowName: "gauge", text: out _, tick: (rewriteTick + 10_000UL)));
        Assert.Equal(actual: settled, expected: 600L);
    }
    // A rule's AddState effect compiles to the same WorldMutation.UpsertStateCell RebaseCellTraits switches on
    // (Server.WorldServer.Step's Write arm), so a rule-authored write rebases exactly like a directly-submitted one.
    [Fact]
    public void RuleAddState_FromRest_AlsoRebasesAndKicksTheVelocity() {
        var gauge = new WorldStateRow(
            Name: CellName.Parse(candidate: "gauge"),
            Kind: CellKind.Int,
            Capacity: 8,
            Cells: [
                new WorldStateCell(Key: CellName.Parse(candidate: "0"), Value: 0, Dynamics: new WorldStateDynamics(EpochTick: 0, Row: "kickPos", V0: 0, Y0: 0)),
            ]
        );
        var trigger = new WorldStateRow(Name: CellName.Parse(candidate: "trigger"), Kind: CellKind.Int,
            Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)]);
        var document = (Fixtures.BuildDocument().WithWorldState(rows: [gauge, trigger]) with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, KickPositive, KickZero, KickNegative],
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "bump"),
                Gate: new ActionPredicate.CompareState(State: "trigger", Comparison: ActionStateComparison.Equal, Value: 1),
                Mode: ActionTriggerMode.Edge,
                Effects: [new ActionEffect.AddState(State: "gauge", Value: 300, Key: "0")]
            )],
        });

        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "trigger", Key: WorldStateRow.SlotKey.Value, Value: 1, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var appliedTick = (fixture.Server.NextInputTick - 1UL);
        var trait = ReadTrait(definition: fixture.Server.Definition);

        Assert.Equal(actual: trait.Y0, expected: 0L);
        Assert.Equal(actual: trait.EpochTick, expected: unchecked((long)appliedTick));
        Assert.Equal(actual: System.Math.Sign(value: trait.V0), expected: 1);
        Assert.True(condition: WorldStateReader.TryRead(definition: fixture.Server.Definition, key: "0", rawValue: out var truth, row: out _, rowName: "gauge", text: out _, tick: appliedTick));
        Assert.Equal(actual: truth, expected: 300L);
    }
    [Fact]
    public void Undo_RestoresTheRebasedTraitBitExactly() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(dynamicsRow: "kickPos"));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 300, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var afterFirstWrite = ReadTrait(definition: fixture.Server.Definition);

        for (var index = 0; (index < 24); index++) {
            fixture.Step();
        }

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 900, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var afterSecondWrite = ReadTrait(definition: fixture.Server.Definition);

        Assert.NotEqual(actual: afterSecondWrite, expected: afterFirstWrite); // the control: the second write genuinely rebased.

        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();

        var afterUndo = ReadTrait(definition: fixture.Server.Definition);

        Assert.Equal(actual: afterUndo, expected: afterFirstWrite);
    }
}
