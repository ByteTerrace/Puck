using Puck.Abstractions.Presentation;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>A capture feed polls its display's color space on frames and queued liveness checks. The probe holds one
/// DXGI factory and the output it found, so a steady capture creates no factory and enumerates no output per poll: only a stale
/// factory, the way DXGI reports a display change, opens another, and only a different monitor is found again.</summary>
public sealed class Win32DisplayColorSpaceProbeTests {
    private const nint Monitor = 0x1001;
    private const nint OtherMonitor = 0x2002;
    private const int Polls = 1000;

    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
    [Theory]
    public void A_failed_pixel_allocation_releases_the_open_display_before_a_retry(int failingAllocation) {
        var dxgi = new FakeDxgi(colorSpace: DisplayColorSpace.Hdr10);

        for (var attempt = 0; (attempt < 2); attempt++) {
            var allocations = 0;

            Assert.False(condition: Win32GraphicsCaptureFeed.TryCreateForMonitor(
                allocatePixels: length => {
                    if (++allocations == failingAllocation) {
                        throw new OutOfMemoryException();
                    }

                    return new byte[length];
                },
                feed: out var feed,
                height: 1,
                monitorHandle: Monitor,
                openOutputs: dxgi.Open,
                refreshRateHz: 60,
                width: 1
            ));
            Assert.Null(@object: feed);
            Assert.Equal(actual: allocations, expected: failingAllocation);
            Assert.Equal(expected: (attempt + 1), actual: dxgi.Factories);
            Assert.Equal(expected: 0, actual: dxgi.Live);
        }
    }
    [InlineData(DisplayColorSpace.Srgb, DisplayColorSpace.Hdr10)]
    [InlineData(DisplayColorSpace.Hdr10, DisplayColorSpace.Srgb)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
    [Theory]
    public void A_static_capture_detects_an_hdr_toggle_without_another_frame(DisplayColorSpace opened, DisplayColorSpace current) {
        var dxgi = new FakeDxgi(colorSpace: opened);
        using var probe = new Win32DisplayColorSpaceProbe(openOutputs: dxgi.Open);
        var display = new Win32CaptureDisplay(colorSpace: probe.ColorSpaceOf(monitorHandle: Monitor));
        var queued = new Queue<Action>();
        var ended = false;
        var poll = new Win32CaptureDisplayPoll(
            check: () => ended = !display.IsCurrent(colorSpace: probe.ColorSpaceOf(monitorHandle: Monitor)),
            queue: queued.Enqueue
        );

        poll.Poll(milliseconds: 0);
        queued.Dequeue()();
        // The toggle follows the last check. No frame arrives once the desktop is still.
        dxgi.Reconfigure(colorSpace: current);
        poll.Poll(milliseconds: 50);
        Assert.Empty(collection: queued);
        poll.Poll(milliseconds: 100);
        Assert.False(condition: ended); // Discovery runs on the queued worker, not the consumer.
        queued.Dequeue()();

        Assert.True(condition: ended);
        Assert.Equal(expected: 2, actual: dxgi.Factories);
        Assert.Equal(expected: 2, actual: dxgi.Live);
    }
    [Fact]
    public void A_slow_display_check_does_not_accumulate_workers() {
        var queued = new Queue<Action>();
        var checks = 0;
        var poll = new Win32CaptureDisplayPoll(check: () => checks++, queue: queued.Enqueue);

        for (var milliseconds = 0; (milliseconds <= 1000); milliseconds += 100) {
            poll.Poll(milliseconds: milliseconds);
        }

        Assert.Single(collection: queued);
        Assert.Equal(actual: checks, expected: 0);
        queued.Dequeue()();
        poll.Poll(milliseconds: 1100);
        queued.Dequeue()();
        Assert.Equal(actual: checks, expected: 2);
    }
    [Fact]
    public void A_steady_capture_creates_no_factory_per_poll() {
        var dxgi = new FakeDxgi(colorSpace: DisplayColorSpace.Srgb);

        using (var probe = new Win32DisplayColorSpaceProbe(openOutputs: dxgi.Open)) {
            for (var poll = 0; (poll < Polls); poll++) {
                Assert.Equal(expected: DisplayColorSpace.Srgb, actual: probe.ColorSpaceOf(monitorHandle: Monitor));
            }
        }

        Assert.Equal(expected: 1, actual: dxgi.Factories);
        Assert.Equal(expected: 1, actual: dxgi.Enumerations);
        Assert.Equal(expected: Polls, actual: dxgi.Reads);
        Assert.Equal(expected: 0, actual: dxgi.Live);
    }
    [Fact]
    public void A_stale_factory_is_replaced_and_reports_the_new_configuration() {
        var dxgi = new FakeDxgi(colorSpace: DisplayColorSpace.Srgb);

        using var probe = new Win32DisplayColorSpaceProbe(openOutputs: dxgi.Open);

        Assert.Equal(expected: DisplayColorSpace.Srgb, actual: probe.ColorSpaceOf(monitorHandle: Monitor));
        // HDR turns on: the held factory goes stale, and only a new one describes the display as it now presents.
        dxgi.Reconfigure(colorSpace: DisplayColorSpace.Hdr10);
        Assert.Equal(expected: DisplayColorSpace.Hdr10, actual: probe.ColorSpaceOf(monitorHandle: Monitor));
        Assert.Equal(expected: DisplayColorSpace.Hdr10, actual: probe.ColorSpaceOf(monitorHandle: Monitor));
        Assert.Equal(expected: 2, actual: dxgi.Factories);
        Assert.Equal(expected: 2, actual: dxgi.Enumerations);
        // The stale factory and its output are released when the new ones open.
        Assert.Equal(expected: 2, actual: dxgi.Live);
    }
    [Fact]
    public void A_different_monitor_is_found_again_through_the_held_factory() {
        var dxgi = new FakeDxgi(colorSpace: DisplayColorSpace.Srgb);

        using var probe = new Win32DisplayColorSpaceProbe(openOutputs: dxgi.Open);

        _ = probe.ColorSpaceOf(monitorHandle: Monitor);
        _ = probe.ColorSpaceOf(monitorHandle: OtherMonitor);
        _ = probe.ColorSpaceOf(monitorHandle: OtherMonitor);
        Assert.Equal(expected: 1, actual: dxgi.Factories);
        Assert.Equal(expected: 2, actual: dxgi.Enumerations);
        Assert.Equal(expected: 2, actual: dxgi.Live);
    }
    [Fact]
    public void Discovery_that_cannot_open_a_factory_or_find_the_output_reports_no_color_space() {
        var noFactory = new FakeDxgi(colorSpace: DisplayColorSpace.Srgb) { FailsToOpen = true };
        var noOutput = new FakeDxgi(colorSpace: DisplayColorSpace.Srgb) { FindsNoOutput = true };

        using (var probe = new Win32DisplayColorSpaceProbe(openOutputs: noFactory.Open)) {
            Assert.Null(@object: probe.ColorSpaceOf(monitorHandle: Monitor));
        }
        using (var probe = new Win32DisplayColorSpaceProbe(openOutputs: noOutput.Open)) {
            Assert.Null(@object: probe.ColorSpaceOf(monitorHandle: Monitor));
        }

        Assert.Equal(expected: 0, actual: noOutput.Live);
    }

    // A DXGI whose factories count themselves: each holds the configuration it was opened under and goes stale when the
    // configuration changes, as IDXGIFactory1::IsCurrent does.
    private sealed class FakeDxgi(DisplayColorSpace colorSpace) {
        private DisplayColorSpace m_colorSpace = colorSpace;
        private int m_configuration;

        public int Enumerations { get; private set; }
        public int Factories { get; private set; }
        public bool FailsToOpen { get; init; }
        public bool FindsNoOutput { get; init; }
        public int Live { get; private set; }
        public int Reads { get; private set; }

        public IDisplayAdapterOutputs? Open() {
            if (FailsToOpen) {
                return null;
            }

            Factories++;
            Live++;
            return new Outputs(
                configuration: m_configuration,
                dxgi: this
            );
        }
        public void Reconfigure(DisplayColorSpace colorSpace) {
            m_colorSpace = colorSpace;
            m_configuration++;
        }

        private sealed class Outputs(FakeDxgi dxgi, int configuration) : IDisplayAdapterOutputs {
            private readonly DisplayColorSpace m_colorSpace = dxgi.m_colorSpace;

            public bool IsCurrent => (configuration == dxgi.m_configuration);

            public IDisplayAdapterOutput? FindOutput(nint monitorHandle) {
                dxgi.Enumerations++;
                if (dxgi.FindsNoOutput) {
                    return null;
                }

                dxgi.Live++;
                return new Output(
                    colorSpace: m_colorSpace,
                    dxgi: dxgi
                );
            }
            public void Dispose() => dxgi.Live--;
        }
        private sealed class Output(FakeDxgi dxgi, DisplayColorSpace colorSpace) : IDisplayAdapterOutput {
            public DisplayColorSpace? ReadColorSpace() {
                dxgi.Reads++;
                return colorSpace;
            }
            public void Dispose() => dxgi.Live--;
        }
    }
}
