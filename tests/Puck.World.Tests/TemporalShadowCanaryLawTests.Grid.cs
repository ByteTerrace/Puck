using System.Numerics;
using System.Text.Json;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class TemporalShadowCanaryLawTests {
    [Fact]
    public void TheDepartedOccluderLeavesTheLiveGridCellsThatCanReachTheReceiver() {
        const string Path = "tests/Puck.World.Canaries/temporal-shadows/fixture.world.json";
        var definition = AuthoredGameFixtures.Load(relativePath: Path);
        var frame = ComposedSdfWorldFixture.Capture(definition: definition, relativePath: Path);
        var program = frame.Program;
        var transforms = frame.DynamicTransforms.ToArray();
        var slot = Assert.Single(collection: Enumerable.Range(start: 0, count: transforms.Length), predicate: index =>
            (transforms[index].Position == new Vector3(x: 1.2f, y: 0f, z: 0f)));
        var instance = Assert.Single(collection: Enumerable.Range(start: 0, count: program.Instances.Count), predicate: index =>
            (program.Instances[index].Active && program.Instances[index].IsDynamic && (program.Instances[index].Slot == slot)));
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
        transforms[slot] = transforms[slot] with { Position = Vector3.Zero };
        _ = program.BuildFrameInstanceGrid(inputScratch: scratch, transforms: transforms, workspace: workspace);
        var previous = scratch[instance];
        var direction = Vector3.Normalize(value: new Vector3(x: -0.6f, y: 0.8f, z: 0f));
        var receiver = new Vector3(x: 0.75f, y: 0f, z: 0f);
        var delta = (previous.Center - receiver);
        var along = MathF.Max(x: Vector3.Dot(vector1: delta, vector2: direction), y: 0f);
        var chord = (3f * SdfLights.DefaultPenumbraSlope);

        Assert.Null(@object: Assert.IsType<WorldRenderLight.Directional>(@object: definition.Render.Lighting!.Lights![1]).AngularRadius);
        Assert.True(condition: ((delta - (direction * along)).Length() <= ((previous.Radius + (chord * along)) / MathF.Sqrt(x: (1f - (chord * chord))))));
        var departure = definition.Schedule!.Rows.Single(predicate: row => (row.Tick == 110UL)).Command;
        using var coordinates = JsonDocument.Parse(json: departure[departure.IndexOf(value: '[')..]);
        var position = coordinates.RootElement.EnumerateArray().Select(selector: item => item.GetSingle()).ToArray();

        Assert.Equal(expected: 3, actual: position.Length);
        transforms[slot] = transforms[slot] with { Position = new Vector3(x: position[0], y: position[1], z: position[2]) };
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
    }
}
