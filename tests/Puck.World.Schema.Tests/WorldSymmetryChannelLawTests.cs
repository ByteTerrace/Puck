using Puck.Physics.Motion;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Laws for the <c>$symmetry:</c> rule channel at compile time: every function spells, each carries the argument shape
/// it takes and no other, the source cell resolves through the ordinary row/key walk, and the numbers the live canary
/// pins are the symmetry lattice's own.
/// </summary>
public sealed class WorldSymmetryChannelLawTests {
    private static WorldDefinition Definition(params ActionEffect[] effects) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "node"), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 5)]),
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "mirror"), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 17)]),
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "nodes"), Kind: CellKind.Int, Capacity: 4, Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: 9)]),
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "out"), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)]),
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "outFixed"), Kind: CellKind.Fixed, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)]),
        ]),
        Rules: [new WorldRule(Name: WorldCellName.Parse(candidate: "probe"), Effects: effects, Mode: ActionTriggerMode.Edge)]
    );
    private static SymmetryOperand Compile(string channel, string? key = null, string destination = "out") {
        var compiled = WorldRuleCompiler.CompileAll(definition: Definition(new ActionEffect.SetState(State: destination, FromState: channel, FromKey: key)));

        Assert.Single(collection: compiled);

        return Assert.IsType<SymmetryOperand>(@object: ((WriteEffect)compiled[0].Effects[0].Value!).From!.Value.Value);
    }
    private static string Refusal(string channel, string? key = null, string destination = "out") {
        var exception = Assert.Throws<WorldRuleException>(testCode: () => WorldRuleCompiler.CompileAll(definition: Definition(new ActionEffect.SetState(State: destination, FromState: channel, FromKey: key))));

        Assert.Equal(expected: WorldRuleRefusal.SymmetryChannelMalformed, actual: exception.Refusal);

        return exception.Message;
    }

    [Fact]
    public void EveryFunction_CompilesToASymmetryOperandOverTheSourceCell() {
        foreach (var (channel, function, argument) in new (string, WorldSymmetryFunction, long)[] {
            ("$symmetry:ring:node", WorldSymmetryFunction.Ring, 0L),
            ("$symmetry:antipode:node", WorldSymmetryFunction.Antipode, 0L),
            ("$symmetry:canonicalRay:node", WorldSymmetryFunction.CanonicalRay, 0L),
            ("$symmetry:cycle:3:node", WorldSymmetryFunction.Cycle, 3L),
            ("$symmetry:cycle:-7:node", WorldSymmetryFunction.Cycle, -7L),
            ("$symmetry:reflect:17:node", WorldSymmetryFunction.Reflect, 17L),
            ("$symmetry:orthogonal:239:node", WorldSymmetryFunction.Orthogonal, 239L),
            ("$symmetry:innerProduct:17:node", WorldSymmetryFunction.InnerProduct, 17L),
        }) {
            var operand = Compile(channel: channel);

            Assert.Equal(expected: WorldRuleFactKind.Symmetry, actual: operand.Kind);
            Assert.Equal(expected: "node", actual: operand.Row);
            Assert.Equal(expected: function, actual: operand.Symmetry);
            Assert.Equal(expected: argument, actual: operand.SymmetryArgument);
            Assert.Null(@object: operand.SymmetryOtherCell);
        }

        foreach (var channel in new[] { "$symmetry:projectionX:node", "$symmetry:projectionY:node" }) {
            Assert.Equal(expected: WorldRuleFactKind.Symmetry, actual: Compile(channel: channel, destination: "outFixed").Kind);
        }
    }
    [Fact]
    public void ACellArgument_AndAKeyedSource_ResolveThroughTheOrdinaryWalk() {
        var reflected = Compile(channel: "$symmetry:reflect:cell:mirror:node");

        Assert.Equal(expected: "mirror", actual: reflected.SymmetryOtherCell!.Value.Row);
        Assert.Contains(expected: reflected.SymmetryOtherCell.Value.Key, collection: new[] { string.Empty, WorldStateRow.SlotKey.Value });

        var keyedOther = Compile(channel: "$symmetry:orthogonal:cell:nodes.0:node");

        Assert.Equal(expected: "nodes", actual: keyedOther.SymmetryOtherCell!.Value.Row);
        Assert.Equal(expected: "0", actual: keyedOther.SymmetryOtherCell.Value.Key);
        Assert.True(condition: keyedOther.SymmetryOtherCell.Value.Handle.IsValid);

        var keyedSource = Compile(channel: "$symmetry:ring:nodes", key: "0");

        Assert.Equal(expected: "nodes", actual: keyedSource.Row);
        Assert.Equal(expected: "0", actual: keyedSource.Key);
    }
    [Fact]
    public void MalformedSpellings_RefuseByName() {
        Assert.Contains(expectedSubstring: "names no symmetry function", actualString: Refusal(channel: "$symmetry:spin:node"));
        Assert.Contains(expectedSubstring: "needs an argument", actualString: Refusal(channel: "$symmetry:reflect:node"));
        Assert.Contains(expectedSubstring: "needs an argument", actualString: Refusal(channel: "$symmetry:innerProduct:node"));
        Assert.Contains(expectedSubstring: "takes no argument", actualString: Refusal(channel: "$symmetry:ring:3:node"));
        Assert.Contains(expectedSubstring: "neither a node", actualString: Refusal(channel: "$symmetry:reflect:240:node"));
        Assert.Contains(expectedSubstring: "whole number of ring steps", actualString: Refusal(channel: "$symmetry:cycle:x:node"));
        Assert.Contains(expectedSubstring: "names no source row", actualString: Refusal(channel: "$symmetry:ring"));

        // The source walk's own refusals still apply: a keyed row needs a key.
        var keyed = Assert.Throws<WorldRuleException>(testCode: () => WorldRuleCompiler.CompileAll(definition: Definition(new ActionEffect.SetState(State: "out", FromState: "$symmetry:ring:nodes"))));

        Assert.NotEqual(expected: WorldRuleRefusal.SymmetryChannelMalformed, actual: keyed.Refusal);
    }
    [Fact]
    public void TheCanaryPins_AreTheLatticesOwnNumbers() {
        // tests/Puck.World.Canaries/symmetry-channel reads these through the rule channel from node 5 and mirror 17.
        Assert.Equal(expected: 87, actual: SymmetryLattice.Cycle(node: 5, steps: 7));
        Assert.Equal(expected: SymmetryLattice.Ring(node: 5), actual: SymmetryLattice.Ring(node: SymmetryLattice.Cycle(node: 5, steps: 7)));
        Assert.InRange(actual: SymmetryLattice.Ring(node: 5), high: 7, low: 0);
        Assert.NotEqual(expected: 5, actual: SymmetryLattice.Antipode(node: 5));
        Assert.Equal(expected: 5, actual: SymmetryLattice.Antipode(node: SymmetryLattice.Antipode(node: 5)));
        Assert.Equal(expected: 5, actual: SymmetryLattice.Reflect(mirror: 17, node: SymmetryLattice.Reflect(mirror: 17, node: 5)));
        Assert.Equal(expected: (SymmetryLattice.Reflect(mirror: 17, node: 5) == 5), actual: SymmetryLattice.AreOrthogonal(first: 5, second: 17));
        Assert.Equal(expected: (SymmetryLattice.InnerProduct(first: 5, second: 17) == 0), actual: SymmetryLattice.AreOrthogonal(first: 5, second: 17));
        Assert.InRange(actual: SymmetryLattice.InnerProduct(first: 5, second: 17), low: -2, high: 2);
    }
}
