using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Commands;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>world.counters</c> reports every <see cref="IWorkCounterSource"/> registered in the World's
/// container — one it has never heard of included — as one section per source sorted by name, filters by whole dotted
/// segments, prints the same facts as one line of JSON under <c>--json</c> with a legend of every kind's unit and class,
/// folds the render nodes of an <see cref="IGpuWorkRegistry"/> into its <c>gpu</c> section, and reports in its
/// <c>allocation</c> section whether reading every count allocates.
/// </summary>
public sealed class WorldCountersCommandModuleLawTests {
    private static readonly WorkKind Loads = new(name: "world.boot.loads", unit: "count", workClass: WorkClass.Deterministic);
    private static readonly WorkKind Parses = new(name: "world.boot.parses", unit: "count", workClass: WorkClass.Deterministic);
    private static readonly WorkKind Visits = new(name: "state.arena.visits", unit: "lanes", workClass: WorkClass.Deterministic);
    private static readonly string Allocation = $"allocation gc=\"{AllocationWindow.GcMode}\"\nworld.counters.read 0\n";
    private static readonly string AllocationJson = (("\"allocation\":{\"gcMode\":\"" + AllocationWindow.GcMode) + "\",\"windows\":{\"world.counters.read\":0}}");
    private static readonly string GpuLegend = string.Join(
        separator: ",",
        values: GpuWork.SubmissionKinds.ToArray().Concat(second: GpuWork.LifetimeKinds.ToArray()).Select(selector: static kind => (((((("\"" + kind.Name) + "\":{\"unit\":\"") + kind.Unit) + "\",\"class\":\"") + ((kind.Class == WorkClass.Deterministic) ? "deterministic" : "per-backend-deterministic")) + "\"}"))
    );

    private sealed class FakeSource(string name, (WorkKind Kind, long Value)[] counts, bool allocatesOnRead = false) : IWorkCounterSource {
        private readonly WorkKind[] m_kinds = [.. counts.Select(selector: static count => count.Kind)];

        public object? LastRead { get; private set; }
        public string Name => name;
        public ReadOnlySpan<WorkKind> WorkKinds => m_kinds;

        public bool TryRead(WorkKind kind, out long value) {
            if (allocatesOnRead) {
                LastRead = new object();
            }

            foreach (var count in counts) {
                if (ReferenceEquals(objA: count.Kind, objB: kind)) {
                    value = count.Value;

                    return true;
                }
            }

            value = 0L;

            return false;
        }
    }
    private sealed class FakeRegistry(GpuDeviceIdentity? identity, params GpuWorkNode[] registered) : IGpuWorkRegistry {
        public GpuDeviceCapabilities? DeviceCapabilities { get; init; }
        public GpuDeviceIdentity? DeviceIdentity => identity;

        public void CopyNodes(List<GpuWorkNode> nodes) =>
            nodes.AddRange(collection: registered);
    }

    private static CommandResult Run(string line, IGpuWorkRegistry? gpu = null, bool allocatingSource = false) {
        var services = new ServiceCollection();

        // Registered after the verb, as a builder composing its own source later would: discovery is by
        // registration, not by order or name.
        _ = services.AddWorldCounters();
        _ = services.AddSingleton<IWorkCounterSource>(implementationInstance: new FakeSource(
            counts: [(Loads, 3L), (Parses, 7L)],
            name: "world.boot"
        ));
        _ = services.AddSingleton<IWorkCounterSource>(implementationInstance: new FakeSource(
            counts: [(Visits, 12L)],
            name: "state.arena"
        ));

        if (allocatingSource) {
            _ = services.AddSingleton<IWorkCounterSource>(implementationInstance: new FakeSource(
                allocatesOnRead: true,
                counts: [(Visits, 1L)],
                name: "test.allocating"
            ));
        }

        if (gpu is not null) {
            _ = services.AddSingleton(implementationInstance: gpu);
        }

        using var provider = services.BuildServiceProvider();

        return new CommandRegistry(modules: provider.GetServices<ICommandModule>()).Submit(line: line);
    }

