using Puck.Abstractions.Machines;

namespace Puck.GamingBricks.Tests;

/// <summary>Exercises the one queued-worker lifecycle through both of its hosts — the single-machine
/// <see cref="QueuedMachineWorker"/> and the N-member <see cref="LinkedMachineGroup"/> — under a concurrent producer,
/// so the stop ordering (close, wake every backpressured producer, append an ordered marker, join) and the fault
/// propagation are proven on the shape each host actually runs, not on the primitive alone.</summary>
public sealed class QueuedWorkerLifecycleTests {
    private const int DrainCount = 64;
    private const int PendingWindow = 4;
    private const int SegmentCount = 512;

    [Fact]
    public void GroupFaultReleasesTheDrainAndReportsTheFault() {
        var firstCore = new CountingCore();
        var secondCore = new CountingCore();

        using var firstHost = new TestHost(core: firstCore);
        using var secondHost = new TestHost(core: secondCore);
        using var link = new LinkedMachineGroup(
            createCore: lent => new CountingGroupCore(lent: lent) { ThrowOnRunCycles = true },
            machines: [firstHost, secondHost],
            maximumPendingSteps: PendingWindow,
            workerName: "lifecycle-link-fault-test",
            workers: [firstHost.Worker, secondHost.Worker]
        );

        var fault = Assert.Throws<InvalidOperationException>(testCode: () => link.Step(
            deltaTicks: 1UL,
            inputs: [default, default]
        ));

        Assert.Contains(
            actualString: fault.Message,
            expectedSubstring: "link thread faulted"
        );
        Assert.NotNull(@object: link.QueueFault);
        Assert.Equal(
            actual: link.Submit(
                deltaTicks: 1UL,
                inputs: [default, default]
            ),
            expected: QueuedMachineSubmission.Rejected
        );
    }
    [Fact]
    public async Task GroupStopCompletesEverySegmentAcceptedWhileAProducerIsSubmitting() {
        var firstCore = new CountingCore();
        var secondCore = new CountingCore();

        using var firstHost = new TestHost(core: firstCore);
        using var secondHost = new TestHost(core: secondCore);

        var link = new LinkedMachineGroup(
            createCore: lent => new CountingGroupCore(lent: lent),
            machines: [firstHost, secondHost],
            maximumPendingSteps: PendingWindow,
            workerName: "lifecycle-link-load-test",
            workers: [firstHost.Worker, secondHost.Worker]
        );
        var accepted = 0L;
        var producer = RunOnItsOwnThread(body: () => {
            for (var index = 0; (index < SegmentCount); ++index) {
                if (link.Submit(
                    deltaTicks: 1UL,
                    inputs: [default, default]
                ) != QueuedMachineSubmission.Rejected) {
                    _ = Interlocked.Increment(location: ref accepted);
                }
            }
        });

        // The link is live for this whole loop, so every synchronous step is accepted and drained rather than refused.
        for (var index = 0; (index < DrainCount); ++index) {
            link.Step(
                deltaTicks: 1UL,
                inputs: [default, default]
            );
            _ = Interlocked.Increment(location: ref accepted);
        }

        // A lost wake would leave the producer waiting for pending-window capacity forever.
        await producer.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);

        var completed = link.CompletedSteps;

        link.Dispose();

