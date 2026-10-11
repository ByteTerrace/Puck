using System.Globalization;
using System.Numerics;

using Puck.SignedDistance;
using Puck.World;
using Puck.World.Client;

namespace Puck.Cli.Creation;

public static partial class CreationCommand {
    private static void ReportStepClamps(WorldDefinition definition, WorldPrototype prototype, SpecializationUnion union) {
        if (prototype.Document.TextRuns is { Count: > 0 }) {
            Console.Out.WriteLine(value: "  step clamps unavailable: text runs require the render host's resolved font atlas; inspect world.budget.");
            return;
        }

        Console.Out.WriteLine(value: "  step clamps and chains: unit-scale rest geometry; field bounds, not measured march/GPU cost");
        if (!WorldPlacementStamper.IsAnimated(creation: prototype)) {
            var builder = new SdfProgramBuilder();

            WorldPlacementStamper.EmitStatic(
                builder: builder,
                definition: definition,
                creations: [prototype],
                placements: [new WorldPlacement(
                        "stats",
                        prototype.Id,
                        Vector3.Zero,
                        0f,
                        1f
                    )]
            );
            ReportProgram(
                path: "static",
                program: builder.Build(buildInstanceGrid: false),
                union: union
            );
        }

        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [prototype],
            dynamics: [],
            bodyStamps: [new WorldStampPool.BodyStamp(
                    0,
                    prototype,
                    1f,
                    WorldLook.Implicit
                )]
        );
        var pooled = new SdfProgramBuilder();

        pool.Emit(
            pooled,
            WorldBakedColors.Of(definition: definition),
            probeWorstCase: false,
            maxPlacementScale: 1f,
            slotBase: 0
        );
        ReportProgram(
            path: "pooled",
            program: pooled.Build(buildInstanceGrid: false),
            union: union
        );
    }
    private static void ReportProgram(string path, SdfProgram program, SpecializationUnion union) {
        Console.Out.WriteLine(value: string.Create(
            CultureInfo.InvariantCulture,
            $"    {path}: globalStepScale {program.StepScale:G6}, scoped clamps {program.FieldScopeClamps.Count} ({program.FieldScopeClamps.Count(predicate: static clamp => (clamp.ShapeCount > 1))} shared)"
        ));
        Console.Out.WriteLine(value: string.Create(
            CultureInfo.InvariantCulture,
            $"      segments {program.SkipSegmentCount} ({program.RigidSegmentCount} rigid) running {program.ChainCount} chain(s); specialization {program.Specialization.Describe()}"
        ));
        union.Add(set: program.Specialization);
        foreach (var clamp in program.FieldScopeClamps.OrderBy(keySelector: static clamp => clamp.StepScale)) {
            Console.Out.WriteLine(value: string.Create(
                CultureInfo.InvariantCulture,
                $"      scope instance {clamp.InstanceIndex}, instructions {clamp.PushInstructionIndex}..{clamp.PopInstructionIndex}: stepScale {clamp.StepScale:G6}, field bound {(1f / clamp.StepScale):G6}x, {clamp.ShapeCount} shape(s){((clamp.ShapeCount > 1)
                ? " sharing one clamp"
                : string.Empty)}"
            ));
        }
    }

    // The union of every reported program's specialization set: the chains and vocabulary one set covering them all
    // would hold.
    private sealed class SpecializationUnion {
        private readonly SortedSet<SdfBlendOp> m_blends = [];
        private readonly HashSet<SdfChain> m_chains = [];
        private readonly SortedSet<SdfBlendOp> m_composes = [];
        private readonly SortedSet<SdfShapeType> m_shapes = [];

        public void Add(SdfSpecializationSet set) {
            m_chains.UnionWith(other: set.Chains);
            m_shapes.UnionWith(other: set.Shapes);
            m_blends.UnionWith(other: set.Blends);
            m_composes.UnionWith(other: set.Composes);
        }
        public string Describe() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{m_chains.Count} distinct chain(s), shapes {m_shapes.Count} ({string.Join(separator: ' ', values: m_shapes)}), blends {m_blends.Count} ({string.Join(separator: ' ', values: m_blends)}), composes {m_composes.Count} ({string.Join(separator: ' ', values: m_composes)})"
            );
    }
}
