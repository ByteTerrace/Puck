using System.Collections.Concurrent;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfWorldPipelines"/> over <see cref="FakeGpuDevice"/>: a set leases one pass-pipeline cache entry
/// per engine kernel (the brick baker only when asked for and present), which the cache creates and counts once and
/// persists the device's cache once for; a reload leases only the pipelines whose bytecode changed; the cache holds at
/// most <see cref="GpuPassPipelineCache.BuildConcurrency"/> creations in the driver however many entries build; a set
/// disposed while creations are in the driver waits for those alone and creates no more; and creations failing together
/// are named in the set's order, with everything created released.
/// </summary>
public sealed class SdfWorldPipelinesLawTests {
    [Fact]
    public async Task ASetLeasesEveryEnginePipelineAndAReloadOnlyTheChangedOnes() {
        var device = new PersistingDevice(services: new FakeGpuDevice().Services);
        var cache = new GpuPassPipelineCache();

        using var pipelines = SdfTestPipelines.Build(
            cache: cache,
            device: device,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        );

        Assert.Equal(expected: (11L, 11), actual: (Created(cache: cache), device.Persisted));

        using var reflector = SdfTestPipelines.Reflector();

        using (var unchanged = pipelines.PrepareReload(
            cache: cache,
            device: device,
            kernels: SdfTestPipelines.Kernels(beam: 1),
            reflector: reflector
        )) {
            Assert.Equal(expected: 0, actual: unchanged.ChangedPipelines);
        }

        using (var changed = pipelines.PrepareReload(
            cache: cache,
            device: device,
            kernels: SdfTestPipelines.Kernels(beam: 2),
            reflector: reflector
        )) {
            await changed.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: 1, actual: changed.ChangedPipelines);
        }

        Assert.Equal(expected: (12L, 12), actual: (Created(cache: cache), device.Persisted));

        // A second set of the same kernels on the device joins every entry the first leases.
        using var joined = SdfTestPipelines.Build(
            cache: cache,
            device: device,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        );

