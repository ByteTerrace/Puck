using System.Buffers.Binary;
using Puck.Abstractions.Machines;

namespace Puck.Machines.Tests;

/// <summary>Pins the queued host's controller ports: one port per declared seat, named in seat order, each port's
/// state reaching the core as its own seat, every declared seat riding a checkpoint, and a checkpoint refused by a host
/// that declares a different number of seats.</summary>
public sealed class MultiPortInputTests {
    private static readonly string[] TwoPorts = ["controls", "controls-2"];

    [Fact]
    public void SeatOrderCannotBeChangedThroughThePublishedList() {
        using var host = new TestHost(
            core: new SeatRecordingCore(),
            inputPorts: TwoPorts
        );
        var seats = Assert.IsAssignableFrom<IList<IMachineInputPort>>(host.Seats);

        _ = Assert.Throws<NotSupportedException>(testCode: () => seats[0] = seats[1]);
        Assert.Same(
            actual: host.Seats[0],
            expected: host.InputPorts["controls"]
        );
    }
    [Fact]
    public void EachPortReachesTheCoreAsItsOwnSeat() {
        var core = new SeatRecordingCore();

        using var host = new TestHost(
            core: core,
            inputPorts: TwoPorts
        );

        host.InputPorts["controls"].SetState(state: Pressed(buttons: MachineButtons.South));
        host.InputPorts["controls-2"].SetState(state: Pressed(buttons: MachineButtons.North));
        _ = host.Advance(deltaTicks: 840UL);

        Assert.Equal(
            actual: core.Seen,
            expected: [MachineButtons.South, MachineButtons.North, MachineButtons.None, MachineButtons.None]
        );
        Assert.Same(
            actual: host.Seats[1],
            expected: host.InputPorts["controls-2"]
        );
    }
    [Fact]
    public void ACheckpointCarriesEveryDeclaredSeat() {
        using var source = new TestHost(
            core: new SeatRecordingCore(),
            inputPorts: TwoPorts
        );
        using var target = new TestHost(
            core: new SeatRecordingCore(),
            inputPorts: TwoPorts
        );

        source.Seats[0].SetState(state: Pressed(buttons: MachineButtons.Start));
        source.Seats[1].SetState(state: Pressed(buttons: MachineButtons.East));
        _ = source.Advance(deltaTicks: 840UL);
        target.RestoreCheckpoint(checkpoint: source.CaptureCheckpoint());

        Assert.Equal(
            actual: target.Seats[1].State.Buttons,
            expected: MachineButtons.East
        );
        Assert.Equal(
            actual: target.CaptureCheckpoint(),
            expected: source.CaptureCheckpoint()
        );
    }
    [Fact]
    public void ACheckpointRefusesAHostWithADifferentSeatCount() {
        using var source = new TestHost(
            core: new SeatRecordingCore(),
            inputPorts: TwoPorts
        );
        using var target = new TestHost(
            core: new SeatRecordingCore(),
            inputPorts: null
        );

        _ = source.Advance(deltaTicks: 840UL);

        var untouched = target.CaptureCheckpoint();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => target.RestoreCheckpoint(checkpoint: source.CaptureCheckpoint()));
        Assert.Equal(
            actual: target.CaptureCheckpoint(),
            expected: untouched
        );
    }
    [Fact]
    public void AnExplicitSubmissionHoldsUndeclaredSeatsNeutral() {
        var core = new SeatRecordingCore();

        using var host = new TestHost(
            core: core,
            inputPorts: null
        );

        _ = host.Step(
            deltaTicks: 840UL,
            inputs: MachinePads.From(inputs: [Pressed(buttons: MachineButtons.South), Pressed(buttons: MachineButtons.North)])
        );

        Assert.Equal(
            actual: core.Seen,
            expected: [MachineButtons.South, MachineButtons.None, MachineButtons.None, MachineButtons.None]
        );
    }
    [Fact]
    public void ASingleSeatHostDeclaresOnlyTheControlsPort() {
        using var host = new TestHost(
            core: new SeatRecordingCore(),
            inputPorts: null
        );

        Assert.Equal(
            actual: host.InputPorts.Keys,
            expected: ["controls"]
        );
    }
    [Fact]
    public void AHostRefusesASeatCountOutsideTheImage() {
        foreach (var inputPorts in (string[][])[[], ["a", "b", "c", "d", "e"]]) {
            _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new TestHost(
                core: new SeatRecordingCore(),
                inputPorts: inputPorts
            ));
        }
    }
    [Fact]
    public void AHostRefusesARepeatedOrBlankPortName() {
        foreach (var inputPorts in (string[][])[["controls", "controls"], ["controls", " "]]) {
            _ = Assert.ThrowsAny<ArgumentException>(testCode: () => new TestHost(
                core: new SeatRecordingCore(),
                inputPorts: inputPorts
            ));
        }
    }

    [Fact]
    public void SeatImagesCompareEverySeat() {
        var first = MachinePads.From(inputs: [MachinePadState.Neutral, MachinePadState.Neutral, MachinePadState.Neutral, Pressed(buttons: MachineButtons.West)]);
        var same = MachinePads.From(inputs: [MachinePadState.Neutral, MachinePadState.Neutral, MachinePadState.Neutral, Pressed(buttons: MachineButtons.West)]);

        Assert.True(condition: EqualityComparer<MachinePads>.Default.Equals(
            x: first,
            y: same
        ));
        Assert.Equal(
            actual: same.GetHashCode(),
            expected: first.GetHashCode()
        );
        Assert.False(condition: EqualityComparer<MachinePads>.Default.Equals(
            x: first,
            y: MachinePads.Neutral
        ));
    }

    private static MachinePadState Pressed(MachineButtons buttons) =>
        (MachinePadState.Neutral with { Buttons = buttons });

    // A core whose whole state is each seat's buttons as it last saw them, so a checkpoint carries them byte for byte.
    private sealed class SeatRecordingCore : IQueuedMachineCore {
        private readonly uint[] m_framebuffer = [0U];

        public MachineButtons[] Seen { get; } = new MachineButtons[MachinePads.MaxSeats];
        public string CheckpointIdentity => "test/seat-recording-core";
        public long CycleCount => 0L;
        public MachineCycleRate CycleRate => new(cycles: 60UL);
        public ReadOnlySpan<uint> Framebuffer => m_framebuffer;
        public long NativeFrameIndex => 0L;

        public void ApplyInput(in MachinePads input) {
            for (var seat = 0; (seat < MachinePads.MaxSeats); ++seat) {
                Seen[seat] = input[seat].Buttons;
            }
        }
        public int CaptureState(ref byte[] buffer) {
            var length = (MachinePads.MaxSeats * sizeof(uint));

            if (buffer.Length < length) {
                buffer = new byte[length];
            }

            for (var seat = 0; (seat < MachinePads.MaxSeats); ++seat) {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination: buffer.AsSpan(start: (seat * sizeof(uint))),
                    value: ((uint)Seen[seat])
                );
            }

            return length;
        }
        public void ConfigureAudio(int sampleRate) { }
        public ITimeTravelLookahead<MachinePads> CreateLookahead() => throw new NotSupportedException();
        public void Dispose() { }
        public int DrainAudioSamples(Span<short> destination) => 0;
        public void FlushSave(bool force) { }
        public void RestoreState(byte[] buffer, int length) {
            for (var seat = 0; (seat < MachinePads.MaxSeats); ++seat) {
                Seen[seat] = ((MachineButtons)BinaryPrimitives.ReadUInt32LittleEndian(source: buffer.AsSpan(start: (seat * sizeof(uint)))));
            }
        }
        public void RunCycles(long cycles) { }
    }
    private sealed class TestHost : QueuedMachineHost {
        private readonly IQueuedMachineCore m_core;

        public TestHost(IQueuedMachineCore core, IReadOnlyList<string>? inputPorts) : base(
            audioSampleRate: 0,
            height: 1,
            inputPorts: inputPorts,
            maximumPendingSteps: 32,
            savePath: null,
            width: 1,
            workerName: "multi-port-test-host"
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
