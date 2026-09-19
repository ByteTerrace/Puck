using Puck.Hosting;
using Puck.World.Server;
using Xunit;

using BoardOperand = Puck.State.Rules.BoardOperand;
using CompiledExpressionToken = Puck.State.Rules.CompiledExpressionToken;
using CompiledValueSource = Puck.State.Rules.CompiledValueSource;
using GateToken = Puck.State.Rules.GateToken;
using IRuleOperand = Puck.State.Rules.IRuleOperand;
using PatternOperand = Puck.State.Rules.PatternOperand;
using StateCellOperand = Puck.State.Rules.StateCellOperand;
using SymmetryOperand = Puck.State.Rules.SymmetryOperand;

namespace Puck.World.Tests;

public sealed class StateAddressingBaselineLawTests(ITestOutputHelper output) {
    [Fact]
    public void ShippedWorldStateHashesAndOperandDistribution() {
        const string WorldPath = "src/Puck.World/Assets/worlds/puck.world.json";
        var catalog = TestHookInstaller.CreateMachineCatalog();
        var definition = AuthoredGameFixtures.Load(catalog: catalog, relativePath: WorldPath);
        var width = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));

        using var fixture = Fixtures.FreshServer(
            definition: definition,
            machineCatalog: catalog,
            documentPath: Path.Combine(path1: AuthoredGameFixtures.Root, path2: WorldPath)
        );

        var hash31 = 0UL;
        var hash151 = 0UL;

        for (var tick = 1; (tick <= 151); tick++) {
            fixture.Step(stepTicks: width);
            if (tick == 31) {
                hash31 = WorldStateHashComposition.Hash(
                    scope: WorldStateHashScope.Capture,
                    server: fixture.Server,
                    tick: 31
                );
            } else if (tick == 151) {
                hash151 = WorldStateHashComposition.Hash(
                    scope: WorldStateHashScope.Capture,
                    server: fixture.Server,
                    tick: 151
                );
            }
        }

        output.WriteLine(message: $"puck.world.json state hash at tick 31:  {hash31:x16}");
        output.WriteLine(message: $"puck.world.json state hash at tick 151: {hash151:x16}");

        var allRules = WorldFactsCompiler.CompileAll(definition: definition).Concat(second: WorldFactsCompiler.CompileAllInteractions(definition: definition)).ToArray();

        var operands = new List<IRuleOperand>();

        void CollectTokens(CompiledExpressionToken[]? tokens) {
            if (tokens is null) {
                return;
            }
            foreach (var token in tokens) {
                if (token.Operand is not null) {
                    operands.Add(item: token.Operand);
                }
            }
        }
        void CollectSource(CompiledValueSource source) {
            if (source.Operand is not null) {
                operands.Add(item: source.Operand);
            }
            CollectTokens(tokens: source.Expression);
        }
        void CollectGate(GateToken[] gate) {
            foreach (var token in gate) {
                CollectSource(source: token.LeftSource);
                CollectSource(source: token.RightSource);
            }
        }

        foreach (var rule in allRules) {
            CollectGate(gate: rule.Gate);
            if (rule.Locals is not null) {
                foreach (var binding in rule.Locals) {
                    CollectTokens(tokens: binding.Expression);
                }
            }
            if ((rule as CompiledWorldFactsRule)?.Decision is { } decision) {
                if (decision.Interrupt is not null) {
                    CollectGate(gate: decision.Interrupt);
                }
                foreach (var opt in decision.Options) {
                    CollectGate(gate: opt.Gate);
                    CollectTokens(tokens: opt.Score);
                }
            }
        }

        var fixedLiteralCellOps = 0;
        var dynamicKeyCellOps = 0;
        var liveZoneCellOps = 0;
        var boardOps = 0;
        var symmetryOps = 0;
        var patternOps = 0;
        var otherOps = 0;

        foreach (var op in operands) {
            if (op is StateCellOperand cellOp) {
                if (cellOp.RowFrom is not null) {
                    liveZoneCellOps++;
                } else if (cellOp.KeyFrom is not null) {
                    dynamicKeyCellOps++;
                } else {
                    fixedLiteralCellOps++;
                }
            } else if (op is BoardOperand) {
                boardOps++;
            } else if (op is SymmetryOperand) {
                symmetryOps++;
            } else if (op is PatternOperand) {
                patternOps++;
            } else {
                otherOps++;
            }
        }

        output.WriteLine(message: $"Total compiled operands inspected: {operands.Count}");
        output.WriteLine(message: $"  StateCellOperand fixed row & literal key: {fixedLiteralCellOps}");
        output.WriteLine(message: $"  StateCellOperand dynamic key ($cell:, etc): {dynamicKeyCellOps}");
        output.WriteLine(message: $"  StateCellOperand live zone:               {liveZoneCellOps}");
        output.WriteLine(message: $"  BoardOperand:                             {boardOps}");
        output.WriteLine(message: $"  SymmetryOperand:                          {symmetryOps}");
        output.WriteLine(message: $"  PatternOperand:                           {patternOps}");
        output.WriteLine(message: $"  Other operands (binding, tick, etc):      {otherOps}");

        Assert.Equal(
            actual: hash31,
            expected: 0x05d8507c60b73487UL
        );
        Assert.Equal(
            actual: hash151,
            expected: 0xef603db93519732eUL
        );
        Assert.True(condition: (fixedLiteralCellOps > 0));
    }
}
