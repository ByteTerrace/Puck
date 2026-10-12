using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>CPU geometry and selection oracles for the shadow-slots canary's distinct per-sun floor regions.</summary>
public sealed class ShadowSlotsCanaryLawTests {
    private const string Directory = "tests/Puck.World.Canaries/shadow-slots";

    [Fact]
    public void HighSelectsBothSunsAndMediumRetainsTheBrighterEastSun() {
        var definition = AuthoredGameFixtures.Load(relativePath: $"{Directory}/fixture.world.json");
        var mirror = ClientFixtures.StateMirror(definition: definition);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        foreach (var tier in new[] { QualityTier.High, QualityTier.Medium }) {
            var preset = definition.Render.Preset(tier: tier)!.Value;
            var lights = resolver.Resolve(definition: definition, revision: 0, mirror: mirror,
                shadows: new WorldShadowSettings(preset.ShadowLights, preset.ShadowFadeSlots, preset.ShadowFadeTicks, preset.ShadowOverflow)).Lights;
            var owners = Enumerable.Range(start: 0, count: lights.ShadowSlots.SlotCount)
                .Select(selector: slot => ((WorldRenderLight.Directional)definition.Render.Lighting!.Lights![lights.ShadowSlots[slot]]).Name).ToArray();

            Assert.Equal(actual: owners, expected: ((tier == QualityTier.High) ? new[] { "east-sun", "west-sun" } : ["east-sun"]));
            Assert.Equal(expected: 0, actual: lights.ShadowSlots.FadeCount);
        }
    }
    [InlineData("positive")]
    [InlineData("discriminating")]
    [Theory]
    public void EveryRegionSeesFloorOccludedByOnlyItsNamedSun(string leg) {
        var definition = AuthoredGameFixtures.Load(relativePath: $"{Directory}/fixture.world.json");
        var placement = Assert.Single(collection: definition.Placements);

        Assert.Equal(expected: Vector3.Zero, actual: placement.Position.Value);
        Assert.Equal(expected: 0f, actual: placement.YawDegrees);
        Assert.Equal(expected: 1f, actual: placement.Scale);
        // The render stamper reads EngineDocument: creation positions flip X and Z; world cameras and lights do not.
        var boxes = Assert.Single(collection: definition.Creations).EngineDocument.Shapes!;
        var floor = boxes.Single(predicate: static shape => (shape.Id == 0));
        var pillars = boxes.Where(predicate: static shape => (shape.Id != 0)).ToArray();

        foreach (var box in boxes) {
            Assert.Equal(expected: SdfSolidPrimitive.Box, actual: box.Type);
            Assert.Null(@object: box.Domain);
            Assert.Null(@object: box.Swings);
            Assert.Null(@object: box.Slides);
            Assert.Equal(expected: 0f, actual: ((((((box.Smooth ?? 0f) + MathF.Abs(x: (box.Twist ?? 0f))) + MathF.Abs(x: (box.Bend ?? 0f))) +
                MathF.Abs(x: (box.Dilate ?? 0f))) + (box.Onion ?? 0f)) + (box.Chamfer ?? 0f)));
        }
        var row = Assert.Single(collection: definition.Cameras);

        var (eye, target, fieldOfView) = WorldCameraRigCompiler.Compile(definition: definition, domains: new WorldValueDomainGuard(),
            mirror: ClientFixtures.StateMirror(definition: definition), program: row.Rig).Resolve(
                anchor: new SdfAnchor(Orientation: Quaternion.Identity, Position: Vector3.Zero),
                clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f));
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var lights = resolver.Resolve(definition: definition, revision: 0, mirror: ClientFixtures.StateMirror(definition: definition)).Lights;
        using var manifest = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(path1: AuthoredGameFixtures.Root, path2: Directory, path3: "canary.json")));
        var observations = 0;

        foreach (var observation in manifest.RootElement.GetProperty(propertyName: leg).GetProperty(propertyName: "expect").EnumerateArray()) {
            if (observation.GetProperty(propertyName: "type").GetString() != "imageDifference") { continue; }
            var name = observation.GetProperty(propertyName: "name").GetString()!;
            var sun = (name.Contains(comparisonType: StringComparison.Ordinal, value: "east-sun") ? "east-sun" : "west-sun");
            var region = observation.GetProperty(propertyName: "region");
            var extent = observation.GetProperty(propertyName: "extent");
            var width = extent[0].GetInt32();
            var height = extent[1].GetInt32();
            var camera = CameraSnapshot.LookAt(fieldOfViewRadians: fieldOfView, position: eye, target: target,
                viewportHeight: ((uint)height), viewportWidth: ((uint)width));
            var pixels = 0;

            for (var y = 0; (y < height); y++) {
                var v = ((y + 0.5) / height);

                if ((v < region[1].GetDouble()) || (v >= region[3].GetDouble())) { continue; }
                for (var x = 0; (x < width); x++) {
                    var u = ((x + 0.5) / width);

                    if ((u < region[0].GetDouble()) || (u >= region[2].GetDouble())) { continue; }
                    var ray = SourceRay.Through(camera: camera, image: new FixedVector2(X: FixedQ4816.FromDouble(value: u), Y: FixedQ4816.FromDouble(value: v)));
                    var origin = ray.Origin.ToVector3();
                    var direction = ray.Direction.ToVector3();
                    var distance = Entry(box: floor, direction: direction, origin: origin);

                    Assert.True(condition: distance.HasValue, userMessage: $"{name} ({x},{y}) misses the floor");
                    Assert.All(collection: pillars, action: pillar => Assert.True(condition:
                        ((Entry(box: pillar, direction: direction, origin: origin) ?? float.PositiveInfinity) > distance.Value),
                        userMessage: $"{name} ({x},{y}) sees a pillar instead of floor"));
                    var point = (origin + (distance.Value * direction));

                    for (var index = 0; (index < lights.Count); index++) {
                        var light = ((WorldRenderLight.Directional)definition.Render.Lighting!.Lights![index]);
                        var blocked = pillars.Any(predicate: pillar => Entry(box: pillar, origin: point, direction: lights[index].Direction).HasValue);

                        Assert.True(condition: (blocked == (light.Name == sun)), userMessage: $"{name} ({x},{y}) at {point}: {light.Name} blocked={blocked}");
                    }
                    pixels++;
                }
            }
            Assert.True(condition: (pixels > 0), userMessage: $"{name} covers no pixel centers");
            observations++;
        }
        Assert.Equal(actual: observations, expected: 4);
    }

    // Independent slab intersection of each converted box; Scale is its half-extent in the creation unit table.
    private static float? Entry(ShapeDocument box, Vector3 origin, Vector3 direction) {
        var inverse = Quaternion.Conjugate(value: box.Rotation.Value);

        origin = Vector3.Transform(value: (origin - box.Position.Value), rotation: inverse);
        direction = Vector3.Transform(rotation: inverse, value: direction);
        var near = 0f;
        var far = float.PositiveInfinity;

        for (var axis = 0; (axis < 3); axis++) {
            var extent = box.Scale.Value[axis];

            if (MathF.Abs(x: direction[axis]) < 1e-6f) {
                if (MathF.Abs(x: origin[axis]) > extent) { return null; }
                continue;
            }
            var a = ((-extent - origin[axis]) / direction[axis]);
            var b = ((extent - origin[axis]) / direction[axis]);

            near = MathF.Max(x: near, y: MathF.Min(x: a, y: b));
            far = MathF.Min(x: far, y: MathF.Max(x: a, y: b));
        }
        return ((near <= far) ? near : null);
    }
}
