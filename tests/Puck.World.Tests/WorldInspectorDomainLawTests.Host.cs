using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldInspectorDomainLawTests {
    [Fact]
    public void ActualInspectorTickAndCommandShowOnlyTheFollowedOwnersActiveDiagnosticsWithoutAllocation() {
        using var files = new TemporaryDirectory();
        var builder = WorldBootHarness.Compose(files, WorldHostPresentation.Windowed,
            "tests/Puck.World.Canaries/editor-grid/fixture.world.json");

        for (var index = (builder.Services.Count - 1); (index >= 0); index--) {
            var descriptor = builder.Services[index];

            if ((descriptor.ServiceType == typeof(ICommandModule)) && (descriptor.ImplementationType?.Name != "WorldInspectionCommandModule")) {
                builder.Services.RemoveAt(index: index);
            }
        }
        using var host = builder.Build();
        var registry = host.Services.GetRequiredService<CommandRegistry>();
        var source = host.Services.GetRequiredService<IInspectorSource>();
        var probe = host.Services.GetRequiredService<IWorldEngineReadiness>();
        var environment = new WorldEnvironmentResolve();

        _ = environment.Domains.OrderedPair(field: "pane.curvature", high: 1, initialHigh: 1, initialLow: 0, low: 2, source: "state.bounds");
        var expected = Assert.Single(collection: environment.Domains.Diagnostics).ToString();
        var pipelines = new GpuPassPipelineCache();
        var catalog = new SdfWorldPipelineCatalog(
            regionCopy: new GpuRegionCopyPass(kernel: new byte[] { 1 }, pipelines: pipelines),
            meshRaster: new SdfMeshRasterPass(fragment: new byte[] { 1 }, pipelines: pipelines, vertex: new byte[] { 1 }));
        using var pane = Residency(catalog: catalog, name: "pane");

        probe.GetType().GetMethod(name: "RegisterView")!.Invoke(obj: probe, parameters: ["pane", pane.Work, pane.WorkLifetime, environment]);
        // Reuse the inspector's device-free followed-pane fixture: no GPU readback or authoritative state is changed.
        var picker = new SdfWorldPicker();

        typeof(SdfWorldPicker).GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_view")!.SetValue(
            obj: picker, value: new SdfWorldView(Residency: pane, View: 0));
        var cursorType = source.GetType().Assembly.GetType("Puck.World.WorldCursorFeed", throwOnError: true)!;
        var cursor = host.Services.GetRequiredService(serviceType: cursorType);

        cursorType.GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_hoverPicker")!.SetValue(obj: cursor, value: picker);
        var tick = source.GetType().GetMethod(name: "Tick")!.CreateDelegate<Action>(target: source);

        Assert.True(condition: source.Read(slot: 0, viewport: out _).IsEmpty);
        var shown = registry.Submit(line: "world.inspect on");

        Assert.False(condition: shown.IsError, userMessage: shown.Output);
        Assert.Contains(expected, shown.Output.Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: "\n"));
        Assert.Equal(shown.Output, new string(value: source.Read(slot: 0, viewport: out _)));
        for (var index = 0; (index < 100); index++) { tick(); }
        Assert.Equal(0L, AllocationWindow.Least(() => { for (var index = 0; (index < 100); index++) { tick(); } }));
        _ = environment.Domains.OrderedPair(field: "pane.curvature", high: 1, initialHigh: 1, initialLow: 0, low: 0, source: "state.bounds");
        tick();
        Assert.DoesNotContain("pane.curvature", new string(value: source.Read(slot: 0, viewport: out _)));
        _ = environment.Domains.OrderedPair(field: "pane.curvature", high: 1, initialHigh: 1, initialLow: 0, low: 2, source: "state.bounds");
        tick();
        Assert.Contains(expected, new string(value: source.Read(slot: 0, viewport: out _)).Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: "\n"));
        probe.GetType().GetMethod(name: "UnregisterView")!.Invoke(obj: probe, parameters: ["pane"]);
        tick();
        Assert.DoesNotContain("pane.curvature", new string(value: source.Read(slot: 0, viewport: out _)));
        Assert.False(condition: registry.Submit(line: "world.inspect off").IsError);
        tick();
        Assert.True(condition: source.Read(slot: 0, viewport: out _).IsEmpty);
    }
}
