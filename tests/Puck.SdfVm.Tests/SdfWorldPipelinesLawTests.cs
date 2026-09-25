using System.Collections.Concurrent;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfWorldPipelines"/> over <see cref="FakeGpuDevice"/>: a build creates one pipeline per engine
/// kernel (brick pipelines only when asked for and present) and counts them into the owner's ledger; a build and a
/// reload that created pipelines each write the device's persistent cache once, from the thread that built them; a
/// reload creates only the pipelines whose bytecode changed; a canceled build throws before creating anything; a build
/// holds at most <see cref="SdfWorldPipelines.BuildConcurrency"/> creations in the driver and starts the views variants
/// last; a build canceled while creations are in the driver waits for those alone and creates no more; and a build whose
/// creations fail together names every failed pipeline in build order and releases everything it created.
/// </summary>
public sealed class SdfWorldPipelinesLawTests {
    [Fact]
    public void ABuildCreatesEveryEnginePipelineAndPersistsTheDeviceCacheOnce() {
        var device = new PersistingDevice(services: new FakeGpuDevice(reportVersion: SdfIsa.Version).Services);
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );

        using var pipelines = SdfWorldPipelines.Build(
            cancellationToken: CancellationToken.None,
            device: device,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1),
            ledger: ledger
        );

        Assert.Equal(expected: 11L, actual: Created(ledger: ledger));
        Assert.Equal(expected: 1, actual: device.Persisted);

        using (var unchanged = pipelines.PrepareReload(
            cancellationToken: CancellationToken.None,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        )) {
            Assert.Equal(expected: 0, actual: unchanged.ChangedPipelines);
        }

        using (var changed = pipelines.PrepareReload(
            cancellationToken: CancellationToken.None,
            kernels: SdfTestPipelines.Kernels(beam: 2)
        )) {
            Assert.Equal(expected: 1, actual: changed.ChangedPipelines);
        }

        Assert.Equal(expected: 12L, actual: Created(ledger: ledger));
        Assert.Equal(expected: 3, actual: device.Persisted);
    }
    [Fact]
    public void ABuildWithABrickPoolAddsTheBrickBakePipelineItsKernelCarries() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );

        using var pipelines = SdfWorldPipelines.Build(
            cancellationToken: CancellationToken.None,
            device: gpu,
            includeBrickPipelines: true,
            kernels: SdfTestPipelines.Kernels(beam: 1) with { BrickBake = new byte[] { 1 } },
            ledger: ledger
        );

        Assert.True(condition: pipelines.IncludesBrickPipelines);
        Assert.Equal(expected: 12L, actual: Created(ledger: ledger));
    }
    [Fact]
    public void ACanceledBuildThrowsBeforeCreatingAnything() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );

        Assert.Throws<OperationCanceledException>(testCode: () => SdfWorldPipelines.Build(
            cancellationToken: new CancellationToken(canceled: true),
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1),
            ledger: ledger
        ));
        Assert.Equal(expected: 0L, actual: Created(ledger: ledger));
    }
    [Fact]
    public async Task ABuildHoldsAtMostItsConcurrencyInTheDriverAndStartsTheViewsVariantsLast() {
        using var driver = new SteppedDriver();
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
            BeforeComputePipeline = driver.Enter,
        };
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );
        var build = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: () => SdfWorldPipelines.Build(
                cancellationToken: CancellationToken.None,
                device: gpu,
                includeBrickPipelines: false,
                kernels: SdfTestPipelines.Kernels(beam: 1),
                ledger: ledger
            )
        );
        var started = new List<string>();

        // The first creators enter together; after that each creation let through lets exactly one more start, so the
        // order the rest start in is the build's own.
        for (var creation = 0; (creation < SdfWorldPipelines.BuildConcurrency); creation++) {
            started.Add(item: driver.Next());
        }

        while (started.Count < 11) {
            driver.Step();
            started.Add(item: driver.Next());
        }

        driver.Open();

        using var pipelines = await build;

        Assert.Equal(
            actual: (driver.MostInDriver, started.Distinct().Count()),
            expected: (SdfWorldPipelines.BuildConcurrency, 11)
        );
        Assert.Equal(
            actual: started.TakeLast(count: 3),
            expected: ["sdf-world-views-core", "sdf-world-views-folds", "sdf-world-views"]
        );
        Assert.Equal(expected: 11L, actual: Created(ledger: ledger));
    }
    [Fact]
    public async Task ABuildCanceledWhilePipelinesAreInTheDriverWaitsOnlyForThoseAndCreatesNoMore() {
        using var driver = new SteppedDriver();
        using var cancellation = new CancellationTokenSource();
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
            BeforeComputePipeline = driver.Enter,
        };
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );
        var progress = new SdfWorldPipelineBuildProgress();
        var build = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: () => SdfWorldPipelines.Build(
                cancellationToken: cancellation.Token,
                device: gpu,
                includeBrickPipelines: false,
                kernels: SdfTestPipelines.Kernels(beam: 1),
                ledger: ledger,
                progress: progress
            )
        );

        for (var creation = 0; (creation < SdfWorldPipelines.BuildConcurrency); creation++) {
            _ = driver.Next();
        }

        cancellation.Cancel();
        driver.Open();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(testCode: () => build);
        Assert.Equal(
            actual: (driver.Entered, Created(ledger: ledger), progress.Created),
            expected: (SdfWorldPipelines.BuildConcurrency, ((long)SdfWorldPipelines.BuildConcurrency), SdfWorldPipelines.BuildConcurrency)
        );
    }
    [Fact]
    public void TwoCreationsFailingInTheDriverAtOnceAreBothNamedAndEverythingCreatedIsReleased() {
        Assert.SkipWhen(
            condition: (SdfWorldPipelines.BuildConcurrency < 2),
            reason: "Two creations are in the driver at once only when the build's concurrency is at least two."
        );

        // Each failing creation waits in the driver for the other before it throws, so both fail while the build still
        // runs, and the later-finishing one can never be dropped.
        using var bothInDriver = new Barrier(participantCount: 2);
        var gpu = new FakeGpuDevice(
            reportVersion: SdfIsa.Version,
            trackObjects: true
        ) {
            BeforeComputePipeline = description => {
                if (description.Name is not ("sdf-beam" or "sdf-world-views")) {
                    return;
                }

                _ = bothInDriver.SignalAndWait(timeout: TimeSpan.FromSeconds(value: 30));

                throw new InvalidOperationException(message: $"injected failure creating {description.Name}");
            },
        };
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );
        var failure = Assert.Throws<AggregateException>(testCode: () => SdfWorldPipelines.Build(
            cancellationToken: CancellationToken.None,
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1),
            ledger: ledger
        ));

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

    private static long Created(GpuWorkLedger ledger) {
        Assert.True(condition: ledger.TryRead(kind: GpuWork.PipelinesCreated, value: out var value));

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
        // Takes the name of the next creation to enter; the bound is liveness, and decides nothing.
        public string Next() {
            Assert.True(condition: m_entered.TryTake(
                cancellationToken: TestContext.Current.CancellationToken,
                item: out var name,
                millisecondsTimeout: 30_000
            ));

            return name!;
        }
        // Lets every held creation through, and every later one pass without waiting.
        public void Open() {
            m_open.Set();
            _ = m_permits.Release(releaseCount: SdfWorldPipelines.BuildConcurrency);
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
