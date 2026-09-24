using Puck.Abstractions.Machines;

namespace Puck.GamingBricks.Tests;

public sealed class LinkedMachineGroupConcurrentDisposeTests {
    // The group's execution thread is held INSIDE RunCycles, a step it was already running before either dispose
    // call started. Each disposer then produces exactly one arrival: it either raises SeverWaiting on its way into
    // the wait that must outlast the step, or it disposes its core without waiting. The step is released only after
    // both arrivals, so a disposer that skips the wait has already disposed its core mid-step when the core records
    // it, whatever the scheduler did.
    [Fact]
    public async Task ConcurrentMemberDisposeNeverDisposesACoreWhileTheGroupIsMidStep() {
        var cancellationToken = TestContext.Current.CancellationToken;
        var arrivals = new Arrivals(expected: 2);
        var step = new StepGate();
        var firstCore = new TestQueuedCore(
            arrivals: arrivals,
            step: step
        );
        var secondCore = new TestQueuedCore(
            arrivals: arrivals,
            step: step
        );

        using var firstHost = new TestHost(core: firstCore);
        using var secondHost = new TestHost(core: secondCore);
        using var link = new LinkedMachineGroup(
            createCore: lent => new TestGroupCore(
                lent: lent,
                step: step
            ),
            machines: [firstHost, secondHost],
            maximumPendingSteps: 4,
            workerName: "concurrent-dispose-test-link",
            workers: [firstHost.Worker, secondHost.Worker]
        );
        var severWaits = 0;

        link.SeverWaiting += () => {
            _ = Interlocked.Increment(location: ref severWaits);
            arrivals.Arrive();
        };

        Task[] disposers;

        try {
            Assert.Equal(
                actual: link.Submit(
                    deltaTicks: 1UL,
                    inputs: [default, default]
                ),
                expected: QueuedMachineSubmission.Accepted
            );
            await step.Entered.Task.WaitAsync(cancellationToken: cancellationToken);
            disposers = [
                DisposeOnItsOwnThread(host: firstHost),
                DisposeOnItsOwnThread(host: secondHost),
            ];
            await arrivals.All.Task.WaitAsync(cancellationToken: cancellationToken);
        } finally {
            // Released even when an assertion fails before both arrivals, or the enclosing disposal waits forever
            // and hides the original failure.
            step.Release();
        }

        await Task.WhenAll(tasks: disposers).WaitAsync(cancellationToken: cancellationToken);

        Assert.False(
            condition: (firstCore.DisposedMidStep || secondCore.DisposedMidStep),
            userMessage: "a member's core was disposed while the group's execution thread was still inside the step it was already running"
        );
        Assert.Equal(
            expected: 2,
            actual: Volatile.Read(location: ref severWaits)
        );
        Assert.True(condition: (firstCore.Disposed && secondCore.Disposed));
        Assert.False(
            condition: step.ObservedDisposedCore,
            userMessage: "the step that was already running observed a member's core disposed once it was allowed to finish"
        );
    }

    private static Task DisposeOnItsOwnThread(TestHost host) =>
        Task.Factory.StartNew(
            action: host.Dispose,
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

    private sealed class Arrivals(int expected) {
        private int m_count;

        public TaskCompletionSource All { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arrive() {
            if (Interlocked.Increment(location: ref m_count) == expected) {
                _ = All.TrySetResult();
            }
        }
    }
    // The in-flight step: entered once the group thread is inside RunCycles, finished once it returns from it.
    private sealed class StepGate {
        private readonly ManualResetEventSlim m_release = new(initialState: false);

        private int m_finished;

        public TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsFinished => (Volatile.Read(location: ref m_finished) != 0);
        public bool ObservedDisposedCore { get; set; }

        public void Finish() => Interlocked.Exchange(
            location1: ref m_finished,
            value: 1
        );
        public void Release() => m_release.Set();
        public void WaitForRelease() => m_release.Wait();
    }
    private sealed class TestGroupCore(IReadOnlyList<IQueuedMachineCore> lent, StepGate step) : IMachineGroupCore {
        private readonly TestQueuedCore m_first = ((TestQueuedCore)lent[0]);
        private readonly TestQueuedCore m_second = ((TestQueuedCore)lent[1]);

        public long CompletedTransfers => 0L;
        public long CycleCount => 0L;
        public ulong CyclesPerSecond => 1UL;
        public ReadOnlySpan<uint> Framebuffer => m_first.Framebuffer;
        public int MemberCount => 2;
        public long NativeFrameIndex => 0L;
        public ulong TrafficFingerprint => 0UL;

        public void ApplyInput(in MachineLinkPads input) { }
        public int CaptureState(ref byte[] buffer) => 0;
        public void Dispose() { }
        public void RestoreState(byte[] buffer, int length) { }
        // Blocks the calling (group execution) thread mid-step until the test releases it, holding the step open
        // across the window a concurrent member dispose races against.
        public void RunCycles(long cycles) {
            _ = step.Entered.TrySetResult();
            step.WaitForRelease();
            step.ObservedDisposedCore |= (m_first.Disposed || m_second.Disposed);
            step.Finish();
        }
    }
    // A minimal QueuedMachineHost so disposing it exercises the SAME QueuedMachineWorker.Dispose/DetachCore path a
    // real host (MachineHost) does; the injected core is fixed at construction and ignores the loaded content bytes.
    private sealed class TestHost : QueuedMachineHost {
        private readonly IQueuedMachineCore m_core;

        public TestHost(IQueuedMachineCore core) : base(
            audioSampleRate: 0,
            height: 1,
            maximumPendingSteps: 4,
            savePath: null,
            width: 1,
            workerName: "concurrent-dispose-test-host"
        ) {
            m_core = core;

            LoadContent(
                data: [],
                savePath: null
            );
        }

        protected override IQueuedMachineCore CreateCore(byte[] data, string? savePath) => m_core;
    }
    // Records, at the moment of its own disposal, whether the group's in-flight step had finished; a disposal before
    // the step finishes counts as that disposer's arrival.
    private sealed class TestQueuedCore(Arrivals arrivals, StepGate step) : IQueuedMachineCore {
        private readonly uint[] m_framebuffer = [0U];

        private int m_disposed;

        public string CheckpointIdentity => "test/linked-core";
        public long CycleCount => 0L;
        public ulong CyclesPerSecond => 1UL;
        public bool Disposed => (Volatile.Read(location: ref m_disposed) != 0);
        public bool DisposedMidStep { get; private set; }
        public ReadOnlySpan<uint> Framebuffer => m_framebuffer;
        public long NativeFrameIndex => 0L;

        public void ApplyInput(in MachinePadState input) { }
        public int CaptureState(ref byte[] buffer) => 0;
        public void ConfigureAudio(int sampleRate) { }
        public ITimeTravelLookahead<MachinePadState> CreateLookahead() => throw new NotSupportedException();
        public void Dispose() {
            if (0 != Interlocked.Exchange(
                location1: ref m_disposed,
                value: 1
            )) {
                return;
            }

            if (!step.IsFinished) {
                DisposedMidStep = true;
                arrivals.Arrive();
            }
        }
        public int DrainAudioSamples(Span<short> destination) => 0;
        public void FlushSave(bool force) { }
        public void RestoreState(byte[] buffer, int length) { }
        public void RunCycles(long cycles) { }
    }
}
