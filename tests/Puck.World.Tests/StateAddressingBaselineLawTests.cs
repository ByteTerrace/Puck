using Puck.Hosting;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class StateAddressingBaselineLawTests(ITestOutputHelper output) {
    [Fact]
    public void ShippedWorldStateHashesAndOperandDistribution() {
        const string WorldPath = "src/Puck.World/Assets/worlds/puck.world.json";
        var catalog = TestHookInstaller.CreateMachineCatalog();
        var definition = AuthoredGameFixtures.Load(relativePath: WorldPath, catalog: catalog);
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
                hash31 = WorldRuntimeStateHash.Hash(
                    scope: WorldStateHashScope.Capture,
                    server: fixture.Server,
                    tick: 31
                );
            } else if (tick == 151) {
                hash151 = WorldRuntimeStateHash.Hash(
                    scope: WorldStateHashScope.Capture,
                    server: fixture.Server,
                    tick: 151
                );
            }
        }

        output.WriteLine(message: $"puck.world.json state hash at tick 31:  {hash31:x16}");
        output.WriteLine(message: $"puck.world.json state hash at tick 151: {hash151:x16}");

        var compilation = WorldRuleCompilation.Compile(definition: definition);
        var allRules = compilation.Rules.Concat(second: compilation.Interactions).ToArray();

        var operands = new List<OperandFact>();

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
        void CollectGate(GateToken[] gate) {
            foreach (var token in gate) {
                if (token.Left is not null) {
                    operands.Add(item: token.Left);
                }
                if (token.Comparand is not null) {
                    operands.Add(item: token.Comparand);
                }
                CollectTokens(tokens: token.LeftExpression);
                CollectTokens(tokens: token.RightExpression);
            }
        }

        foreach (var rule in allRules) {
            CollectGate(gate: rule.Gate);
            if (rule.Bindings is not null) {
                foreach (var binding in rule.Bindings) {
                    CollectTokens(tokens: binding.Expression);
                }
            }
            if (rule.Decision is not null) {
                if (rule.Decision.Interrupt is not null) {
                    CollectGate(gate: rule.Decision.Interrupt);
                }
                foreach (var opt in rule.Decision.Options) {
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
            expected: 0x05ed666c0438fb08UL
        );
        Assert.Equal(
            actual: hash151,
            expected: 0x92dd209305359d13UL
        );
        Assert.True(condition: (fixedLiteralCellOps > 0));
    }
}