    [Fact]
    public void EveryRegisteredSourceIsASectionSortedByName() {
        var result = Run(line: "world.counters");

        Assert.False(condition: result.IsError);
        Assert.Equal(
            expected: $"[world.counters: {Allocation}state.arena\nstate.arena.visits 12\nworld.boot\nworld.boot.loads 3\nworld.boot.parses 7\n]",
            actual: result.Output
        );
    }
    [Fact]
    public void AFilterSelectsWholeSegments() {
        Assert.Equal(
            expected: "[world.counters: world.boot\nworld.boot.loads 3\nworld.boot.parses 7\n]",
            actual: Run(line: "world.counters world").Output
        );
        Assert.Equal(
            expected: "[world.counters: world.boot\nworld.boot.loads 3\nworld.boot.parses 7\n]",
            actual: Run(line: "world.counters world.boot").Output
        );

        var partial = Run(line: "world.counters wor");

        Assert.True(condition: partial.IsError);
        Assert.Equal(
            expected: "[world.counters: no source is named 'wor' or sits under it — sources: allocation, state.arena, world.boot]",
            actual: partial.Output
        );
    }
    [Fact]
    public void AHostWithoutARendererHasNoGpuSection() {
        var result = Run(line: "world.counters gpu");

        Assert.True(condition: result.IsError);
        Assert.Equal(
            expected: "[world.counters: no source is named 'gpu' or sits under it — sources: allocation, state.arena, world.boot]",
            actual: result.Output
        );
    }
    [Fact]
    public void JsonCarriesTheSameCountsAndALegendOfEveryKind() =>
        Assert.Equal(
            expected: (("""[world.counters: {"sources":[{"name":"state.arena","counts":{"state.arena.visits":12}},{"name":"world.boot","counts":{"world.boot.loads":3,"world.boot.parses":7}}],""" + AllocationJson) + ""","kinds":{"state.arena.visits":{"unit":"lanes","class":"deterministic"},"world.boot.loads":{"unit":"count","class":"deterministic"},"world.boot.parses":{"unit":"count","class":"deterministic"}}}]"""),
            actual: Run(line: "world.counters --json").Output
        );
    [Fact]
    public void TheAllocationSectionSaysWhetherReadingEveryCountAllocates() {
        Assert.Equal(
            expected: $"[world.counters: {Allocation}]",
            actual: Run(line: "world.counters allocation").Output
        );

        // A source whose read allocates turns the reading non-zero: the window really measures the reads.
        var allocating = Run(
            allocatingSource: true,
            line: "world.counters allocation"
        ).Output;

        Assert.StartsWith(expectedStartString: $"[world.counters: allocation gc=\"{AllocationWindow.GcMode}\"\nworld.counters.read ", actualString: allocating);
        Assert.DoesNotContain(actualString: allocating, expectedSubstring: "world.counters.read 0\n");
    }
    [Fact]
    public void TheGpuSectionReportsEveryRegisteredNodeInItsSortedPlace() {
        var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );
        var registry = new FakeRegistry(
            null,
            new GpuWorkNode(
                Lifetime: ledger,
                Name: "world",
                OwnedBytes: 2048UL,
                Work: ledger
            ),
            new GpuWorkNode(
                Lifetime: null,
                Name: "overlay",
                Work: ledger
            )
        );
        const string Lifetime = "work lifetime: created.pipelines=0 created.shader-modules=0 created.images=0 created.buffers=0 created.descriptor-pools=0 created.descriptor-sets=0 created.timestamp-pools=0\n";

