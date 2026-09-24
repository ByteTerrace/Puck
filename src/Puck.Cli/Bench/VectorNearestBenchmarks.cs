using BenchmarkDotNet.Attributes;

using ArenaEffectHost = Puck.State.Rules.ArenaEffectHost;
using ArenaTransform = Puck.State.Rules.ArenaTransform;
using ArenaTransformBinding = Puck.State.Rules.ArenaTransformBinding;
using ArenaTransforms = Puck.State.Rules.ArenaTransforms;
using RuleCompileContext = Puck.State.Rules.RuleCompileContext;
using RuleCompiler = Puck.State.Rules.RuleCompiler;
using RuleVocabulary = Puck.State.Rules.RuleVocabulary;

namespace Puck.Cli.Bench;

// One `nearest` firing through the effect host over a full 256-key, 256-dimension table into a five-match Fixed
// ranking: the cross-row scan, one Q48.16 cosine per key, the k-wide insertion and the destination rewrite. The
// work-shape laws in VectorNearestWorkLawTests hold the firing to zero allocation and one scoring per key; this is
// where its latency is read, on a quiet machine.
[MemoryDiagnoser]
public class VectorNearest {
    private const int Capacity = 256;
    private const int Dimensions = 256;

    private ArenaEffectHost? m_host;
    private ArenaTransform? m_transform;

    /// <summary>Compiles the ranking and seeds the table and the query.</summary>
    [GlobalSetup]
    public void Setup() {
        var cells = new StateCell[Capacity];

        for (var i = 0; (i < Capacity); i++) {
            var components = new sbyte[Dimensions];

            components[(i % Dimensions)] = 127;
            cells[i] = new StateCell(
                Key: CellName.Parse(candidate: $"c{i}"),
                Value: CellValue.Vector(components: components)
            );
        }

        var section = new StateSection(
            Rows: [
                new StateRow(
                    Name: CellName.Parse(candidate: "source"),
                    Kind: CellKind.Vector,
                    Capacity: Capacity,
                    Cells: cells,
                    Space: "lore"
                ),
                new StateRow(
                    Name: CellName.Parse(candidate: "query"),
                    Kind: CellKind.Vector,
                    Space: "lore"
                ),
                new StateRow(
                    Name: CellName.Parse(candidate: "ranked"),
                    Kind: CellKind.Fixed,
                    Capacity: 8
                ),
            ],
            Spaces: [
                new StateSpace(
                    name: CellName.Parse(candidate: "lore"),
                    model: "model",
                    revision: "r1",
                    dimensions: Dimensions
                ),
            ]
        );
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
        var query = new sbyte[Dimensions];

        query[3] = 127;
        if (!host.Arena.TryWriteVector(
            components: query,
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            reason: out var writeReason,
            rowOrdinal: RuleCompiler.ResolveRowOrdinal(
                context: context,
                name: "query"
            )
        )) {
            throw new InvalidOperationException(message: writeReason);
        }

        var compiled = RuleCompiler.Compile(
            context: context,
            rule: new Rule(
                Name: CellName.Parse(candidate: "nearest-256"),
                Effects: [new ActionEffect.TransformState(Transform: new StateTransform.Nearest(
                    From: "source",
                    Into: "ranked",
                    K: 5,
                    Query: "query",
                    Threshold: null
                ))]
            )
        );

        if (!ArenaTransforms.TryVectorTransform(
            catalog: context.Catalog,
            effect: compiled.Effects[0],
            reader: host,
            reason: out var resolveReason,
            transform: out var transform
        )) {
            throw new InvalidOperationException(message: resolveReason);
        }

        m_host = host;
        m_transform = transform;
    }
    /// <summary>Fires the ranking once.</summary>
    /// <returns>Whether it landed.</returns>
    [Benchmark]
    public bool Nearest256x256() => m_host!.TryTransform(
        binding: ArenaTransformBinding.None,
        moved: out _,
        refusal: out _,
        transform: m_transform!
    );
}
