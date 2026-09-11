using System.Globalization;
using System.Numerics;

using Puck.SignedDistance;
using Puck.World;
using Puck.World.Client;

namespace Puck.Cli.Creation;

internal static partial class CreationCommand {
    private static void ReportStepClamps(WorldDefinition definition, WorldPrototype prototype) {
        if (prototype.Document.TextRuns is { Count: > 0 }) {
            Console.Out.WriteLine("  step clamps unavailable: text runs require the render host's resolved font atlas; inspect world.budget.");
            return;
        }

        Console.Out.WriteLine("  step clamps: unit-scale rest geometry; field bounds, not measured march/GPU cost");
        if (!WorldPlacementStamper.IsAnimated(prototype)) {
            var builder = new SdfProgramBuilder();
            WorldPlacementStamper.EmitStatic(
                builder: builder,
                definition: definition,
                creations: [prototype],
                placements: [new WorldPlacement("stats", prototype.Id, Vector3.Zero, 0f, 1f)]
            );
            ReportProgramClamps("static", builder.Build(buildInstanceGrid: false));
        }

        var pool = new WorldStampPool();
        pool.Reconcile(
            placements: [],
            creations: [prototype],
            dynamics: [],
            bodyStamps: [new WorldStampPool.BodyStamp(0, prototype, 1f, WorldLookMotion.Default)]
        );
        var pooled = new SdfProgramBuilder();
        pool.Emit(pooled, definition, probeWorstCase: false, maxPlacementScale: 1f, slotBase: 0);
        ReportProgramClamps("pooled", pooled.Build(buildInstanceGrid: false));
    }

    private static void ReportProgramClamps(string path, SdfProgram program) {
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    {path}: globalStepScale {program.StepScale:G6}, scoped clamps {program.FieldScopeClamps.Count} ({program.FieldScopeClamps.Count(static clamp => clamp.ShapeCount > 1)} shared)"));
        foreach (var clamp in program.FieldScopeClamps.OrderBy(static clamp => clamp.StepScale)) {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"      scope instance {clamp.InstanceIndex}, instructions {clamp.PushInstructionIndex}..{clamp.PopInstructionIndex}: stepScale {clamp.StepScale:G6}, field bound {1f / clamp.StepScale:G6}x, {clamp.ShapeCount} shape(s){(clamp.ShapeCount > 1 ? " sharing one clamp" : string.Empty)}"));
        }
    }
}