        Assert.Equal(
            expected: $"[world.counters: {Allocation}gpu device unavailable\nnode world work unavailable\n{Lifetime}owned-bytes=2048\nnode overlay work unavailable\nstate.arena\nstate.arena.visits 12\nworld.boot\nworld.boot.loads 3\nworld.boot.parses 7\n]",
            actual: Run(
                gpu: registry,
                line: "world.counters"
            ).Output
        );
        Assert.Equal(
            expected: $"[world.counters: gpu device unavailable\nnode world work unavailable\n{Lifetime}owned-bytes=2048\nnode overlay work unavailable\n]",
            actual: Run(
                gpu: registry,
                line: "world.counters gpu"
            ).Output
        );
        Assert.Equal(
            expected: (("""[world.counters: {"sources":[],"gpu":{"device":null,"capabilities":null,"nodes":[{"name":"world","sample":null,"lifetime":{"gpu.created.pipelines":0,"gpu.created.shader-modules":0,"gpu.created.images":0,"gpu.created.buffers":0,"gpu.created.descriptor-pools":0,"gpu.created.descriptor-sets":0,"gpu.created.timestamp-pools":0},"owned-bytes":2048},{"name":"overlay","sample":null,"lifetime":null,"owned-bytes":null}]},"kinds":{""" + GpuLegend) + "}}]"),
            actual: Run(
                gpu: registry,
                line: "world.counters gpu --json"
            ).Output
        );
    }
    [Fact]
    public void ARegistryWithNoNodeYetSaysSo() =>
        Assert.Equal(
            expected: "[world.counters: gpu device unavailable\nnodes none: the renderer is not built yet\n]",
            actual: Run(
                gpu: new FakeRegistry(identity: null),
                line: "world.counters gpu"
            ).Output
        );
    [Fact]
    public void TheGpuHeaderNamesTheDevice() {
        var identity = new GpuDeviceIdentity(
            AdapterName: "Example GPU",
            ApiVersion: "1.4.303",
            Backend: "vulkan",
            DeviceId: 0x2786U,
            DriverVersion: "566.36",
            DriverVersionRaw: 0x8D8D8000UL,
            VendorId: 0x10DEU
        );
        var registry = new FakeRegistry(identity: identity);

        Assert.Equal(
            expected: "[world.counters: gpu backend=vulkan adapter=\"Example GPU\" vendor=0x10de device=0x2786 driver=566.36 driver.raw=0x8d8d8000 api=1.4.303\nnodes none: the renderer is not built yet\n]",
            actual: Run(
                gpu: registry,
                line: "world.counters gpu"
            ).Output
        );
        Assert.Equal(
            expected: (("""[world.counters: {"sources":[],"gpu":{"device":{"backend":"vulkan","adapter":"Example GPU","vendor":4318,"device":10118,"driver":"566.36","driver.raw":2374860800,"api":"1.4.303","driver.name":"","driver.id":0,"conformance":"","pipeline-cache.uuid":""},"capabilities":null,"nodes":[]},"kinds":{""" + GpuLegend) + "}}]"),
            actual: Run(
                gpu: registry,
                line: "world.counters gpu --json"
            ).Output
        );
    }
    [Fact]
    public void TheGpuHeaderNamesWhatTheDeviceCanBind() {
        var identity = new GpuDeviceIdentity(
            AdapterName: "Example GPU",
            ApiVersion: "12_2",
            Backend: "directx",
            DeviceId: 0x2786U,
            DriverVersion: "32.0.15.6636",
            DriverVersionRaw: 1UL,
            VendorId: 0x10DEU
        );
        var registry = new FakeRegistry(identity: identity) {
            DeviceCapabilities = GpuDeviceCapabilities.FromDirectX(
                resourceBindingTier: 3U,
                rootSignatureVersion: "1.1",
                samplerHeapSize: 0U,
                shaderModel: "6.8",
                staticSamplerHeapSize: 0U,
                viewHeapSize: 0U
            ),
        };

        Assert.Equal(
            expected: "[world.counters: gpu backend=directx adapter=\"Example GPU\" vendor=0x10de device=0x2786 driver=32.0.15.6636 driver.raw=0x1 api=12_2\ncapabilities backend=directx root-signature-words=64 push-constant-bytes=256 stage.samplers=2048 stage.uniform-buffers=1000000 stage.storage-buffers=1000000 stage.sampled-images=1000000 stage.storage-images=1000000 binding-tier=3 root-signature=1.1 shader-model=6.8 heap.views=1000000 heap.samplers=2048 heap.samplers-static=2048\nnodes none: the renderer is not built yet\n]",
            actual: Run(
                gpu: registry,
                line: "world.counters gpu"
            ).Output
        );
        Assert.Equal(
            expected: (("""[world.counters: {"sources":[],"gpu":{"device":{"backend":"directx","adapter":"Example GPU","vendor":4318,"device":10118,"driver":"32.0.15.6636","driver.raw":1,"api":"12_2","driver.name":"","driver.id":0,"conformance":"","pipeline-cache.uuid":""},"capabilities":{"backend":"directx","descriptor-sets":0,"root-signature-words":64,"push-constant-bytes":256,"stage.samplers":2048,"stage.uniform-buffers":1000000,"stage.storage-buffers":1000000,"stage.sampled-images":1000000,"stage.storage-images":1000000,"stage.resources":0,"binding-tier":3,"root-signature":"1.1","shader-model":"6.8","heap.views":1000000,"heap.samplers":2048,"heap.samplers-static":2048},"nodes":[]},"kinds":{""" + GpuLegend) + "}}]"),
            actual: Run(
                gpu: registry,
                line: "world.counters gpu --json"
            ).Output
        );
    }
    [Fact]
    public void AVulkanDeviceNamesItsSetsAndStageLimits() {
        var registry = new FakeRegistry(identity: null) {
            DeviceCapabilities = new GpuDeviceCapabilities(
                Backend: "vulkan",
                MaxBoundDescriptorSets: 32U,
                MaxPerStageResources: 4294967295U,
                MaxPerStageSampledImages: 1048576U,
                MaxPerStageSamplers: 1048576U,
                MaxPerStageStorageBuffers: 1048576U,
                MaxPerStageStorageImages: 1048576U,
                MaxPerStageUniformBuffers: 1048576U,
                MaxPushConstantBytes: 256U,
                MaxRootSignatureWords: 0U
            ),
        };

        Assert.Equal(
            expected: "[world.counters: gpu device unavailable\ncapabilities backend=vulkan descriptor-sets=32 push-constant-bytes=256 stage.samplers=1048576 stage.uniform-buffers=1048576 stage.storage-buffers=1048576 stage.sampled-images=1048576 stage.storage-images=1048576 stage.resources=4294967295\nnodes none: the renderer is not built yet\n]",
            actual: Run(
                gpu: registry,
                line: "world.counters gpu"
            ).Output
        );
    }
    [Fact]
    public void TheLegendNamesEveryKindANodeReports_ARoutedEndpointsRowIncluded() {
        var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );
        var registry = new FakeRegistry(
            null,
            new GpuWorkNode(
                Lifetime: new WorldRoutedResidencyCounts(
                    lifetime: ledger,
                    residencies: static () => (1L, 0L),
                    tables: static () => new GpuMemoryBytes(DeviceLocal: 4096UL, HostVisible: 512UL)
                ),
                Name: "sdf:routed$north",
                Work: ledger
            )
        );
        var output = Run(
            gpu: registry,
            line: "world.counters gpu --json"
        ).Output;

        Assert.Contains(
            actualString: output,
            expectedSubstring: "\"world.routed.residencies.created\":1,\"world.routed.residencies.released\":0,\"world.routed.tables.device-local.allocated\":4096,\"world.routed.tables.device-local.released\":0,\"world.routed.tables.host-visible.allocated\":512,\"world.routed.tables.host-visible.released\":0"
        );

        foreach (var kind in WorldRoutedResidencyCounts.Kinds) {
            Assert.Contains(
                expectedSubstring: $"\"{kind.Name}\":{{\"unit\":",
                actualString: output
            );
        }
    }

    // One completed submission under three passes: mesh drew twice (30 and 12 vertices), views dispatched twice, sky
    // skipped.
    private static GpuWorkLedger MeshLedger() {
        var device = new Puck.Testing.FakeGpuDevice(holdFences: true);
        var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );
        var services = GpuWorkCounting.Wrap(
            ledger: ledger,
            services: device.Services
        );

        ledger.Configure(
            passClasses: [WorkClass.Deterministic, WorkClass.Deterministic, WorkClass.Deterministic],
            passLabels: ["sdf.world$mesh", "sdf.world$views", "sdf.world$sky"],
            revision: 1L
        );
        ledger.EnterPass(pass: 0);
        services.Recorder.DrawIndexed(commandBufferHandle: 2, indexCount: 30U);
        services.Recorder.DrawIndexed(commandBufferHandle: 2, indexCount: 12U);
        ledger.LeavePass();
        ledger.EnterPass(pass: 1);
        services.Recorder.Dispatch(commandBufferHandle: 2, groupCountX: 1U, groupCountY: 1U, groupCountZ: 1U);
        services.Recorder.Dispatch(commandBufferHandle: 2, groupCountX: 1U, groupCountY: 1U, groupCountZ: 1U);
        ledger.LeavePass();
        ledger.SkipPass(pass: 2);
        services.QueueSubmitter.SubmitAndWait(commandBufferHandles: []);

        return ledger;
    }
    private static FakeRegistry MeshRegistry(int sessions = 2) {
        var ledger = MeshLedger();
        var nodes = new List<GpuWorkNode> { new(Lifetime: ledger, Name: "sdf:world", Work: ledger) };

        for (var index = 0; (index < sessions); index++) {
            nodes.Add(item: new GpuWorkNode(Lifetime: null, Name: $"session${index}", Work: ledger));
        }

        nodes.Add(item: new GpuWorkNode(Lifetime: null, Name: "session$0$24", Work: ledger));

        return new FakeRegistry(null, [.. nodes]);
    }

    [Fact]
    public void ASummaryIsOneLinePerNode() {
        var output = Run(
            gpu: MeshRegistry(sessions: 1),
            line: "world.counters gpu summary"
        );

        Assert.False(condition: output.IsError);
        Assert.Equal(
            expected: "[world.counters: gpu device unavailable\nnode sdf:world draws=2 vertices=42 texels=0 dispatches=2\nnode session$0 draws=2 vertices=42 texels=0 dispatches=2\nnode session$0$24 draws=2 vertices=42 texels=0 dispatches=2\n]",
            actual: output.Output
        );
        Assert.Equal(
            expected: "[world.counters: gpu device unavailable\nnode sdf:world draws=2 vertices=42 texels=0 dispatches=0\n]",
            actual: Run(
                gpu: MeshRegistry(sessions: 1),
                line: "world.counters node sdf:world pass sdf.world$mesh summary"
            ).Output
        );
        Assert.Contains(
            expectedSubstring: "{\"name\":\"sdf:world\",\"summary\":{\"draws\":2,\"vertices\":42,\"texels\":0,\"dispatches\":2}}",
            actualString: Run(
                gpu: MeshRegistry(sessions: 1),
                line: "world.counters summary --json"
            ).Output
        );
    }
    [Fact]
    public void ANodeFilterKeepsTheNodeAndTheNodesUnderIt() {
        var registry = MeshRegistry(sessions: 2);

        Assert.Equal(
            expected: "[world.counters: gpu device unavailable\nnode session$0 draws=2 vertices=42 texels=0 dispatches=2\nnode session$0$24 draws=2 vertices=42 texels=0 dispatches=2\n]",
            actual: Run(
                gpu: registry,
                line: "world.counters node session$0 summary"
            ).Output
        );

        var exact = Run(
            gpu: registry,
            line: "world.counters gpu node sdf:world"
        ).Output;

        Assert.Contains(actualString: exact, expectedSubstring: "node sdf:world work submission=1");
        Assert.DoesNotContain(actualString: exact, expectedSubstring: "node session");

        var missing = Run(
            gpu: registry,
            line: "world.counters gpu node sdf.world$mesh"
        );

        Assert.True(condition: missing.IsError);
        Assert.Contains(expectedSubstring: "'sdf.world$mesh' names a pass; use: world.counters gpu pass sdf.world$mesh", actualString: missing.Output);
    }
    [Fact]
    public void APassFilterKeepsThePassWhereverItIsAndDropsTheRest() {
        var output = Run(
            gpu: MeshRegistry(sessions: 0),
            line: "world.counters gpu pass sdf.world$views"
        ).Output;

        Assert.StartsWith(
            actualString: output,
            expectedStartString: "[world.counters: gpu device unavailable\nnode sdf:world work submission=1 revision=1\nwork sdf.world$views executed: dispatches=2 "
        );
        Assert.DoesNotContain(actualString: output, expectedSubstring: "sdf.world$mesh");
        Assert.DoesNotContain(actualString: output, expectedSubstring: "work outside");
        Assert.DoesNotContain(actualString: output, expectedSubstring: "work lifetime");
        // The registry holds two nodes, and the pass appears once in each.
        Assert.Equal(
            expected: 3,
            actual: output.Split(separator: "work sdf.world$views").Length
        );
        // A prefix of whole segments reaches every pass under it.
        Assert.Contains(
            expectedSubstring: "work sdf.world$sky skipped",
            actualString: Run(
                gpu: MeshRegistry(sessions: 0),
                line: "world.counters gpu pass sdf.world"
            ).Output
        );

        var refused = Run(
            gpu: MeshRegistry(sessions: 0),
            line: "world.counters gpu pass shadow"
        );

        Assert.True(condition: refused.IsError);
        Assert.Equal(
            expected: "[world.counters: no gpu pass is named 'shadow' or sits under it in any node]",
            actual: refused.Output
        );
    }
    [Fact]
    public void ACounterFilterKeepsTheCountsUnderThePrefix() {
        var output = Run(
            gpu: MeshRegistry(sessions: 0),
            line: "world.counters gpu node sdf:world counter vertices"
        ).Output;

        Assert.Contains(actualString: output, expectedSubstring: "work sdf.world$mesh executed: vertices=42\n");
        Assert.Contains(actualString: output, expectedSubstring: "work outside: vertices=0\n");
        Assert.DoesNotContain(actualString: output, expectedSubstring: "dispatches");
        Assert.DoesNotContain(actualString: output, expectedSubstring: "owned-bytes");
        Assert.Contains(
            expectedSubstring: "work sdf.world$mesh executed: draws=2 draws.indirect=0\n",
            actualString: Run(
                gpu: MeshRegistry(sessions: 0),
                line: "world.counters gpu counter draws"
            ).Output
        );
        Assert.Contains(
            expectedSubstring: "work lifetime: created.pipelines=0 created.shader-modules=0 created.images=0 created.buffers=0 created.descriptor-pools=0 created.descriptor-sets=0 created.timestamp-pools=0\n",
            actualString: Run(
                gpu: MeshRegistry(sessions: 0),
                line: "world.counters gpu counter created"
            ).Output
        );

        var refused = Run(
            gpu: MeshRegistry(sessions: 0),
            line: "world.counters gpu counter nonsense"
        );

        Assert.True(condition: refused.IsError);
        Assert.StartsWith(expectedStartString: "[world.counters: no gpu count starts with 'nonsense' — counts are dispatches, ", actualString: refused.Output);
        Assert.True(condition: Run(
            gpu: MeshRegistry(sessions: 0),
            line: "world.counters gpu summary counter draws"
        ).IsError);
    }
    [Fact]
    public void JsonNarrowsToTheSelectedNodesPassesAndCounts() {
        var output = Run(
            gpu: MeshRegistry(sessions: 1),
            line: "world.counters node sdf:world pass sdf.world$mesh counter vertices --json"
        ).Output;

        Assert.StartsWith(
            actualString: output,
            expectedStartString: """[world.counters: {"sources":[],"gpu":{"device":null,"capabilities":null,"nodes":[{"name":"sdf:world","sample":{"submission":1,"revision":1,"passes":[{"label":"sdf.world$mesh","class":"deterministic","state":"executed","details":[],"counts":{"gpu.vertices":42}}],"outside":{}},"lifetime":null,"owned-bytes":null}]},"kinds":{"gpu.vertices":{"unit":"count","class":"per-backend-deterministic"}}}]"""
        );
    }
    [Fact]
    public void ATextGpuSectionPastTheBudgetPrintsItsSummary() {
        var full = Run(
            gpu: MeshRegistry(sessions: 4),
            line: "world.counters gpu"
        ).Output;

        Assert.DoesNotContain(actualString: full, expectedSubstring: "summary:");

        var registry = MeshRegistry(sessions: 400);
        var capped = Run(
            gpu: registry,
            line: "world.counters gpu"
        ).Output;

        Assert.Contains(actualString: capped, expectedSubstring: "summary: the lines for these nodes run ");
        Assert.Contains(actualString: capped, expectedSubstring: "node session$399 draws=2 vertices=42 texels=0 dispatches=2\n");
        Assert.DoesNotContain(actualString: capped, expectedSubstring: "work sdf.world$mesh");
        Assert.True(condition: (capped.Length < WorldCountersCommandModule.GpuTextBudget));
        // Narrowed under the budget, the lines come back.
        Assert.Contains(
            expectedSubstring: "work sdf.world$mesh executed:",
            actualString: Run(
                gpu: registry,
                line: "world.counters gpu node session$399"
            ).Output
        );
        // The JSON is never replaced.
        Assert.Contains(
            expectedSubstring: "\"label\":\"sdf.world$mesh\"",
            actualString: Run(
                gpu: registry,
                line: "world.counters gpu --json"
            ).Output
        );
    }
    [Fact]
    public void TheOptionsBelongToTheGpuSection() {
        Assert.True(condition: Run(
            gpu: MeshRegistry(sessions: 0),
            line: "world.counters state summary"
        ).IsError);
        Assert.True(condition: Run(
            gpu: MeshRegistry(sessions: 0),
            line: "world.counters gpu node"
        ).IsError);
        Assert.True(condition: Run(
            gpu: MeshRegistry(sessions: 0),
            line: "world.counters gpu node a node b"
        ).IsError);
    }
    [Fact]
    public void ASecondFilterIsRefused() =>
        Assert.True(condition: Run(line: "world.counters state world").IsError);
}
