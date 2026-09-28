using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Overlays;
using Puck.Testing;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldInspectorLawTests {
    [Fact]
    public void RealInspectorCommandAndPanelShareOneTextWithoutCreatingADevice() {
        using var files = new TemporaryDirectory();
        var builder = WorldBootHarness.Compose(files, WorldHostPresentation.Windowed,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json");
        // Exercise the registered inspector module with its real presentation dependencies. Recording and other
        // window-only commands consume Program's host inputs, outside this device-sealed composition fixture.
        for (var index = (builder.Services.Count - 1); (index >= 0); index--) {
            var descriptor = builder.Services[index];

            if ((descriptor.ServiceType == typeof(ICommandModule)) && (descriptor.ImplementationType?.Name != "WorldInspectionCommandModule")) {
                builder.Services.RemoveAt(index: index);
            }
        }
        using var host = builder.Build();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var source = host.Services.GetRequiredService<IInspectorSource>();

        Assert.True(condition: source.Read(slot: 0, viewport: out _).IsEmpty);
        var shown = registry.Submit(line: "world.inspect on");

        Assert.False(condition: shown.IsError, userMessage: shown.Output);
        Assert.Equal(expected: shown.Output, actual: new string(value: source.Read(slot: 0, viewport: out _)));
        Assert.Contains(expectedSubstring: "hit=none", actualString: shown.Output);
        var read = registry.Submit(line: "world.inspect");

        Assert.Equal(expected: read.Output, actual: new string(value: source.Read(slot: 0, viewport: out _)));
        Assert.False(condition: registry.Submit(line: "world.inspect off").IsError);
        Assert.True(condition: source.Read(slot: 0, viewport: out _).IsEmpty);

        // Supply only a followed presentation view, without creating a GPU. The cursor can follow a pane from a
        // different world than its seat, so inspector and cost must use this exact residency while it is demanded.
        var pipelines = new GpuPassPipelineCache();
        var catalog = new SdfWorldPipelineCatalog(
            regionCopy: new GpuRegionCopyPass(kernel: new byte[] { 1 }, pipelines: pipelines),
            meshRaster: new SdfMeshRasterPass(fragment: new byte[] { 1 }, pipelines: pipelines, vertex: new byte[] { 1 }));
        using var pane = new SdfWorldResidency(pipelines: catalog, frameSource: new EmptyFrameSource(),
            kernels: new SdfKernelSet(bytecode: new ReadOnlyMemory<byte>[SdfKernelSet.Kernels.Count]), name: "pane-world", width: 32, height: 32);
        var picker = new SdfWorldPicker();

        typeof(SdfWorldPicker).GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_view")!.SetValue(
            obj: picker, value: new SdfWorldView(Residency: pane, View: 0));
        var cursorType = source.GetType().Assembly.GetType(name: "Puck.World.WorldCursorFeed", throwOnError: true)!;
        var cursor = host.Services.GetRequiredService(serviceType: cursorType);
        var field = cursorType.GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_hoverPicker")!;

        field.SetValue(obj: cursor, value: picker);
        var residencyOf = source.GetType().GetMethod(name: "ResidencyOf")!;

        Assert.Same(expected: pane, actual: residencyOf.Invoke(obj: source, parameters: [0]));
        Assert.Null(@object: residencyOf.Invoke(obj: source, parameters: [1]));
        field.SetValue(obj: cursor, value: null);
        Assert.Null(@object: residencyOf.Invoke(obj: source, parameters: [0]));
    }
    [Fact]
    public void FormatterNamesCapturedPlacementMaterialAndPixelCostWithoutSteadyAllocation() {
        var maps = new WorldPickMapBuilder();

        maps.Instances(first: 0, end: 1, target: new WorldPickTarget(BodyIndex: null, Placement: "crate") { Prototype = "cube" });
        maps.Materials(ids: [7], prototype: "cube");
        var camera = new CameraSnapshot(Position: Vector3.Zero, Right: Vector3.UnitX, Up: Vector3.UnitY, Forward: -Vector3.UnitZ, TanHalfFieldOfView: 1, AspectRatio: 1);
        var hit = new SdfPickResult(Request: 1, X: 1, Y: 1, Width: 4, Height: 4, Identity: 0x40000001, Distance: 4, Material: 7,
            Program: new SdfProgramBuilder().Build(), MeshRevision: 0, Map: maps.Snapshot(pool: [])) {
            Flags = 23U | (107U << 8),
            Normal = Vector3.UnitZ,
            Sample = new SdfReprojectionView(Camera: camera, Jitter: Vector2.Zero, Width: 4, Height: 4),
        };
        var snapshot = new WorldInspectorSnapshot {
            Camera = camera,
            Pick = hit,
            ReloadError = "source.puck:3: invalid row",
            Selection = "crate",
            View = new SdfViewSnapshot(Camera: camera, Region: default) {
                RenderScale = 0.25f,
                Quality = new SdfViewQuality {
                    DisableAmbientOcclusion = true,
                    DisableFarBound = true,
                    ShadowDistanceScale = 0.25f,
                    UseCameraTileShadowMask = true,
                    UseFastAmbientOcclusion = true,
                    UseFastSoftShadowMarch = true,
                },
            },
        };
        var text = new WorldInspectorText();

        text.Format(snapshot: in snapshot);
        text.Finish();
        var result = new string(value: text.Text);

        Assert.Contains(actualString: result, expectedSubstring: "placement=crate");
        Assert.Contains(actualString: result, expectedSubstring: "material=cube.palette[0] index=7");
        Assert.Contains(actualString: result, expectedSubstring: "steps=23 queries=107");
        Assert.Contains(actualString: result, expectedSubstring: "reload=source.puck:3: invalid row");
        Assert.Contains(actualString: result, expectedSubstring: "render-scale=0.25 debug=0 shadows=0.25 ao=False");
        Assert.Contains(actualString: result, expectedSubstring: "fast-shadow=True tile-mask=True fast-ao=True");
        Assert.Contains(actualString: result, expectedSubstring: "far-bound=False");
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
    [InlineData(97)]
    [InlineData(4000)]
    [Theory]
    public void InspectorRefusesTextOverflowByWriterNameInsteadOfTruncating(int length) {
        var text = new WorldInspectorText();

        text.Format(snapshot: new WorldInspectorSnapshot { ReloadError = new string(c: 'x', count: length) });
        text.Finish();
        Assert.True(condition: text.Refused);
        Assert.Contains(expectedSubstring: "editor refused", actualString: new string(value: text.Text));
        text.Format(snapshot: new WorldInspectorSnapshot { ReloadError = "none" });
        text.Finish();
        Assert.False(condition: text.Refused);
        Assert.Contains(expectedSubstring: "hit=none", actualString: new string(value: text.Text));
    }

    private sealed class EmptyFrameSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            new(Program: new SdfProgramBuilder().Build(), ProgramChanged: false, Views: [], Time: 0);
    }
}
