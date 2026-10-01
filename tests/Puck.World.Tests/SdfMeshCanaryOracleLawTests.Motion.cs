using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Cameras;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfMeshCanaryOracleLawTests {
    /// <summary>The scheduled edit's first frame has a correspondence to its prior pose; the next frame has none of
    /// that displacement. The oracle intersects current geometry, maps mesh points with the inverse current matrix,
    /// then projects the prior point with ViewProjection. It reads every region and bound from the canary manifest.</summary>
    [Fact]
    public void TemporalMotionRegionsDistinguishTheEditedFrameFromItsSettledSuccessor() {
        using var manifest = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: AuthoredGameFixtures.Root, path2: "tests/Puck.World.Canaries/temporal-motion/canary.json")));

        foreach (var leg in new[] { "positive", "discriminating" }) {
            var section = manifest.RootElement.GetProperty(propertyName: leg);
            var definition = AuthoredGameFixtures.Load(relativePath: section.GetProperty(propertyName: "world").GetString()!);

            Assert.True(condition: section.GetProperty(propertyName: "runSchedule").GetBoolean());
            var lines = new List<string>();

            for (var tick = 0UL; (tick <= 83); tick++) {
                foreach (var row in definition.Schedule!.Rows.Where(predicate: row => ((row!.Tick + 1) == tick))) {
                    lines.Add(item: row!.Command);
                }
                lines.Add(item: $"world.screenshot tick{tick}.png");
            }
            var states = Captures(definition: definition, lines: lines);

            foreach (var expectation in section.GetProperty(propertyName: "expect").EnumerateArray()) {
                var path = expectation.GetProperty(propertyName: "capture").GetString()!;
                var tick = ulong.Parse(s: Path.GetFileNameWithoutExtension(path: path).Split(separator: '~')[1],
                    provider: System.Globalization.CultureInfo.InvariantCulture);
                var holds = MotionRegionHolds(expectation: expectation,
                    current: states[$"tick{tick}.png"].Definition, previous: states[$"tick{(tick - 1)}.png"].Definition);

                Assert.Equal(expected: expectation.GetProperty(propertyName: "holds").GetBoolean(), actual: holds);
            }
        }
    }

    private static bool MotionRegionHolds(JsonElement expectation, WorldDefinition current, WorldDefinition previous) {
        var extent = expectation.GetProperty(propertyName: "extent");
        var width = extent[0].GetInt32();
        var height = extent[1].GetInt32();
        var camera = Camera(definition: current, height: ((uint)height), name: "mesh-cam", width: ((uint)width));
        var prior = Camera(definition: previous, height: ((uint)height), name: "mesh-cam", width: ((uint)width));
        var projection = ViewProjection.Create(camera: prior, near: SdfFrameBlock.NearOf(camera: prior));

        static (SdfProgram Program, SdfMeshDraw Draw) Scene(WorldDefinition world) {
            var builder = new SdfProgramBuilder();
            var draws = new List<SdfMeshDraw>();

            WorldPlacementStamper.EmitStatic(builder: builder, creations: world.Creations,
                definition: world, meshDraws: draws, placements: world.Placements);
            return (builder.Build(), Assert.Single(collection: draws));
        }
        var scene = Scene(world: current);
        var old = Scene(world: previous);
        var field = new SdfFieldEvaluator(program: scene.Program);
        var triangles = Triangles(draws: [scene.Draw]);

        Assert.True(condition: Matrix4x4.Invert(matrix: scene.Draw.ObjectToWorld, result: out var inverse));
        var region = expectation.GetProperty(propertyName: "region");
        var minimum = expectation.GetProperty(propertyName: "minimum");
        var maximum = expectation.GetProperty(propertyName: "maximum");
        var tolerance = (expectation.GetProperty(propertyName: "toleranceCodes").GetSingle() / 255f);
        var holds = true;
        var pixels = 0;

        for (var y = 0; (y < height); y++) {
            if ((((y + 0.5) / height) < region[1].GetDouble()) || (((y + 0.5) / height) > region[3].GetDouble())) {
                continue;
            }
            for (var x = 0; (x < width); x++) {
                if ((((x + 0.5) / width) < region[0].GetDouble()) || (((x + 0.5) / width) > region[2].GetDouble())) {
                    continue;
                }
                var ray = Direction(camera: camera, height: height, width: width, x: x, y: y);
                var mesh = Nearest(origin: camera.Position, direction: ray, near: SdfFrameBlock.NearOf(camera: camera), triangles: triangles);
                var marched = field.Raycast(origin: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: camera.Position)),
                    dir: FixedVector3.FromVector3(value: ray), maxDist: FixedQ4816.FromInteger(value: 100), hit: out var hit);

                Assert.True(condition: (!marched || (hit.Confidence == WorldQueryConfidence.Exact)));
                var isMesh = ((mesh is { } distance) && (!marched || (distance <= ((double)hit.Distance))));

                Assert.True(condition: (isMesh || marched), userMessage: "Every judged motion pixel must hit geometry.");
                var point = (camera.Position + (ray * ((float)(isMesh ? mesh!.Value : ((double)hit.Distance)))));
                var oldPoint = (isMesh
                    ? Vector3.Transform(position: Vector3.Transform(matrix: inverse, position: point), matrix: old.Draw.ObjectToWorld)
                    : point);
                var clip = projection.ToClip(world: oldPoint);
                var pixel = new Vector2(x: ((((clip.X / clip.W) + 1f) * 0.5f) * width),
                    y: (((1f - (clip.Y / clip.W)) * 0.5f) * height));
                var motion = ((pixel - new Vector2(x: (x + 0.5f), y: (y + 0.5f))) / 32f);
                float[] color = [Math.Clamp(max: 1f, min: 0f, value: (0.5f + motion.X)),
                    Math.Clamp(max: 1f, min: 0f, value: (0.5f + motion.Y)), 1f, 1f];

                for (var channel = 0; (channel < color.Length); channel++) {
                    holds &= ((color[channel] >= (minimum[channel].GetSingle() - tolerance)) &&
                        (color[channel] <= (maximum[channel].GetSingle() + tolerance)));
                }
                pixels++;
            }
        }
        Assert.True(condition: (pixels > 0));
        return holds;
    }
}
