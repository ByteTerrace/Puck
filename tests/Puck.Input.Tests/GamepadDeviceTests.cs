using System.Collections.Concurrent;
using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Output;

namespace Puck.Input.Tests;

public sealed class GamepadDeviceTests {
    [Fact]
    public async Task Zero_duration_rumble_is_an_immediate_stop() {
        using var hid = new TestHidDevice();
        var parser = new TestParser();

        hid.EnqueueReport(1);
        using var device = CreateDevice(hid: hid, parser: parser);

        device.Start();
        await TestWait.UntilAsync(condition: () => device.HasStream);

        var effect = new RumbleEffect(DurationMilliseconds: 0u, HighFrequency: 0.5f, LowFrequency: 1f);

        Assert.True(condition: device.Output.Rumble(effect: in effect));
        await TestWait.UntilAsync(condition: () => (parser.RumbleWrites.Count != 0));

        Assert.Equal(expected: (0f, 0f), actual: parser.RumbleWrites[^1]);
    }
    [Fact]
    public async Task Scheduled_trigger_effect_fires_without_another_input_report() {
        using var hid = new TestHidDevice();
        var parser = new TestParser();
        var clock = new ManualInputClock { NowTicks = 1UL, };

        hid.EnqueueReport(1);
        using var device = CreateDevice(hid: hid, parser: parser, clock: clock);

        device.Start();
        await TestWait.UntilAsync(condition: () => device.HasStream);

        var effect = TriggerEffectSpec.Feedback(position: 2, strength: 3);

        Assert.True(condition: device.Output.SetTriggerEffectAt(left: in effect, right: in effect, fireAtTick: 5UL));
        clock.NowTicks = 5UL;

        await TestWait.UntilAsync(condition: () => (parser.TriggerWrites.Count != 0));
        Assert.Equal(expected: effect, actual: parser.TriggerWrites[0].Left);
    }
    [Fact]
    public async Task Silent_receiver_releases_stream_and_resets_parser_state() {
        using var hid = new TestHidDevice();
        var parser = new TestParser();

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            activateOnStream: true,
            hid: hid,
            parser: parser,
            receiverSilenceTimeoutMilliseconds: 25
        );

        device.Start();
        await TestWait.UntilAsync(condition: () => device.HasStream);
        await TestWait.UntilAsync(condition: () => !device.HasStream);

        Assert.True(condition: (parser.ResetCount > 0));
        var effect = new RumbleEffect(DurationMilliseconds: 100u, HighFrequency: 1f, LowFrequency: 1f);

        Assert.False(condition: device.Output.Rumble(effect: in effect));
    }
    [Fact]
    public async Task Silent_park_returns_running_motors_to_rest() {
        using var hid = new TestHidDevice();
        var parser = new TestParser();

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            activateOnStream: true,
            hid: hid,
            parser: parser,
            receiverSilenceTimeoutMilliseconds: 25
        );

        device.Start();
        await TestWait.UntilAsync(condition: () => device.HasStream);

        // A rumble outlasting the silence window would otherwise leave the pad's motors running on the last
        // written speed after the park discards the tracked expiry.
        var effect = new RumbleEffect(DurationMilliseconds: 60_000u, HighFrequency: 1f, LowFrequency: 1f);

        Assert.True(condition: device.Output.Rumble(effect: in effect));

        // Feed reports until the write lands so the silence park cannot race the enqueued command; then let
        // the stream go silent.
        await TestWait.UntilAsync(condition: () => {
            if (parser.RumbleWrites.Count != 0) {
                return true;
            }

            hid.EnqueueReport(1);

            return false;
        });
        await TestWait.UntilAsync(condition: () => !device.HasStream);

        Assert.Equal(expected: (0f, 0f), actual: parser.RumbleWrites[^1]);
    }
    [Fact]
    public async Task Dispose_waits_for_the_pending_read_before_closing_the_handle() {
        var hid = new TestHidDevice();
        var parser = new TestParser();
        var device = CreateDevice(hid: hid, parser: parser);

        device.Start();
        await hid.ReadEntered.Task.WaitAsync(timeout: TimeSpan.FromSeconds(value: 2), cancellationToken: TestContext.Current.CancellationToken);
        device.Dispose();

        Assert.True(condition: hid.IsDisposed);
        Assert.False(condition: hid.DisposedWhileReading);
        Assert.Equal(expected: 1, actual: parser.DisposeCount);
    }
    [Fact]
    public async Task Dispose_force_closes_an_uncooperative_read_before_releasing_loop_resources() {
        var diagnostics = new ConcurrentQueue<string>();
        var hid = new TestHidDevice { BlockReadUntilDisposed = true, };
        var parser = new TestParser();
        var device = CreateDevice(
            diagnostics: diagnostics.Enqueue,
            hid: hid,
            parser: parser
        );

        device.Start();
        await hid.ReadEntered.Task.WaitAsync(timeout: TimeSpan.FromSeconds(value: 2), cancellationToken: TestContext.Current.CancellationToken);
        device.Dispose();

        Assert.True(condition: hid.IsDisposed);
        Assert.True(condition: hid.DisposedWhileReading);
        Assert.Equal(expected: 0, actual: parser.DisposeCount);
        Assert.Contains(
            collection: diagnostics,
            filter: static message => message.Contains(comparisonType: StringComparison.Ordinal, value: "forcing HID close")
        );
    }

    private static GamepadDevice CreateDevice(
        TestHidDevice hid,
        TestParser parser,
        ManualInputClock? clock = null,
        bool activateOnStream = false,
        int receiverSilenceTimeoutMilliseconds = 1000,
        Action<string>? diagnostics = null
    ) => new(
        activateOnStream: activateOnStream,
        clock: (clock ?? new ManualInputClock()),
        deviceId: InputDeviceId.New(),
        diagnostics: diagnostics,
        hid: hid,
        parser: parser,
        playerIndex: (activateOnStream ? -1 : 0),
        receiverSilenceTimeoutMilliseconds: receiverSilenceTimeoutMilliseconds
    );
}
