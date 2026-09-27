using System.Buffers.Binary;

namespace Puck.Machines.Tests;

/// <summary>Pins the queued host's pacing and checkpoint at a clock that is not a whole number of hertz: the durable
/// checkpoint carries a phase scaled by the rate's seconds, restores and continues identically in an independent host,
/// and refuses a runtime whose clock carries a different scale.</summary>
public sealed class FractionalRateCheckpointTests {
    private static readonly MachineCycleRate NtscMasterClock = new(
        cycles: 236_250_000UL,
        seconds: 11UL
    );

    [Fact]
    public void ACheckpointAtAFractionalRateRestoresAndContinuesIdentically() {
        var result = QueuedHostContractProbe.VerifyCheckpoint(withContent: () => new TestHost(rate: NtscMasterClock));

        Assert.True(
            condition: result.Passed,
            userMessage: result.Detail
        );
    }
    [Fact]
    public void ACheckpointRefusesARuntimeWhoseClockScaleDiffers() {
        using var source = new TestHost(rate: NtscMasterClock);
        using var target = new TestHost(rate: new MachineCycleRate(cycles: 21_477_272UL));

        _ = source.Advance(deltaTicks: 841UL);

        var checkpoint = source.CaptureCheckpoint();
        var untouched = target.CaptureCheckpoint();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => target.RestoreCheckpoint(checkpoint: checkpoint));
        Assert.Equal(
            actual: target.CaptureCheckpoint(),
            expected: untouched
        );
    }
    [Fact]
    public void ARateRefusesZeroSeconds() =>
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new MachineCycleRate(
            cycles: 1UL,
            seconds: 0UL
        ));

    // A core whose whole state is its cycle count and the last input it saw, so a checkpoint round trip is checked
    // byte for byte without an emulator.
    private sealed class CyclingCore(MachineCycleRate rate) : IQueuedMachineCore {
        private const long CyclesPerFrame = 357_954L;

        private readonly uint[] m_framebuffer = [0U];

        private long m_cycles;
        private uint m_buttons;

        public string CheckpointIdentity => "test/cycling-core";
        public long CycleCount => m_cycles;
        public MachineCycleRate CycleRate => rate;
        public ReadOnlySpan<uint> Framebuffer => m_framebuffer;
        public long NativeFrameIndex => (m_cycles / CyclesPerFrame);

        public void ApplyInput(in MachinePads input) =>
            m_buttons = ((uint)input[0].Buttons);
        public int CaptureState(ref byte[] buffer) {
            if (buffer.Length < 12) {
                buffer = new byte[12];
            }

            BinaryPrimitives.WriteInt64LittleEndian(
                destination: buffer,
                value: m_cycles
            );
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: buffer.AsSpan(start: 8),
                value: m_buttons
            );

            return 12;
        }
        public void ConfigureAudio(int sampleRate) { }
        public ITimeTravelLookahead<MachinePads> CreateLookahead() => throw new NotSupportedException();
        public void Dispose() { }
        public int DrainAudioSamples(Span<short> destination) => 0;
        public void FlushSave(bool force) { }
        public void RestoreState(byte[] buffer, int length) {
            m_cycles = BinaryPrimitives.ReadInt64LittleEndian(source: buffer);
            m_buttons = BinaryPrimitives.ReadUInt32LittleEndian(source: buffer.AsSpan(start: 8));
        }
        public void RunCycles(long cycles) =>
            m_cycles += cycles;
    }
    private sealed class TestHost : QueuedMachineHost {
        private readonly MachineCycleRate m_rate;

        public TestHost(MachineCycleRate rate) : base(
            audioSampleRate: 0,
            height: 1,
            maximumPendingSteps: 32,
            savePath: null,
            width: 1,
            workerName: "fractional-rate-test-host"
        ) {
            m_rate = rate;

            LoadContent(
                data: [],
                savePath: null
            );
        }

        protected override IQueuedMachineCore CreateCore(byte[] data, string? savePath) => new CyclingCore(rate: m_rate);
    }
}
