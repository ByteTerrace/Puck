using System.Diagnostics;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a `nearest` transform over a 256-capacity, 256-dimension table costs under
/// 100 microseconds through <see cref="ArenaEffectHost.TryTransform"/> — the cross-row scan, score, and
/// destination write the transform itself performs, apart from any host tick machinery above it.</summary>
public sealed class VectorNearestPerformanceTests {
    private const int Dimensions = 256;
    private const int Capacity = 256;

    private static StateSection Section() {
        var cells = new StateCell[Capacity];

        for (var i = 0; (i < Capacity); i++) {
            var components = new sbyte[Dimensions];

            components[(i % Dimensions)] = 127;
            cells[i] = new StateCell(
                Key: RulesFixture.Name(value: $"c{i}"),
                Value: CellValue.Vector(components: components)
            );
        }

        return new StateSection(
            Rows: [
                new StateRow(
                    Name: RulesFixture.Name(value: "source"),
                    Kind: CellKind.Vector,
                    Capacity: Capacity,
                    Cells: cells,
                    Space: "lore"
                ),
                new StateRow(
                    Name: RulesFixture.Name(value: "query"),
                    Kind: CellKind.Vector,
                    Space: "lore"
                ),
                new StateRow(
                    Name: RulesFixture.Name(value: "ranked"),
                    Kind: CellKind.Fixed,
                    Capacity: 8
                ),
            ],
            Spaces: [
                new StateSpace(
                    Name: RulesFixture.Name(value: "lore"),
                    Model: "model",
                    Revision: "r1",
                    Dimensions: Dimensions
                ),
            ]
        );
    }

    [Fact]
    public void NearestOver256x256CostsUnderOneHundredMicroseconds() {
        var section = Section();
        var context = new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
        var host = new ArenaEffectHost(
            arena: new StateArena(
                catalog: context.Catalog,
                options: null,
                section: section,
                time: ArenaTime.Origin
            ),
            generators: null
        );
        var queryComponents = new sbyte[Dimensions];

        queryComponents[3] = 127;
        Assert.True(condition: host.Arena.TryWriteVector(
            components: queryComponents,
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            reason: out var writeReason,
            rowOrdinal: RuleCompiler.ResolveRowOrdinal(
                context: context,
                name: "query"
            )
        ), userMessage: writeReason);

        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: new StateTransform.Nearest(
                    From: "source",
                    Into: "ranked",
                    K: 5,
                    Query: "query",
                    Threshold: null
                ))],
                name: "nearest-256"
            )
        );

        Assert.True(condition: ArenaTransforms.TryVectorTransform(
            catalog: context.Catalog,
            effect: compiled.Effects[0],
            reader: host,
            reason: out var resolveReason,
            transform: out var transform
        ), userMessage: resolveReason);

        bool FireOnce() => host.TryTransform(
            binding: ArenaTransformBinding.None,
            moved: out _,
            refusal: out var refusal,
            transform: transform!
        );

        // Warm the JIT and every cache line the transform touches before timing.
        for (var i = 0; (i < 2000); i++) {
            Assert.True(condition: FireOnce());
        }

        const int Iterations = 500;
        var microseconds = new double[Iterations];
        var stopwatch = new Stopwatch();

        for (var i = 0; (i < Iterations); i++) {
            stopwatch.Restart();
            var applied = FireOnce();
            stopwatch.Stop();

            Assert.True(condition: applied);
            microseconds[i] = stopwatch.Elapsed.TotalMicroseconds;
        }

        Array.Sort(array: microseconds);
        var median = microseconds[(Iterations / 2)];

        Assert.True(condition: (median < 100.0), userMessage: $"median nearest-over-256x256 cost {median:F2} us exceeds the 100 us target");
    }
}
