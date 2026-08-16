using Puck.Abstractions.Machines;

namespace Puck.GamingBricks.Tests;

public sealed class QueuedMachineWorkerTests {
    [Fact]
    public void RejectsLoadAfterDisposal() {
        using var core = new TestQueuedCore();
        var worker = new QueuedMachineWorker(
            width: 1,
            height: 1,
            maximumPendingSteps: 1,
            workerName: "gaming-bricks-contract-test"
        );

        worker.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(testCode: () => worker.Load(core: core));
        Assert.False(condition: worker.IsAssigned);
        Assert.Equal(expected: 0, actual: core.DisposeCount);
    }

    private sealed class TestQueuedCore : IQueuedMachineCore {
        private readonly uint[] m_framebuffer = [0U];

        public long CycleCount => 0L;
        public ulong CyclesPerSecond => 1UL;
        public int DisposeCount { get; private set; }
        public ReadOnlySpan<uint> Framebuffer => m_framebuffer;
        public long NativeFrameIndex => 0L;

        public void ConfigureAudio(int sampleRate) { }
        public int DrainAudioSamples(Span<short> destination) => 0;
        public void FlushSave(bool force) { }
        public int CaptureState(ref byte[] buffer) => 0;
        public void RestoreState(byte[] buffer, int length) { }
        public void ApplyInput(in MachinePadState input) { }
        public void RunCycles(long cycles) { }
        public ITimeTravelLookahead<MachinePadState> CreateLookahead() => throw new NotSupportedException();
        public void Dispose() => ++DisposeCount;
    }
}