        Assert.Equal(expected: 12L, actual: Created(cache: cache));
    }
    // A reload is held to the host's interface before it leases anything: a kernel compiled against another instruction
    // set (its pass block carries another stamp), one binding the program words and the frame's instance grid in each
    // other's places, and one binding the cull bounds and the views' dispatch arguments, two buffers of one shape, in each
    // other's places, each refuse the reload by name, and the set keeps its kernels and creates nothing; the same kernels
    // compiled as the host was prepare.
    [Fact]
    public async Task AReloadWhoseKernelsDoNotReadTheHostsInterfaceIsRefusedAndTheSetKeepsItsKernels() {
        var gpu = new FakeGpuDevice();
        var cache = new GpuPassPipelineCache();
        using var pipelines = SdfTestPipelines.Build(
            cache: cache,
            device: gpu,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        );
        using var reflector = SdfTestPipelines.Reflector();
        var installed = pipelines.Kernels;
        var created = Created(cache: cache);
        var changed = SdfTestPipelines.Kernels(beam: 2);
        var foreign = changed.With(
            bytecode: SpirvEdits.Renamed(
                from: ("passGroup" + SdfWorldInterfaces.Stamp),
                module: changed[SdfKernel.Beam].Span,
                to: ("passGroup" + SdfIsaHlsl.StampOf(fingerprint: SdfIsaFingerprint.Value ^ 1U))
            ),
            kernel: SdfKernel.Beam
        );
        var swapped = changed.With(
            bytecode: SpirvEdits.BindingsSwapped(
                first: SdfWorldPackage.ProgramWords,
                module: changed[SdfKernel.InstanceCull].Span,
                second: SdfWorldPackage.FrameInstanceGrid
            ),
            kernel: SdfKernel.InstanceCull
        );

        var sameShaped = changed.With(
            bytecode: SpirvEdits.BindingsSwapped(
                first: SdfWorldPackage.CullBoundsWritten,
                module: changed[SdfKernel.CullArgs].Span,
                second: SdfWorldPackage.ViewsArgsWritten
            ),
            kernel: SdfKernel.CullArgs
        );

        foreach (var (kernels, stem, reason) in ((ReadOnlySpan<(SdfKernelSet, string, string)>)[
            (foreign, "sdf-beam", "stamped"),
            (swapped, "sdf-instance-cull", SdfWorldPackage.ProgramWords),
            (sameShaped, "sdf-cull-args", SdfWorldPackage.CullBoundsWritten),
        ])) {
            var refusal = Assert.Throws<InvalidOperationException>(testCode: () => pipelines.PrepareReload(
                cache: cache,
                device: gpu,
                kernels: kernels,
                reflector: reflector
            ));

            Assert.Contains(actualString: refusal.Message, expectedSubstring: $"'{stem}': ");
            Assert.Contains(actualString: refusal.Message, expectedSubstring: reason);
            Assert.Same(expected: installed, actual: pipelines.Kernels);
            Assert.Equal(expected: created, actual: Created(cache: cache));
        }

        using var reload = pipelines.PrepareReload(
            cache: cache,
            device: gpu,
            kernels: changed,
            reflector: reflector
        );

        await reload.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected: 1, actual: reload.ChangedPipelines);
    }
    [Fact]
    public void ASetWithABrickPoolAddsTheBrickBakePipelineItsKernelCarries() {
        var gpu = new FakeGpuDevice();
        var cache = new GpuPassPipelineCache();

        using var pipelines = SdfTestPipelines.Build(
            cache: cache,
            device: gpu,
            includeBrickPipelines: true,
            kernels: SdfTestPipelines.Kernels(beam: 1).With(bytecode: new byte[] { 1 }, kernel: SdfKernel.BrickBake)
        );

        Assert.True(condition: pipelines.IncludesBrickPipelines);
        Assert.Equal(expected: 12L, actual: Created(cache: cache));
    }
    [Fact]
    public async Task TheCacheHoldsAtMostItsConcurrencyInTheDriverAndBuildsEveryPipeline() {
        using var driver = new SteppedDriver();
        var gpu = new FakeGpuDevice() {
            BeforeComputePipeline = driver.Enter,
        };
        var cache = new GpuPassPipelineCache();
        using var pipelines = SdfWorldPipelines.Acquire(
            cache: cache,
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        );
        var started = new List<string>();

        // The first creations enter together; after that each creation let through lets exactly one more start.
        for (var creation = 0; (creation < GpuPassPipelineCache.BuildConcurrency); creation++) {
            started.Add(item: driver.Next());
        }

        while (started.Count < 11) {
            driver.Step();
            started.Add(item: driver.Next());
        }

        driver.Open();
        await pipelines.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            actual: (driver.MostInDriver, started.Distinct().Count(), Created(cache: cache)),
            expected: (GpuPassPipelineCache.BuildConcurrency, 11, 11L)
        );
    }
    [Fact]
    public async Task ASetDisposedWhilePipelinesAreInTheDriverWaitsOnlyForThoseAndCreatesNoMore() {
        using var driver = new SteppedDriver();
        var gpu = new FakeGpuDevice() {
            BeforeComputePipeline = driver.Enter,
        };
        var cache = new GpuPassPipelineCache();
        var pipelines = SdfWorldPipelines.Acquire(
            cache: cache,
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        );

        for (var creation = 0; (creation < GpuPassPipelineCache.BuildConcurrency); creation++) {
            _ = driver.Next();
        }

        // Disposal cancels every build before it waits for any, so only the creations already in the driver finish.
        var disposal = Task.Run(
            action: pipelines.Dispose,
            cancellationToken: TestContext.Current.CancellationToken
        );

        TestLiveness.Until(
            step: () => (cache.SharedPipelines == 0),
            reason: () => $"{cache.SharedPipelines} pipelines are still leased"
        );
        driver.Open();
        await disposal;
        Assert.Equal(
            actual: (driver.Entered, Created(cache: cache)),
            expected: (GpuPassPipelineCache.BuildConcurrency, ((long)GpuPassPipelineCache.BuildConcurrency))
        );
    }
    [Fact]
    public async Task TwoCreationsFailingInTheDriverAtOnceAreBothNamedAndEverythingCreatedIsReleased() {
        Assert.SkipWhen(
            condition: (GpuPassPipelineCache.BuildConcurrency < 2),
            reason: "Two creations are in the driver at once only when the cache's concurrency is at least two."
        );

        // Each failing creation waits in the driver for the other before it throws, so both fail while the set builds.
        using var bothInDriver = new Barrier(participantCount: 2);
        var gpu = new FakeGpuDevice(
            trackObjects: true
        ) {
            BeforeComputePipeline = description => {
                if (description.Name is not ("sdf-beam" or "sdf-world-views")) {
                    return;
                }

                Assert.True(
                    condition: bothInDriver.SignalAndWait(timeout: TestLiveness.Bound),
                    userMessage: $"{description.Name} waited out the liveness bound for the other failing creation to reach the driver."
                );

                throw new InvalidOperationException(message: $"injected failure creating {description.Name}");
            },
        };
        var cache = new GpuPassPipelineCache();
        var pipelines = SdfWorldPipelines.Acquire(
            cache: cache,
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        );
        var failure = await Assert.ThrowsAsync<AggregateException>(testCode: () => pipelines.WaitAsync(cancellationToken: CancellationToken.None));

        pipelines.Dispose();
        Assert.StartsWith(
            actualString: failure.Message,
            expectedStartString: "The SDF pipeline set's build failed creating sdf-beam, sdf-world-views."
        );
        Assert.Equal(
            actual: failure.InnerExceptions.Select(selector: static inner => inner.Message),
            expected: ["injected failure creating sdf-beam", "injected failure creating sdf-world-views"]
        );
        Assert.NotEmpty(collection: gpu.Created);
        Assert.All(
            action: static created => Assert.Equal(
                actual: created.DisposeCount,
                expected: 1
            ),
            collection: gpu.Created
        );
    }

    private static long Created(GpuPassPipelineCache cache) {
        Assert.True(condition: cache.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var value));

        return value;
    }

    // A driver that holds every pipeline creation until the law lets one through (Step) or opens it, reporting each
    // creation's pipeline name as it enters and the most creations it held at once. Once open, a creation passes
    // straight through uncounted in the order but counted in Entered, so an extra creation after a cancel shows.
    private sealed class SteppedDriver : IDisposable {
        private readonly BlockingCollection<string> m_entered = [];
        private readonly ManualResetEventSlim m_open = new(initialState: false);
        private readonly SemaphoreSlim m_permits = new(initialCount: 0);

        private int m_count;
        private int m_inDriver;
        private int m_most;

        public int Entered => Volatile.Read(location: ref m_count);
        public int MostInDriver => Volatile.Read(location: ref m_most);

        public void Dispose() {
            Open();
            m_entered.Dispose();
            m_open.Dispose();
            m_permits.Dispose();
        }
        public void Enter(GpuComputePipelineDescription description) {
            _ = Interlocked.Increment(location: ref m_count);

            if (m_open.IsSet) {
                return;
            }

            var inDriver = Interlocked.Increment(location: ref m_inDriver);
            var most = Volatile.Read(location: ref m_most);

            while ((inDriver > most) && (Interlocked.CompareExchange(
                comparand: most,
                location1: ref m_most,
                value: inDriver
            ) != most)) {
                most = Volatile.Read(location: ref m_most);
            }

            m_entered.Add(item: description.Name);
            m_permits.Wait();
            _ = Interlocked.Decrement(location: ref m_inDriver);
        }
        // Takes the name of the next creation to enter.
        public string Next() {
            Assert.True(condition: m_entered.TryTake(
                cancellationToken: TestContext.Current.CancellationToken,
                item: out var name,
                millisecondsTimeout: ((int)TestLiveness.Bound.TotalMilliseconds)
            ));

            return name!;
        }
        // Lets every held creation through, and every later one pass without waiting.
        public void Open() {
            m_open.Set();
            _ = m_permits.Release(releaseCount: GpuPassPipelineCache.BuildConcurrency);
        }
        public void Step() => _ = m_permits.Release();
    }
    // A device whose persistent cache counts the writes asked of it, over another device's services.
    private sealed class PersistingDevice(GpuDeviceServices services) : IGpuDeviceContext, IGpuPipelineCache {
        private int m_persisted;

        public long AdapterLuid => 0L;
        public GpuDeviceCapabilities? Capabilities => null;
        public GpuDeviceIdentity? Identity => null;
        public GpuMemoryProfile MemoryProfile => default;
        public int Persisted => Volatile.Read(location: ref m_persisted);
        public GpuDeviceServices Services { get; } = services;

        public void Persist() => Interlocked.Increment(location: ref m_persisted);
        public void WaitIdle() { }
    }
}
