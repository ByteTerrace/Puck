using Puck.Abstractions.Machines;

namespace Puck.GamingBricks.Tests;

public sealed class LinkedMachineGroupConcurrentDisposeTests {
    private const int GateTimeoutMilliseconds = 5_000;
    private const int PollWindowMilliseconds = 300;

    // Reproduces the finding-14 race directly rather than by chance: the group's own execution thread is held INSIDE
    // RunCycles (a step it was already running before either dispose call started), so a core disposed while that
    // gate is held is disposed while the group thread might still touch it — the defect, made observable without
    // depending on scheduler timing to create the window.
    [Fact]
    public void ConcurrentMemberDisposeNeverDisposesACoreWhileTheGroupIsMidStep() {
        var firstCore = new TestQueuedCore();
        var secondCore = new TestQueuedCore();

        using var firstHost = new TestHost(core: firstCore);
        using var secondHost = new TestHost(core: secondCore);

        TestGroupCore? groupCore = null;
        using var link = new LinkedMachineGroup(
            createCore: lent => (groupCore = new TestGroupCore(lent: lent)),
            machines: [firstHost, secondHost],
            maximumPendingSteps: 4,
            workerName: "concurrent-dispose-test-link",
            workers: [firstHost.Worker, secondHost.Worker]
        );

        Assert.NotNull(@object: groupCore);
        Assert.Equal(
            actual: link.Submit(
                deltaTicks: 1UL,
                inputs: [default, default]
            ),
            expected: QueuedMachineSubmission.Accepted
        );
        Assert.True(condition: groupCore!.WaitUntilEnteredRunCycles(millisecondsTimeout: GateTimeoutMilliseconds));

        var firstDispose = new Thread(start: () => firstHost.Dispose()) { IsBackground = true };
        var secondDispose = new Thread(start: () => secondHost.Dispose()) { IsBackground = true };

        firstDispose.Start();
        secondDispose.Start();

        var observedDisposeWhileMidStep = false;
        var deadline = Environment.TickCount64 + PollWindowMilliseconds;

        while (Environment.TickCount64 < deadline) {
            if (
                firstCore.Disposed ||
                secondCore.Disposed
            ) {
                observedDisposeWhileMidStep = true;

                break;
            }

            Thread.Sleep(millisecondsTimeout: 5);
        }

        groupCore.Release();

        var bothDisposed = (
            firstDispose.Join(millisecondsTimeout: GateTimeoutMilliseconds) &&
            secondDispose.Join(millisecondsTimeout: GateTimeoutMilliseconds)
        );

        Assert.True(
            condition: bothDisposed,
            userMessage: "disposing both members concurrently did not complete; a lost wait left one disposer stuck"
        );
        Assert.False(
            condition: observedDisposeWhileMidStep,
            userMessage: "a member's core was disposed while the group's execution thread was still inside the step it was already running"
        );
        Assert.False(
            condition: groupCore.ObservedDisposedCoreAfterGate,
            userMessage: "the step that was already running observed a member's core disposed once it was allowed to finish"
        );
    }

    private sealed class TestGroupCore : IMachineGroupCore {
        private readonly TestQueuedCore m_first;
        private readonly ManualResetEventSlim m_gateEntered = new(initialState: false);
        private readonly ManualResetEventSlim m_gateRelease = new(initialState: false);
        private readonly TestQueuedCore m_second;

        public TestGroupCore(IReadOnlyList<IQueuedMachineCore> lent) {
            m_first = ((TestQueuedCore)lent[0]);
            m_second = ((TestQueuedCore)lent[1]);
        }

        public long CompletedTransfers => 0L;
        public long CycleCount => 0L;
        public ulong CyclesPerSecond => 1UL;
        public ReadOnlySpan<uint> Framebuffer => m_first.Framebuffer;
        public int MemberCount => 2;
        public long NativeFrameIndex => 0L;
        public bool ObservedDisposedCoreAfterGate { get; private set; }
        public ulong TrafficFingerprint => 0UL;

        public void ApplyInput(in MachineLinkPads input) { }
        public int CaptureState(ref byte[] buffer) => 0;
        public void Dispose() { }
        public void Release() => m_gateRelease.Set();
        public void RestoreState(byte[] buffer, int length) { }
        // Blocks the calling (group execution) thread mid-step until the test releases it, holding the step open
        // across the window a concurrent member dispose races against.
        public void RunCycles(long cycles) {
            m_gateEntered.Set();
            m_gateRelease.Wait();

            if (
                m_first.Disposed ||
                m_second.Disposed
            ) {
                ObservedDisposedCoreAfterGate = true;
            }
        }
        public bool WaitUntilEnteredRunCycles(int millisecondsTimeout) =>
            m_gateEntered.Wait(millisecondsTimeout: millisecondsTimeout);
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
    private sealed class TestQueuedCore : IQueuedMachineCore {
        private readonly uint[] m_framebuffer = [0U];

        public long CycleCount => 0L;
        public ulong CyclesPerSecond => 1UL;
        public bool Disposed { get; private set; }
        public ReadOnlySpan<uint> Framebuffer => m_framebuffer;
        public long NativeFrameIndex => 0L;

        public void ApplyInput(in MachinePadState input) { }
        public int CaptureState(ref byte[] buffer) => 0;
        public void ConfigureAudio(int sampleRate) { }
        public ITimeTravelLookahead<MachinePadState> CreateLookahead() => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public int DrainAudioSamples(Span<short> destination) => 0;
        public void FlushSave(bool force) { }
        public void RestoreState(byte[] buffer, int length) { }
        public void RunCycles(long cycles) { }
    }
}
