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
                Work: ledger
            ),
            new GpuWorkNode(
                Lifetime: null,
                Name: "overlay",
                Work: ledger
            )
        );
        const string Lifetime = "work lifetime: created.pipelines=0 created.shader-modules=0 created.images=0 created.buffers=0 created.descriptor-pools=0 created.descriptor-sets=0\n";

        Assert.Equal(
            expected: $"[world.counters: {Allocation}gpu device unavailable\nnode world work unavailable\n{Lifetime}node overlay work unavailable\nstate.arena\nstate.arena.visits 12\nworld.boot\nworld.boot.loads 3\nworld.boot.parses 7\n]",
            actual: Run(
                gpu: registry,
                line: "world.counters"
            ).Output
        );
        Assert.Equal(
            expected: $"[world.counters: gpu device unavailable\nnode world work unavailable\n{Lifetime}node overlay work unavailable\n]",
            actual: Run(
                gpu: registry,
                line: "world.counters gpu"
            ).Output
        );
        Assert.Equal(
            expected: (("""[world.counters: {"sources":[],"gpu":{"device":null,"nodes":[{"name":"world","sample":null,"lifetime":{"gpu.created.pipelines":0,"gpu.created.shader-modules":0,"gpu.created.images":0,"gpu.created.buffers":0,"gpu.created.descriptor-pools":0,"gpu.created.descriptor-sets":0}},{"name":"overlay","sample":null,"lifetime":null}]},"kinds":{""" + GpuLegend) + "}}]"),
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
            expected: (("""[world.counters: {"sources":[],"gpu":{"device":{"backend":"vulkan","adapter":"Example GPU","vendor":4318,"device":10118,"driver":"566.36","driver.raw":2374860800,"api":"1.4.303","driver.name":"","driver.id":0,"conformance":"","pipeline-cache.uuid":""},"nodes":[]},"kinds":{""" + GpuLegend) + "}}]"),
            actual: Run(
                gpu: registry,
                line: "world.counters gpu --json"
            ).Output
        );
    }
    [Fact]
    public void ASecondFilterIsRefused() =>
        Assert.True(condition: Run(line: "world.counters state world").IsError);
}
