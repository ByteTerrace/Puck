using System.Buffers.Binary;
using Puck.Testing;

namespace Puck.Machines.Tests;

/// <summary>Pins the queued host's pacing and checkpoint at a clock that is not a whole number of hertz: the durable
/// checkpoint carries a phase scaled by the rate's seconds, restores and continues identically in an independent host
/// and over the stepped runtime it was captured from, and refuses a runtime whose clock carries a different scale.</summary>
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

    // A checkpoint opens with its format string, then the shape fingerprint the ledger records for the format, each
    // length-prefixed by one byte, and ends in a SHA-256 over everything before it.
    private const int FormatStringBytes = 22;
    private const int FingerprintBytes = 16;
    private const int FingerprintOffset = ((1 + FormatStringBytes) + 1);

    [Fact]
    public void ACheckpointCarriesTheShapeTheLedgerRecords() {
        using var host = new TestHost(rate: NtscMasterClock);

        _ = host.Advance(deltaTicks: 841UL);
        Assert.Equal(
            actual: System.Text.Encoding.ASCII.GetString(bytes: host.CaptureCheckpoint().AsSpan(
                length: FingerprintBytes,
                start: FingerprintOffset
            )),
            expected: FormatLedgerShapes.Of(id: "QueuedMachineCheckpoint.Format")
        );
    }
    // The same format string under another shape fingerprint, with its content hash recomputed so only the shape is
    // wrong, is refused by the fingerprint's name before the machine's pacing or state is read, and the host is untouched.
    [Fact]
    public void ACheckpointOfAnotherShapeRefusesByItsFingerprintAndLeavesTheHostUntouched() {
        using var source = new TestHost(rate: NtscMasterClock);
        using var target = new TestHost(rate: NtscMasterClock);

        _ = source.Advance(deltaTicks: 841UL);

        var checkpoint = source.CaptureCheckpoint().ToArray();
        var untouched = target.CaptureCheckpoint();

        System.Text.Encoding.ASCII.GetBytes(
            bytes: checkpoint.AsSpan(start: FingerprintOffset),
            chars: "0000000000000000"
        );
        System.Security.Cryptography.SHA256.HashData(
            destination: checkpoint.AsSpan(start: (checkpoint.Length - 32)),
            source: checkpoint.AsSpan(
                length: (checkpoint.Length - 32),
                start: 0
            )
        );

        var refusal = Assert.Throws<InvalidDataException>(testCode: () => target.RestoreCheckpoint(checkpoint: checkpoint));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "shape fingerprint 0000000000000000"
        );
        Assert.Equal(
            actual: target.CaptureCheckpoint(),
            expected: untouched
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
    // A world history rewinds a running machine in place: the restore lands on the captured image and step count
    // exactly as a restore into a fresh host does, and the two continue identically.
    [Fact]
    public void ACheckpointRestoresOverASteppedRuntimeAsItDoesIntoAFreshOne() {
        using var rewound = new TestHost(rate: NtscMasterClock);
        using var fresh = new TestHost(rate: NtscMasterClock);

        _ = rewound.Advance(deltaTicks: 841UL);

        var checkpoint = rewound.CaptureCheckpoint();

        _ = rewound.Advance(deltaTicks: 2523UL);
        Assert.NotEqual(expected: checkpoint, actual: rewound.CaptureCheckpoint());

        rewound.RestoreCheckpoint(checkpoint: checkpoint);
        fresh.RestoreCheckpoint(checkpoint: checkpoint);
        Assert.Equal(expected: checkpoint, actual: rewound.CaptureCheckpoint());

        _ = rewound.Advance(deltaTicks: 1682UL);
        _ = fresh.Advance(deltaTicks: 1682UL);
        Assert.Equal(expected: fresh.CaptureCheckpoint(), actual: rewound.CaptureCheckpoint());
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
