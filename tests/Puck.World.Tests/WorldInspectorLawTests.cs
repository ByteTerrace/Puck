using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Overlays;
using Puck.Testing;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldInspectorLawTests {
    [InlineData(WorldHostPresentation.Windowed)]
    [InlineData(WorldHostPresentation.Offscreen)]
    [Theory]
    public void RealInspectorCommandAndPanelShareOneTextWithoutCreatingADevice(WorldHostPresentation presentation) {
        using var files = new TemporaryDirectory();
        var builder = WorldBootHarness.Compose(files, presentation,
            ((presentation == WorldHostPresentation.Offscreen)
                ? "tests/Puck.World.Canaries/gi-furnace/fixture.puck"
                : "tests/Puck.World.Canaries/editor-grid/fixture.puck"),
            edit: definition => definition with {
                RenderRaw = definition.Render with { Indirect = new WorldRenderIndirect(Tier: SdfIndirectTier.Off) },
            });
        // Exercise the registered inspector module with its real presentation dependencies. Recording and other
        // window-only commands consume Program's host inputs, outside this device-sealed composition fixture.
        for (var index = (builder.Services.Count - 1); (index >= 0); index--) {
            var descriptor = builder.Services[index];

            if ((descriptor.ServiceType == typeof(ICommandModule)) &&
                (descriptor.ImplementationType?.Name is not ("WorldInspectionCommandModule" or "WorldViewCommandModule"))) {
                builder.Services.RemoveAt(index: index);
            }
        }
        var host = files.Own(owner: builder.Build());
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
            meshRaster: new SdfMeshRasterPass(fragment: new byte[] { 1 }, impostorFragment: new byte[] { 1 }, pipelines: pipelines, vertex: new byte[] { 1 }));
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
        var malformed = registry.Submit(line: "world.explain extra");

        Assert.True(condition: malformed.IsError, userMessage: malformed.Output);
        Assert.Equal("[world.explain: expected no arguments]", malformed.Output);
        var absent = registry.Submit(line: "world.explain");

        Assert.True(condition: absent.IsError, userMessage: absent.Output);
        Assert.Contains("world.explain:", absent.Output);
        Assert.Null(@object: absent.Settlement);
        // Registration/following is earlier than an actual render. A refused or still-building replacement must not
        // leave an explanation's console settlement waiting for a pixel it cannot record.
        cursorType.GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_pointerPicker")!.SetValue(obj: cursor, value: picker);
        cursorType.GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_pointerInstance")!.SetValue(obj: cursor, value: "unrendered-pane");
        cursorType.GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_pointerX")!.SetValue(obj: cursor, value: .5f);
        cursorType.GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_pointerY")!.SetValue(obj: cursor, value: .5f);
        var unrendered = registry.Submit(line: "world.explain");

        Assert.True(condition: unrendered.IsError, userMessage: unrendered.Output);
        Assert.Contains("no rendered pixel", unrendered.Output);
        Assert.Null(@object: unrendered.Settlement);
        Assert.Equal(0, picker.RequestIdentity);
        Assert.False(condition: picker.Pending);
        if (presentation == WorldHostPresentation.Offscreen) { InspectOffscreenDisplay(host.Services, registry); }
    }

    // The neutral device supplies zero readback pixels. This exercises the actual boot renderer, display mapping,
    // console route and fenced request; the real furnace canary judges the physical surface and indirect answer.
    private static void InspectOffscreenDisplay(IServiceProvider services, CommandRegistry registry) {
        using var root = Assert.IsType<RenderGraphRuntimeNode>(@object: WorldRenderRoot.Build(overlay: null, sp: services));
        var probe = services.GetRequiredService<WorldRenderProbe>();
        var views = services.GetRequiredService<WorldViewGraphHost>();
        var device = services.GetRequiredService<IGpuDeviceContext>();
        var host = new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device });
        var index = 0UL;

        void Produce() {
            var frame = new FrameContext(AccumulatorTicks: 0UL, DeltaTicks: 1680UL, ElapsedTicks: (index++ * 1680UL),
                FrameDeltaTicks: 1680UL, Host: host, StepTicks: 1680UL, TargetHeight: 128, TargetWidth: 128);

            _ = root.ProduceFrame(context: frame);
            Assert.NotEqual(FrameCompletion.Refused, root.Runtime.Render.Completion);
            Assert.Null(@object: probe.Residency!.Refusal);
        }
        bool Building() => Enumerable.Range(0, root.Runtime.Instances.Instances.Count)
            .Any(predicate: instance => ((root.Runtime.Producer(instance: instance) is null) && root.Runtime.Node(instance: instance).IsBuildingCandidate));
        TestLiveness.Until(step: () => { Produce(); return probe.Residency!.IsReady; },
            wait: probe.Residency!.WaitPipelineBuilds, reason: () => probe.Residency!.NotReadyReason);
        // PrepareGraph publishes the camera mapping before the root's compositor has necessarily installed. The
        // post-production feed runs only after that root actually renders; a mapping alone cannot establish it.
        TestLiveness.Within(frames: 16, step: () => {
            Produce();
            return (root.Runtime.Render.IsRendered && (views.DisplayView is { } displayed) &&
                (views.Pickers?.HasRenderedResolvedView(instance: displayed.Source.Name) == true));
        },
            building: Building, reason: () => root.Runtime.Render.Reason);
        var display = Assert.IsType<SourceMapping>(@object: views.DisplayView);

        Assert.True(condition: views.Pickers!.HasRenderedResolvedView(instance: display.Source.Name), userMessage: $"The displayed source '{display.Source.Name}' has no current rendered picker binding.");
        Assert.Equal(128, views.DisplayWidth);
        Assert.Equal(128, views.DisplayHeight);
        var pointer = registry.Submit(line: "world.view.pointer 32 96");

        Assert.False(condition: pointer.IsError, userMessage: pointer.Output);
        Produce();
        var positioned = registry.Submit(line: "world.view.pointer");

        Assert.False(condition: positioned.IsError, userMessage: positioned.Output);
        Assert.Contains("position=32,96", positioned.Output);
        var inspection = Assert.Single(collection: services.GetServices<ICommandModule>(),
            predicate: module => (module.GetType().Name == "WorldInspectionCommandModule"));
        var reports = new List<CommandResult>();

        inspection.GetType().GetProperty(name: "Report")!.SetValue(obj: inspection, value: ((Action<CommandResult>)reports.Add));
        var commands = new TextCommandSource(registry);
        var issued = new List<(string Line, CommandResult Result)>();
        var other = new List<string>();
        using var issuer = commands.CreateSession(Principal.Console, onResult: (line, result) => issued.Add(item: (line, result)));
        using var observer = commands.CreateSession(Principal.Console, onResult: (line, _) => other.Add(item: line));

        issuer.Enqueue(line: "world.explain");
        issuer.Enqueue(line: "world.inspect");
        observer.Enqueue(line: "world.inspect");
        commands.Collect();
        var request = Assert.Single(collection: issued).Result;

        Assert.Equal("world.explain", issued[0].Line);
        Assert.Equal("world.inspect", Assert.Single(collection: other));
        Assert.False(condition: request.IsError, userMessage: request.Output);
        var settlement = Assert.IsType<CommandSettlement>(@object: request.Settlement);
        var picker = Assert.IsType<SdfWorldPicker>(@object: views.FindPicker(instance: display.Source.Name));

        Assert.True(condition: (picker.Pending || picker.InFlight));
        TestLiveness.Within(frames: 16, step: () => { Produce(); return settlement.IsSettled; },
            building: Building, reason: () => root.Runtime.Render.Reason);
        var result = CommandResult.Settling(settlement);

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.Contains("pixel=32,96/128,128", result.Output);
        Assert.Equal(result, Assert.Single(collection: reports));
        commands.Collect();
        Assert.Equal(new[] { "world.explain", "world.inspect" }, issued.Select(selector: item => item.Line));

        // The same issuing-session barrier and late sink also own a route cancellation, rather than leaving a
        // pending line silent or letting its next line overtake the named refusal.
        issued.Clear();
        reports.Clear();
        issuer.Enqueue(line: "world.explain");
        issuer.Enqueue(line: "world.inspect");
        commands.Collect();
        var cancelled = Assert.IsType<CommandSettlement>(@object: Assert.Single(collection: issued).Result.Settlement);

        Assert.False(condition: registry.Submit(line: "world.view.pointer clear").IsError);
        Produce();
        Assert.True(condition: cancelled.IsSettled);
        var refusal = Assert.Single(collection: reports);

        Assert.True(condition: refusal.IsError, userMessage: refusal.Output);
        Assert.Contains("acting seat or pane changed", refusal.Output);
        commands.Collect();
        Assert.Equal(new[] { "world.explain", "world.inspect" }, issued.Select(selector: item => item.Line));
        Assert.True(condition: registry.Submit(line: "world.explain").IsError);
        Assert.Single(collection: reports);
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
                RenderScale = 0.75f,
                ResolvedRenderScale = 0.25f,
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

    private sealed class EmptyFrameSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            new(Program: new SdfProgramBuilder().Build(), ProgramChanged: false, Views: [], Time: 0);
    }
}