        Assert.Null(@object: link.QueueFault);
        Assert.Equal(
            expected: Interlocked.Read(location: ref accepted),
            actual: completed
        );
        // Every member publishes once per group segment through its own worker, so the members' completed counts track
        // the group's rather than restarting when the cable goes in.
        Assert.Equal(
            expected: completed,
            actual: firstHost.Worker.CompletedSteps
        );
        Assert.Equal(
            expected: completed,
            actual: secondHost.Worker.CompletedSteps
        );
    }
    [Fact]
    public void WorkerFaultReleasesTheDrainAndReportsTheFault() {
        using var core = new CountingCore { ThrowOnRunCycles = true };
        using var worker = new QueuedMachineWorker(
            width: 1,
            height: 1,
            maximumPendingSteps: PendingWindow,
            workerName: "lifecycle-worker-fault-test"
        );

        worker.Load(core: core);

        var fault = Assert.Throws<InvalidOperationException>(testCode: () => worker.Step(
            deltaTicks: 1UL,
            input: default
        ));

        Assert.Contains(
            actualString: fault.Message,
            expectedSubstring: "lifecycle-worker-fault-test worker faulted"
        );
        Assert.NotNull(@object: worker.QueueFault);
        // A faulted queue accepts nothing further and never blocks a later producer on work that cannot run.
        Assert.Equal(
            actual: worker.Submit(
                deltaTicks: 1UL,
                input: default
            ),
            expected: QueuedMachineSubmission.Rejected
        );
    }
    [Fact]
    public async Task WorkerStopCompletesEverySegmentAcceptedWhileAProducerIsSubmitting() {
        using var core = new CountingCore();
        var worker = new QueuedMachineWorker(
            width: 1,
            height: 1,
            maximumPendingSteps: PendingWindow,
            workerName: "lifecycle-worker-load-test"
        );

        worker.Load(core: core);

        var accepted = 0L;
        var producer = RunOnItsOwnThread(body: () => {
            for (var index = 0; (index < SegmentCount); ++index) {
                if (worker.Submit(
                    deltaTicks: 1UL,
                    input: default
                ) != QueuedMachineSubmission.Rejected) {
                    _ = Interlocked.Increment(location: ref accepted);
                }
            }
        });

        // Synchronous drains race the queued producer: each one appends a barrier behind whatever the producer has
        // already accepted and blocks until the worker has run all of it.
        for (var index = 0; (index < DrainCount); ++index) {
            if (worker.Step(
                deltaTicks: 1UL,
                input: default
            )) {
                _ = Interlocked.Increment(location: ref accepted);
            }
        }

        // A lost wake would leave the producer waiting for pending-window capacity forever.
        await producer.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        worker.Dispose();

        var submitted = Interlocked.Read(location: ref accepted);

        Assert.Null(@object: worker.QueueFault);
        Assert.Equal(
            expected: submitted,
            actual: worker.CompletedSteps
        );
        Assert.Equal(
            expected: 0L,
            actual: worker.PendingSteps
        );
        // The stop drains rather than discards: every accepted segment reached the core before the join returned.
        Assert.Equal(
            expected: submitted,
            actual: core.RunCycleCalls
        );
        Assert.Equal(
            expected: 1,
            actual: core.DisposeCount
        );
    }
    // The first segment holds the worker mid-step, so nothing the producer submits can complete, and the pending
    // window holds the producer to a handful of acceptances, until the stop has closed the queue. The first
    // rejection proves the close happened while the producer was still submitting; only then is the step released.
    [Fact]
    public async Task WorkerStopUnderLoadRejectsEverySubmissionAfterTheCloseAndCompletesEveryOneBefore() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim(initialState: false);
        using var core = new CountingCore { FirstStepRelease = release };
        var worker = new QueuedMachineWorker(
            width: 1,
            height: 1,
            maximumPendingSteps: PendingWindow,
            workerName: "lifecycle-worker-stop-test"
        );

        worker.Load(core: core);

        var accepted = 0L;
        var acceptedAfterRejection = 0L;
        var firstRejection = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = 0L;
        var producer = RunOnItsOwnThread(body: () => {
            for (var index = 0; (index < SegmentCount); ++index) {
                if (worker.Submit(
                    deltaTicks: 1UL,
                    input: default
                ) == QueuedMachineSubmission.Rejected) {
                    ++rejected;
                    _ = firstRejection.TrySetResult();
                } else {
                    ++accepted;

                    if (rejected != 0L) {
                        ++acceptedAfterRejection;
                    }
                }
            }
        });
        Task stop;

        try {
            await core.FirstStepEntered.Task.WaitAsync(cancellationToken: cancellationToken);
            stop = RunOnItsOwnThread(body: worker.Dispose);
            // A producer that finishes without a rejection has been accepted past the close; the assertions say so.
            _ = await Task.WhenAny(
                task1: firstRejection.Task,
                task2: producer
            ).WaitAsync(cancellationToken: cancellationToken);
        } finally {
            release.Set();
        }

        await Task.WhenAll(tasks: [producer, stop]).WaitAsync(cancellationToken: cancellationToken);

        Assert.Null(@object: worker.QueueFault);
        Assert.Equal(
            actual: acceptedAfterRejection,
            expected: 0L
        );
        Assert.InRange(
            actual: accepted,
            high: PendingWindow,
            low: 1L
        );
        Assert.Equal(
            actual: (accepted + rejected),
            expected: ((long)SegmentCount)
        );
        // The stop drained what it inherited: every accepted segment ran, and the core was disposed once.
        Assert.Equal(
            expected: accepted,
            actual: worker.CompletedSteps
        );
        Assert.Equal(
            expected: accepted,
            actual: core.RunCycleCalls
        );
        Assert.Equal(
            expected: 1,
            actual: core.DisposeCount
        );
    }

    private static Task RunOnItsOwnThread(Action body) =>
        Task.Factory.StartNew(
            action: body,
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

    private sealed class CountingCore : IQueuedMachineCore {
        public string CheckpointIdentity => "test/counting-core";
        public long CycleCount => 0L;
        public ulong CyclesPerSecond => 1UL;
        public int DisposeCount { get; private set; }

        /// <summary>Gets a signal set once the first segment is running and, when <see cref="FirstStepRelease"/> is
        /// set, holding the worker until it is released.</summary>
        public TaskCompletionSource FirstStepEntered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim? FirstStepRelease { get; init; }
        public ReadOnlySpan<uint> Framebuffer => m_framebuffer;
        public long NativeFrameIndex => 0L;
        public long RunCycleCalls => Interlocked.Read(location: ref m_runCycleCalls);
        public bool ThrowOnRunCycles { get; init; }

        private readonly uint[] m_framebuffer = [0U];

        private long m_runCycleCalls;

        public void ApplyInput(in MachinePadState input) { }
        public int CaptureState(ref byte[] buffer) => 0;
        public void ConfigureAudio(int sampleRate) { }
        public ITimeTravelLookahead<MachinePadState> CreateLookahead() => throw new NotSupportedException();
        public void Dispose() => ++DisposeCount;
        public int DrainAudioSamples(Span<short> destination) => 0;
        public void FlushSave(bool force) { }
        public void RestoreState(byte[] buffer, int length) { }
        public void RunCycles(long cycles) {
            if (ThrowOnRunCycles) {
                throw new InvalidOperationException(message: "the core refuses to run");
            }

            if (
                (Interlocked.Increment(location: ref m_runCycleCalls) == 1L) &&
                FirstStepEntered.TrySetResult()
            ) {
                FirstStepRelease?.Wait();
            }
        }
    }
    private sealed class CountingGroupCore : IMachineGroupCore {
        private readonly CountingCore m_first;

        public CountingGroupCore(IReadOnlyList<IQueuedMachineCore> lent) =>
            m_first = ((CountingCore)lent[0]);

        public long CompletedTransfers => 0L;
        public long CycleCount => 0L;
        public ulong CyclesPerSecond => 1UL;
        public ReadOnlySpan<uint> Framebuffer => m_first.Framebuffer;
        public int MemberCount => 2;
        public long NativeFrameIndex => 0L;
        public bool ThrowOnRunCycles { get; init; }
        public ulong TrafficFingerprint => 0UL;

        public void ApplyInput(in MachineLinkPads input) { }
        public int CaptureState(ref byte[] buffer) => 0;
        public void Dispose() { }
        public void RestoreState(byte[] buffer, int length) { }
        public void RunCycles(long cycles) {
            if (ThrowOnRunCycles) {
                throw new InvalidOperationException(message: "the medium refuses to run");
            }
        }
    }
    // A minimal host so a member is torn down through the same QueuedMachineWorker paths a real host uses; the
    // injected core is fixed at construction and ignores the loaded content bytes.
    private sealed class TestHost : QueuedMachineHost {
        private readonly IQueuedMachineCore m_core;

        public TestHost(IQueuedMachineCore core) : base(
            audioSampleRate: 0,
            height: 1,
            maximumPendingSteps: PendingWindow,
            savePath: null,
            width: 1,
            workerName: "lifecycle-test-host"
        ) {
            m_core = core;

            LoadContent(
                data: [],
                savePath: null
            );
        }

        protected override IQueuedMachineCore CreateCore(byte[] data, string? savePath) => m_core;
    }
}
