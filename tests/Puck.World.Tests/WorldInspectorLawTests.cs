using System.Numerics;
using Puck.Overlays;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldInspectorLawTests {
    [Fact]
    public void FormatterNamesCapturedPlacementAndMaterialWithoutSteadyAllocation() {
        var maps = new WorldPickMapBuilder();

        maps.Instances(first: 0, end: 1, target: new WorldPickTarget(BodyIndex: null, Placement: "crate") { Prototype = "cube" });
        maps.Materials(ids: [7], prototype: "cube");
        var camera = new CameraSnapshot(Position: Vector3.Zero, Right: Vector3.UnitX, Up: Vector3.UnitY, Forward: -Vector3.UnitZ, TanHalfFieldOfView: 1, AspectRatio: 1);
        var hit = new SdfPickResult(Request: 1, X: 1, Y: 1, Width: 4, Height: 4, Identity: 0x40000001, Distance: 4, Material: 7,
            Program: new SdfProgramBuilder().Build(), MeshRevision: 0, Map: maps.Snapshot(pool: [])) {
            Normal = Vector3.UnitZ,
            Sample = new SdfReprojectionView(Camera: camera, Jitter: Vector2.Zero, Width: 4, Height: 4),
        };
        var snapshot = new WorldInspectorSnapshot {
            Camera = camera,
            Pick = hit,
            ReloadError = "source.puck:3: invalid row",
            Selection = "crate",
            View = new SdfViewSnapshot(Camera: camera, Region: default) {
                RenderScale = 0.75f,
                ResolvedRenderScale = 0.25f,
                Quality = new SdfViewQuality {
                    DisableAmbientOcclusion = true,
                },
            },
        };
        var text = new WorldInspectorText();

        text.Format(snapshot: in snapshot);
        text.Finish();
        var result = new string(value: text.Text);

        Assert.Contains(actualString: result, expectedSubstring: "placement=crate");
        Assert.Contains(actualString: result, expectedSubstring: "material=cube.palette[0] index=7");
        Assert.Contains(actualString: result, expectedSubstring: "reload=source.puck:3: invalid row");
        Assert.Contains(actualString: result, expectedSubstring: "render-scale=0.25 debug=0 shadows=True ao=False");
        for (var index = 0; (index < 100); index++) { text.Format(snapshot: in snapshot); text.Finish(); }
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            for (var index = 0; (index < 100); index++) { text.Format(snapshot: in snapshot); text.Finish(); }
        }));
        var sky = snapshot with { Pick = hit with { Identity = 0 } };

        text.Format(snapshot: in sky);
        text.Finish();
        var missing = new string(value: text.Text);

        Assert.Contains(actualString: missing, expectedSubstring: "hit=none");
        Assert.Contains(actualString: missing, expectedSubstring: "placement=none");
        Assert.Contains(actualString: missing, expectedSubstring: "material=none");
    }
    [Fact]
    public void FormatterDoesNotInventSurfaceValuesForAnIdentityOnlyPick() {
        var hit = new SdfPickResult(Request: 1, X: 1, Y: 1, Width: 4, Height: 4,
            Identity: 0x40000001, Distance: 7, Material: 0,
            Program: new SdfProgramBuilder().Build(), MeshRevision: 0);
        var snapshot = new WorldInspectorSnapshot { Pick = hit, ReloadError = "none" };
        var text = new WorldInspectorText();

        text.Format(snapshot: in snapshot);
        text.Finish();
        var result = new string(value: text.Text);

        Assert.Contains(actualString: result, expectedSubstring: "hit=surface");
        Assert.Contains(actualString: result, expectedSubstring: "point=unavailable normal=unavailable distance=7");
    }
    // Content fits the reservation line by line: a real reload refusal names an absolute path, which shows relative to the
    // world's directory with its file and location first, and a long placement or prototype wraps and elides on its own
    // lines, so neither refuses the panel. Optional lines past the last line are counted, never refused.
    [Fact]
    public void LongDiagnosticsAndNamesFitTheReservationWithTheirFileAndLocationFirst() {
        const string Root = "D:/Source/ByteTerrace/Puck/src/Puck.World/Assets/worlds/avatars";
        var placement = (("courtyard-lantern-" + string.Concat(values: Enumerable.Repeat(count: 12, element: "west-arcade-"))) + "north");
        var prototype = ("lantern-" + new string(c: 'p', count: 180));
        var maps = new WorldPickMapBuilder();

        maps.Instances(first: 0, end: 1, target: new WorldPickTarget(BodyIndex: null, Placement: placement) { Prototype = prototype });
        var hit = new SdfPickResult(Request: 1, X: 1, Y: 1, Width: 4, Height: 4, Identity: 0x40000001, Distance: 4, Material: 0,
            Program: new SdfProgramBuilder().Build(), MeshRevision: 0, Map: maps.Snapshot(pool: []));
        string[] diagnostics = [
            $"[world.reload: {Root}/moth.puck(41,17): error PUCK012: the member 'wingspan' is not a member of prototype 'moth'; the prototype's members are body, wings, antennae, palette and rig]",
            $"[world.reload: cannot read {Root}/moth.puck: The process cannot access the file '{Root}/moth.puck' because it is being used by another process.]",
        ];
        string[] leads = ["reload=moth.puck(41,17): error PUCK012", "reload=moth.puck: cannot read moth.puck: The process"];
        var text = new WorldInspectorText();

        for (var index = 0; (index < diagnostics.Length); index++) {
            var snapshot = new WorldInspectorSnapshot { Pick = hit, ReloadError = diagnostics[index], WorldRoot = Root };

            text.Format(snapshot: in snapshot);
            for (var pass = 0; (pass < 40); pass++) {
                text.Timing(node: "world", timing: new Puck.Abstractions.Gpu.GpuPassTiming(Milliseconds: 0.25, Pass: $"sdf.world$pass{pass}", Samples: 32));
            }
            text.Finish();
            var result = new string(value: text.Text);
            var lines = result.Split(separator: '\n');

            Assert.InRange(actual: lines.Length, high: InspectorWriter.MaxLines, low: 1);
            Assert.All(collection: lines, action: static line => Assert.InRange(actual: line.Length, high: InspectorWriter.MaxLineChars, low: 0));
            Assert.Contains(collection: lines, filter: line => line.StartsWith(value: leads[index], comparisonType: StringComparison.Ordinal));
            Assert.DoesNotContain(actualString: result, expectedSubstring: "D:/Source");
            Assert.StartsWith(expectedStartString: $"placement={placement[..40]}", actualString: lines[1]);
            Assert.Contains(collection: lines, filter: static line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "... ") && line.EndsWith(comparisonType: StringComparison.Ordinal, value: " more lines]")));
            Assert.EndsWith(actualString: result, expectedEndString: "]");
        }

        // Shaping a diagnostic happens once per change, so a steady long panel allocates nothing.
        var steady = new WorldInspectorSnapshot { Pick = hit, ReloadError = diagnostics[0], WorldRoot = Root };

        for (var index = 0; (index < 100); index++) { text.Format(snapshot: in steady); text.Finish(); }
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            for (var index = 0; (index < 100); index++) { text.Format(snapshot: in steady); text.Finish(); }
        }));
    }
}
