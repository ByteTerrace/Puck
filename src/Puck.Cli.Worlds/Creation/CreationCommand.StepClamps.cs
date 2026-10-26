using System.Globalization;
using System.Numerics;

using Puck.SignedDistance;
using Puck.SdfVm;
using Puck.World;
using Puck.World.Client;

namespace Puck.Cli.Creation;

public static partial class CreationCommand {
    private static void ReportStepClamps(WorldDefinition definition, WorldPrototype prototype) {
        if (prototype.Document.TextRuns is { Count: > 0 }) {
            Console.Out.WriteLine(value: "  step clamps unavailable: text runs require the render host's resolved font atlas; inspect world.budget.");
            return;
        }

        Console.Out.WriteLine(value: "  step clamps and segments: unit-scale rest geometry; field bounds, not measured march/GPU cost");
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
                program: builder.Build()
            );
        }

        var pool = new WorldCreationInstances();

        pool.Reconcile(
            placements: [],
            creations: [prototype],
            dynamics: [],
            bodyStamps: [new WorldCreationInstances.BodyStamp(
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
            path: "creation",
            program: pooled.Build(),
            dynamicSlots: pool.DynamicSlotCount
        );
    }
    private static void ReportProgram(string path, SdfProgram program, int dynamicSlots = 0) {
        var transforms = Enumerable.Repeat(new DynamicTransform(Position: Vector3.Zero, Orientation: Quaternion.Identity), dynamicSlots).ToArray();
        var groups = SdfMeshBakeGroup.Plan(program, transforms, SdfFrame.DefaultFarDistance, static (slot, dynamic) => new SdfMeshBakeGroup(slot, dynamic));

        Console.Out.WriteLine($"    {path} bake work: {groups.Count} group(s), {program.Instances.Count} field instance(s), {program.InstructionCount} instructions per full field evaluation; encoded program {checked((((long)program.Words.Length) * sizeof(uint)))} B, {program.MaterialCount} materials, root/part poses {checked((((long)dynamicSlots) * 48))} B");
        Console.Out.WriteLine("      mesh vertices and resident bytes: derived by the bake; read world.budget and world.counters gpu after world.wait bakes");
        Console.Out.WriteLine(value: string.Create(
            CultureInfo.InvariantCulture,
            $"    {path}: globalStepScale {program.StepScale:G6}, scoped clamps {program.FieldScopeClamps.Count} ({program.FieldScopeClamps.Count(predicate: static clamp => (clamp.ShapeCount > 1))} shared)"
        ));
        Console.Out.WriteLine(value: string.Create(
            CultureInfo.InvariantCulture,
            $"      segments {program.SkipSegmentCount} ({program.RigidSegmentCount} rigid)"
        ));
        foreach (var clamp in program.FieldScopeClamps.OrderBy(keySelector: static clamp => clamp.StepScale)) {
            Console.Out.WriteLine(value: string.Create(
                CultureInfo.InvariantCulture,
                $"      scope instance {clamp.InstanceIndex}, instructions {clamp.PushInstructionIndex}..{clamp.PopInstructionIndex}: stepScale {clamp.StepScale:G6}, field bound {(1f / clamp.StepScale):G6}x, {clamp.ShapeCount} shape(s){((clamp.ShapeCount > 1)
                ? " sharing one clamp"
                : string.Empty)}"
            ));
        }
    }
}
