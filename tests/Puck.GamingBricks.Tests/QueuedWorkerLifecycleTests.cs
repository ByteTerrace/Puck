using Puck.Abstractions.Machines;

namespace Puck.GamingBricks.Tests;

/// <summary>Exercises the one queued-worker lifecycle through both of its hosts — the single-machine
/// <see cref="QueuedMachineWorker"/> and the N-member <see cref="LinkedMachineGroup"/> — under a concurrent producer,
/// so the stop ordering (close, wake every backpressured producer, append an ordered marker, join) and the fault
/// propagation are proven on the shape each host actually runs, not on the primitive alone.</summary>
public sealed class QueuedWorkerLifecycleTests {
    private const int DrainCount = 64;
    private const int JoinTimeoutMilliseconds = 30_000;
    private const int PendingWindow = 4;
    private const int SegmentCount = 512;

    [Fact]
    public void WorkerStopCompletesEverySegmentAcceptedWhileAProducerIsSubmitting() {
        using var core = new CountingCore();
        var worker = new QueuedMachineWorker(
            width: 1,
            height: 1,
            maximumPendingSteps: PendingWindow,
            workerName: "lifecycle-worker-load-test"
        );

        worker.Load(core: core);

        var accepted = 0L;
        var producer = new Thread(start: () => {
            for (var index = 0; (index < SegmentCount); ++index) {
                if (worker.Submit(
                    deltaTicks: 1UL,
                    input: default
                ) != QueuedMachineSubmission.Rejected) {
                    _ = Interlocked.Increment(location: ref accepted);
                }
            }
        }) { IsBackground = true };

        producer.Start();

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

        Assert.True(
            condition: producer.Join(millisecondsTimeout: JoinTimeoutMilliseconds),
            userMessage: "the producer never finished; a lost wake left it waiting for pending-window capacity"
        );

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
    [Fact]
    public void WorkerStopUnderLoadRejectsLaterSubmissionsRatherThanAcceptingThem() {
        using var core = new CountingCore();
        var worker = new QueuedMachineWorker(
            width: 1,
            height: 1,
            maximumPendingSteps: PendingWindow,
            workerName: "lifecycle-worker-stop-test"
        );

        worker.Load(core: core);

        var accepted = 0L;
        var rejected = 0L;
        var producer = new Thread(start: () => {
            for (var index = 0; (index < SegmentCount); ++index) {
                if (worker.Submit(
                    deltaTicks: 1UL,
                    input: default
                ) == QueuedMachineSubmission.Rejected) {
                    _ = Interlocked.Increment(location: ref rejected);
                } else {
                    _ = Interlocked.Increment(location: ref accepted);
                }
            }
        }) { IsBackground = true };

        producer.Start();

        // Stop while the producer is mid-run, with the pending window full often enough that some submissions are
        // parked in backpressure when the queue closes.
        var deadline = (Environment.TickCount64 + JoinTimeoutMilliseconds);

        while (
            (worker.CompletedSteps < PendingWindow) &&
            (Environment.TickCount64 < deadline)
        ) {
            Thread.Yield();
        }

        worker.Dispose();

        Assert.True(
            condition: producer.Join(millisecondsTimeout: JoinTimeoutMilliseconds),
            userMessage: "the producer never finished; the stop left a backpressured submission waiting on a queue nothing would drain"
        );
        Assert.Null(@object: worker.QueueFault);
        Assert.Equal(
            expected: Interlocked.Read(location: ref accepted),
            actual: worker.CompletedSteps
        );
        Assert.Equal(
            expected: SegmentCount,
            actual: ((int)(Interlocked.Read(location: ref accepted) + Interlocked.Read(location: ref rejected)))
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
    public void GroupStopCompletesEverySegmentAcceptedWhileAProducerIsSubmitting() {
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
        var producer = new Thread(start: () => {
            for (var index = 0; (index < SegmentCount); ++index) {
                if (link.Submit(
                    deltaTicks: 1UL,
                    inputs: [default, default]
                ) != QueuedMachineSubmission.Rejected) {
                    _ = Interlocked.Increment(location: ref accepted);
                }
            }
        }) { IsBackground = true };

        producer.Start();

        // The link is live for this whole loop, so every synchronous step is accepted and drained rather than refused.
        for (var index = 0; (index < DrainCount); ++index) {
            link.Step(
                deltaTicks: 1UL,
                inputs: [default, default]
            );
            _ = Interlocked.Increment(location: ref accepted);
        }

        Assert.True(
            condition: producer.Join(millisecondsTimeout: JoinTimeoutMilliseconds),
            userMessage: "the producer never finished; a lost wake left it waiting for pending-window capacity"
        );

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

    private sealed class CountingCore : IQueuedMachineCore {
        private readonly uint[] m_framebuffer = [0U];

        private long m_runCycleCalls;

        public long CycleCount => 0L;
        public ulong CyclesPerSecond => 1UL;
        public int DisposeCount { get; private set; }
        public ReadOnlySpan<uint> Framebuffer => m_framebuffer;
        public long NativeFrameIndex => 0L;
        public long RunCycleCalls => Interlocked.Read(location: ref m_runCycleCalls);
        public bool ThrowOnRunCycles { get; init; }

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

            _ = Interlocked.Increment(location: ref m_runCycleCalls);
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
