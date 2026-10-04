using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class TemporalShadowCanaryLawTests {
    [Fact]
    public void TheDepartedOccluderLeavesTheLiveGridCellsThatCanReachTheReceiver() {
        const string Path = "tests/Puck.World.Canaries/temporal-shadows/fixture.world.json";
        var definition = AuthoredGameFixtures.Load(relativePath: Path);
        var occluder = Assert.Single(collection: definition.Placements, predicate: row => (row.Id == "occluder"));
        var creation = Assert.Single(collection: definition.Creations, predicate: row => (row.Id == occluder.PrototypeId));

        // An unused reserved face still emits a parked screen slab outside every instance. Its world segments
        // select the conservative flat motion check, so this proof reserves no screen that its geometry never uses.
        Assert.Equal(expected: 0, actual: definition.Authoring.DerivedFaceScreens);
        Assert.Empty(collection: definition.Screens);
        Assert.Empty(collection: WorldFaceCatalog.For(definition: definition).Rows);
        Assert.True(condition: WorldPlacementStamper.IsAnimated(creation: creation),
            userMessage: "The occluder must ride dynamic transforms; a static-stamp edit rebuilds geometry instead of testing motion rejection.");
        using var files = new TemporaryDirectory(prefix: "temporal-shadow-grid-");
        var host = files.Own(owner: WorldBootHarness.Compose(stateDirectory: files,
            presentation: WorldHostPresentation.Offscreen, world: Path).Build());

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var router = host.Services.GetRequiredService<InputRouter>();
        var server = host.Services.GetRequiredService<WorldServer>();

        SdfFrame Capture() => presenter.CaptureFrame(deltaSeconds: (1f / 30f), height: 128, interpolationAlpha: 1f, width: 128);

        _ = Capture();
        _ = Capture();
        var frame = Capture();
        var program = frame.Program;
        var revision = client.DefinitionRevision;

        SdfFrame ScheduledMove(ulong tick) {
            var command = definition.Schedule!.Rows.Single(predicate: row => (row.Tick == tick)).Command;
            var result = registry.Submit(line: command);

            Assert.False(condition: result.IsError, userMessage: result.Output);
            registry.ApplySnapshot(snapshot: router.SnapshotForTick(tick: server.NextInputTick, windowEndTick: ulong.MaxValue));
            server.Advance(stepTicks: Fixtures.StepTicksAt(rateHz: 30));
            var moved = Capture();

            Assert.Equal(expected: revision, actual: client.DefinitionRevision);
            Assert.Same(expected: program, actual: moved.Program);
            Assert.False(condition: moved.ProgramChanged, userMessage: "A program upload seeds previous poses and would mask departing-occluder rejection.");
            return moved;
        }

        var transforms = frame.DynamicTransforms.ToArray();
        var instance = Assert.Single(collection: Enumerable.Range(start: 0, count: program.Instances.Count), predicate: index =>
            (program.Instances[index].Active && program.Instances[index].IsDynamic));
        var slot = program.Instances[instance].Slot;

        Assert.InRange(actual: transforms[slot].Position.X, low: 1.19f, high: 1.21f);
        var scratch = new SdfInstanceGridInput[program.Instances.Count];
        var workspace = new SdfInstanceGrid.Workspace(maxInstances: SdfProgramBuilder.MaxInstances);

        Assert.True(condition: program.RequiresFrameInstanceGridRebuild);
        // Unowned world segments select the conservative flat motion check, which would hide this defect.
        var words = program.Words;
        var segments = checked((int)((words[SdfProgram.ProgramMaterialOffsetLane]
            + (((uint)SdfProgram.MaterialVectorsPerEntry) * words[SdfProgram.ProgramMaterialCountLane]))
            + (((uint)SdfProgram.BoundRecordVectors) * words[SdfProgram.ProgramInstructionCountLane])));
        var instances = ((segments + 1) + (SdfProgram.BoundRecordVectors * program.SkipSegmentCount));
        var world = ((instances + 1) + (SdfProgram.BoundRecordVectors * program.Instances.Count));

        Assert.Equal(expected: 0u, actual: words[((4 * world) + SdfProgram.WorldSegmentCountLane)]);
        transforms = ScheduledMove(tick: 100UL).DynamicTransforms.ToArray();
        Assert.Equal(expected: new Vector3(x: 0f, y: 1f, z: 0f), actual: transforms[slot].Position);
        _ = program.BuildFrameInstanceGrid(inputScratch: scratch, transforms: transforms, workspace: workspace);
        var previous = scratch[instance];
        var direction = Vector3.Normalize(value: new Vector3(x: -0.6f, y: 0.8f, z: 0f));
        var receiver = new Vector3(x: 0.75f, y: 0f, z: 0f);
        var delta = (previous.Center - receiver);
        var along = MathF.Max(x: Vector3.Dot(vector1: delta, vector2: direction), y: 0f);
        var chord = (3f * SdfLights.DefaultPenumbraSlope);

        Assert.Null(@object: Assert.IsType<WorldRenderLight.Directional>(@object: definition.Render.Lighting!.Lights![1]).AngularRadius);
        Assert.True(condition: ((delta - (direction * along)).Length() <= ((previous.Radius + (chord * along)) / MathF.Sqrt(x: (1f - (chord * chord))))));
        transforms = ScheduledMove(tick: 110UL).DynamicTransforms.ToArray();
        Assert.Equal(expected: new Vector3(x: 0f, y: 1f, z: 64f), actual: transforms[slot].Position);
        var grid = program.BuildFrameInstanceGrid(inputScratch: scratch, transforms: transforms, workspace: workspace).ToArray();

        Assert.Equal(expected: 1u, actual: grid[0]);
        Assert.True(condition: scratch[instance].Binnable);
        Assert.DoesNotContain(expected: ((uint)instance), collection: grid.AsSpan(start: ((int)grid[12]), length: ((int)grid[13])).ToArray());
        // The fixed receiver lies in z [-5,5]. Sixteen units bounds a group's enclosing radius over the floor
        // and pillar. At High's nine-unit reach, this overestimates every slab's z padding (direction.z is zero).
        // Check the actual CSR cell containing the body, rather than inferring exclusion from instance count.
        const float Inflate = 16f;

        Assert.Equal(expected: ShadowTier.High, actual: definition.Render.Shadows);
        var pad = BitConverter.UInt32BitsToSingle(value: grid[9]);
        var queryHighZ = (((5f + (chord * ((9f + Inflate) + pad))) + pad) + Inflate);
        var originZ = BitConverter.UInt32BitsToSingle(value: grid[6]);
        var inverseCell = BitConverter.UInt32BitsToSingle(value: grid[7]);
        var lastQueryZ = ((int)MathF.Floor(x: ((queryHighZ - originZ) * inverseCell)));
        var bodyCells = new List<int>();

        for (var cell = 0; (cell < grid[14]); cell++) {
            var start = grid[(((int)grid[10]) + cell)];
            var end = grid[((((int)grid[10]) + cell) + 1)];

            if (grid.AsSpan(start: checked((int)(grid[11] + start)), length: checked((int)(end - start))).Contains(value: ((uint)instance))) {
                bodyCells.Add(item: cell);
            }
        }
        var bodyCell = Assert.Single(collection: bodyCells);
        var bodyZ = (bodyCell / checked((int)(grid[1] * grid[2])));

        Assert.True(condition: (bodyZ > lastQueryZ), userMessage: $"Departed body cell z={bodyZ} must exceed every receiver query cell z<={lastQueryZ}.");
        var returned = ScheduledMove(tick: 120UL);

        Assert.Equal(expected: new Vector3(x: 0f, y: 1f, z: 0f), actual: returned.DynamicTransforms[slot].Position);
    }
}
