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

    private sealed class EmptyFrameSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            new(Program: new SdfProgramBuilder().Build(), ProgramChanged: false, Views: [], Time: 0);
    }
}
