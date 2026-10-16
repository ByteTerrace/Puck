using System.Collections.Concurrent;
using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Output;

namespace Puck.Input.Tests;

/// <summary>The device loop's deadlines run on the transport's virtual clock, and every observation waits for the
/// loop to park on its next read, so each verdict follows from the order of events rather than from wall time. Each
/// advance lands exactly on the parked read's expiry, the instant a system clock would fire it. Read 1 consumes the
/// report enqueued before start; read 2 is the first steady-state poll.</summary>
public sealed class GamepadDeviceTests {
    private static readonly TimeSpan SteadyStatePoll = TimeSpan.FromMilliseconds(value: GamepadDevice.SteadyStatePollMilliseconds);

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
        playerIndex: (activateOnStream
        ? -1
        : 0),
        receiverSilenceTimeoutMilliseconds: receiverSilenceTimeoutMilliseconds,
        timeProvider: hid.Time
    );
    private static Task DisposeOnItsOwnThread(GamepadDevice device) =>
        Task.Factory.StartNew(
            action: device.Dispose,
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

    [Fact]
    public async Task Dispose_force_closes_an_uncooperative_read_only_once_the_join_deadline_expires() {
        var cancellationToken = TestContext.Current.CancellationToken;
        var diagnostics = new ConcurrentQueue<string>();
        var hid = new TestHidDevice { BlockReadUntilDisposed = true, };
        var parser = new TestParser();
        var device = CreateDevice(
            diagnostics: diagnostics.Enqueue,
            hid: hid,
            parser: parser
        );
        var joinDeadline = TimeSpan.FromMilliseconds(value: GamepadDevice.DisposeJoinTimeoutMilliseconds);

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 1
        );

        var disposal = DisposeOnItsOwnThread(device: device);

        // The disposer is now blocked on a join nothing but the deadline can end: the read ignores cancellation.
        await hid.Time.WhenArmedAsync(
            count: 1,
            ct: cancellationToken,
            dueTime: joinDeadline
        );
        Assert.False(condition: hid.IsDisposed);

        hid.Time.Advance(by: joinDeadline);
        await disposal.WaitAsync(cancellationToken: cancellationToken);

        Assert.True(condition: hid.IsDisposed);
        Assert.True(condition: hid.DisposedWhileReading);
        Assert.Equal(
            expected: 0,
            actual: parser.DisposeCount
        );
        Assert.Contains(
            collection: diagnostics,
            filter: static message => message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "forcing HID close"
            )
        );
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: static message => message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "remained live"
            )
        );
    }
    [Fact]
    public async Task Dispose_waits_for_the_pending_read_before_closing_the_handle() {
        var cancellationToken = TestContext.Current.CancellationToken;
        var hid = new TestHidDevice();
        var parser = new TestParser();
        var device = CreateDevice(
            hid: hid,
            parser: parser
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 1
        );
        // The join deadline never expires here, so only the loop observing cancellation can end the join.
        await DisposeOnItsOwnThread(device: device).WaitAsync(cancellationToken: cancellationToken);

        Assert.True(condition: hid.IsDisposed);
        Assert.False(condition: hid.DisposedWhileReading);
        Assert.Equal(
            expected: 1,
            actual: parser.DisposeCount
        );
    }
    [Fact]
    public async Task Finite_rumble_returns_the_motors_to_rest_exactly_when_its_duration_elapses() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser();

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            hid: hid,
            parser: parser
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 2
        );

        var effect = new RumbleEffect(
            DurationMilliseconds: 40u,
            HighFrequency: 1f,
            LowFrequency: 1f
        );

        Assert.True(condition: device.Output.Rumble(effect: in effect));
        // Read 2 expires and the loop writes the rumble at t = 16 ms, so the motors are due to rest at t = 56 ms.
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 3
        );
        // Reads 3 and 4 poll at the steady rate, expiring at t = 32 ms and t = 48 ms.
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 4
        );
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 5
        );

        // Read 5 polls only for the 8 ms remainder, so no read expires before the motors are due to rest.
        var remainder = TimeSpan.FromMilliseconds(value: 8);

        Assert.Equal(
            expected: 1,
            actual: hid.Time.Armed(dueTime: remainder)
        );
        hid.Time.Advance(by: (remainder - TimeSpan.FromMilliseconds(value: 1)));
        Assert.Equal(
            expected: [(1f, 1f)],
            actual: parser.RumbleWrites
        );

        hid.Time.Advance(by: TimeSpan.FromMilliseconds(value: 1));
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 6
        );
        Assert.Equal(
            expected: [(1f, 1f), (0f, 0f)],
            actual: parser.RumbleWrites
        );
    }
    [Fact]
    public async Task A_weaker_rumble_inside_the_write_interval_is_held_until_the_interval_passes() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser { RumbleWriteInterval = TimeSpan.FromMilliseconds(value: 30), };

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            hid: hid,
            parser: parser
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 2
        );
        Assert.True(condition: device.Output.Rumble(effect: new RumbleEffect(LowFrequency: 1f, HighFrequency: 1f, DurationMilliseconds: 10_000u)));
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 3
        );
        Assert.True(condition: device.Output.Rumble(effect: new RumbleEffect(LowFrequency: 0.5f, HighFrequency: 0.5f, DurationMilliseconds: 10_000u)));
        // Written at t = 16 ms, so the weaker update dequeued at t = 32 ms waits for t = 46 ms.
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 4
        );
        Assert.Equal(
            expected: [(1f, 1f)],
            actual: parser.RumbleWrites
        );
        Assert.Equal(
            expected: 1,
            actual: hid.Time.Armed(dueTime: TimeSpan.FromMilliseconds(value: 14))
        );

        hid.Time.Advance(by: TimeSpan.FromMilliseconds(value: 14));
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 5
        );
        Assert.Equal(
            expected: [(1f, 1f), (0.5f, 0.5f)],
            actual: parser.RumbleWrites
        );
        Assert.True(condition: (parser.ServiceCount > 0));
    }
    [Fact]
    public async Task A_refreshed_controller_hears_its_rumble_every_interval_and_its_stop_three_more_times() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser { RumbleRefreshInterval = TimeSpan.FromMilliseconds(value: 40), };
        var ordinal = 2;
        var shortStep = TimeSpan.FromMilliseconds(value: 8);

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            hid: hid,
            parser: parser
        );

        async Task Step(TimeSpan by) {
            hid.Time.Advance(by: by);
            await hid.WhenReadAsync(
                cancellationToken: cancellationToken,
                ordinal: ++ordinal
            );
        }

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: ordinal
        );
        Assert.True(condition: device.Output.Rumble(effect: new RumbleEffect(LowFrequency: 1f, HighFrequency: 1f, DurationMilliseconds: 10_000u)));
        await Step(by: SteadyStatePoll);    // t = 16: written
        await Step(by: SteadyStatePoll);    // t = 32
        await Step(by: SteadyStatePoll);    // t = 48
        await Step(by: shortStep);          // t = 56: refreshed
        Assert.Equal(
            expected: [(1f, 1f), (1f, 1f)],
            actual: parser.RumbleWrites
        );

        Assert.True(condition: device.Output.Rumble(effect: RumbleEffect.Off));
        await Step(by: SteadyStatePoll);    // t = 72: stopped

        // The stop is rewritten at t = 112, 152 and 192 ms; after that the loop polls at its steady rate.
        for (var repeat = 0; (repeat < 3); ++repeat) {
            await Step(by: SteadyStatePoll);
            await Step(by: SteadyStatePoll);
            await Step(by: shortStep);
        }

        await Step(by: SteadyStatePoll);
        await Step(by: SteadyStatePoll);
        await Step(by: SteadyStatePoll);

        Assert.Equal(
            expected: [(1f, 1f), (1f, 1f), (0f, 0f), (0f, 0f), (0f, 0f), (0f, 0f)],
            actual: parser.RumbleWrites
        );
    }
    [Fact]
    public async Task A_streaming_controller_that_falls_silent_faults() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser { StreamSilenceTimeout = TimeSpan.FromMilliseconds(value: 32), };

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            hid: hid,
            parser: parser
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 2
        );
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 3
        );
        Assert.False(condition: device.IsFaulted);

        hid.Time.Advance(by: SteadyStatePoll);

        for (var attempt = 0; ((attempt < 200) && !device.IsFaulted); ++attempt) {
            await Task.Delay(
                cancellationToken: cancellationToken,
                millisecondsDelay: 5
            );
        }

        Assert.True(condition: device.IsFaulted);
    }
    [Fact]
    public async Task Disposal_stops_running_motors_and_releases_adaptive_triggers() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser();

        hid.EnqueueReport(1);

        var device = CreateDevice(
            hid: hid,
            parser: parser
        );
        var effect = TriggerEffectSpec.Feedback(
            position: 2,
            strength: 3
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 2
        );
        Assert.True(condition: device.Output.Rumble(effect: new RumbleEffect(LowFrequency: 1f, HighFrequency: 1f, DurationMilliseconds: 10_000u)));
        Assert.True(condition: device.Output.SetTriggerEffect(left: in effect, right: in effect));
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 3
        );

        await DisposeOnItsOwnThread(device: device);

        Assert.Equal(
            expected: [(1f, 1f), (0f, 0f)],
            actual: parser.RumbleWrites
        );
        Assert.Equal(
            expected: (TriggerEffectSpec.Off, TriggerEffectSpec.Off),
            actual: parser.TriggerWrites[^1]
        );
    }
    [Fact]
    public async Task Typed_output_reaches_an_hd_parser_and_a_plain_parser_gets_band_amplitudes() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        using var plainHid = new TestHidDevice();
        var parser = new TestHdParser();
        var plain = new TestParser();
        var band = new HdRumbleSide(High: new HdRumbleBand(Amplitude: 0.25f, FrequencyHertz: 900f), Low: new HdRumbleBand(Amplitude: 2f, FrequencyHertz: 60f));
        var effect = new HdRumbleEffect(DurationMilliseconds: 10_000u, Left: band, Right: HdRumbleSide.Rest);

        hid.EnqueueReport(1);
        plainHid.EnqueueReport(1);
        using var device = new GamepadDevice(clock: new ManualInputClock(), deviceId: InputDeviceId.New(), hid: hid, parser: parser, playerIndex: 0, timeProvider: hid.Time);
        using var plainDevice = CreateDevice(hid: plainHid, parser: plain);

        device.Start();
        plainDevice.Start();
        await hid.WhenReadAsync(cancellationToken: cancellationToken, ordinal: 2);
        await plainHid.WhenReadAsync(cancellationToken: cancellationToken, ordinal: 2);

        Assert.True(condition: device.Output.Capabilities.HasFlag(flag: GamepadOutputCapabilities.HdRumble | GamepadOutputCapabilities.HomeLedPattern | GamepadOutputCapabilities.TrackpadHaptics));
        Assert.True(condition: device.Output.RumbleHd(effect: in effect));
        Assert.True(condition: device.Output.SetHomeLedPattern(pattern: HomeLedPattern.Breathing(intensity: 15, period: TimeSpan.FromSeconds(value: 2))));
        Assert.True(condition: device.Output.PlayTrackpadHaptic(effect: TrackpadHapticEffect.LfoTone(side: TrackpadSide.Left, frequencyHertz: 200, durationMilliseconds: 100)));
        Assert.False(condition: device.Output.PlayTrackpadHaptic(effect: TrackpadHapticEffect.Pulse(side: TrackpadSide.Left, onMicroseconds: 1, offMicroseconds: 1, repeat: 1)));
        Assert.False(condition: device.Output.PlayTrackpadHaptic(effect: TrackpadHapticEffect.LfoTone(side: TrackpadSide.Left, frequencyHertz: 200, durationMilliseconds: 100) with { Kind = TrackpadHapticKinds.Tone | TrackpadHapticKinds.Pulse, }));
        Assert.True(condition: plainDevice.Output.RumbleHd(effect: in effect));
        Assert.False(condition: plainDevice.Output.SetHomeLedPattern(pattern: HomeLedPattern.Steady(intensity: 1)));
        hid.Time.Advance(by: SteadyStatePoll);
        plainHid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(cancellationToken: cancellationToken, ordinal: 3);
        await plainHid.WhenReadAsync(cancellationToken: cancellationToken, ordinal: 3);

        var (left, right) = Assert.Single(collection: parser.HdWrites);

        Assert.Equal(expected: 1f, actual: left.Low.Amplitude);
        Assert.Equal(expected: 900f, actual: left.High.FrequencyHertz);
        Assert.Equal(expected: HdRumbleSide.Rest, actual: right);
        Assert.Equal(expected: 2, actual: Assert.Single(collection: parser.Patterns).Steps.Length);
        Assert.Single(collection: parser.Haptics);
        Assert.Equal(expected: [(1f, 0.25f)], actual: plain.RumbleWrites);
    }
    [Fact]
    public async Task Scheduled_trigger_effect_is_held_until_its_tick_and_fires_without_another_input_report() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser();
        var clock = new ManualInputClock { NowTicks = 1UL, };

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            hid: hid,
            parser: parser,
            clock: clock
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 2
        );
        Assert.True(condition: device.HasStream);

        var effect = TriggerEffectSpec.Feedback(
            position: 2,
            strength: 3
        );

        Assert.True(condition: device.Output.SetTriggerEffectAt(
            fireAtTick: 5UL,
            left: in effect,
            right: in effect
        ));
        // The loop dequeues the effect while the capture clock still reads tick 1, so it must hold it.
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 3
        );
        Assert.Empty(collection: parser.TriggerWrites);

        // No report arrives: the next timed poll alone must fire the held effect once its tick is reached.
        clock.NowTicks = 5UL;
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 4
        );
        Assert.Equal(
            expected: effect,
            actual: Assert.Single(collection: parser.TriggerWrites).Left
        );
    }
    [Fact]
    public async Task Silent_park_returns_running_motors_to_rest() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser();

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            activateOnStream: true,
            hid: hid,
            parser: parser,
            receiverSilenceTimeoutMilliseconds: GamepadDevice.SteadyStatePollMilliseconds
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 2
        );

        // A rumble outlasting the silence window would otherwise leave the pad's motors running on the last
        // written speed after the park discards the tracked expiry.
        var effect = new RumbleEffect(
            DurationMilliseconds: 60_000u,
            HighFrequency: 1f,
            LowFrequency: 1f
        );

        Assert.True(condition: device.Output.Rumble(effect: in effect));
        // A report completes read 2 without moving the clock, and the next iteration writes the queued rumble.
        hid.EnqueueReport(1);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 3
        );
        Assert.Equal(
            expected: [(1f, 1f)],
            actual: parser.RumbleWrites
        );

        // Read 3 expires at t = 16 ms, the whole silence window after the last parsed report.
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 4
        );
        Assert.False(condition: device.HasStream);
        Assert.Equal(
            expected: [(1f, 1f), (0f, 0f)],
            actual: parser.RumbleWrites
        );
    }
    [Fact]
    public async Task Silent_receiver_keeps_its_stream_until_the_silence_window_then_releases_it_and_resets_the_parser() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser();

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            activateOnStream: true,
            hid: hid,
            parser: parser,
            receiverSilenceTimeoutMilliseconds: (2 * GamepadDevice.SteadyStatePollMilliseconds)
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 2
        );
        Assert.True(condition: device.HasStream);

        // Read 2 expires at t = 16 ms, short of the 32 ms window, so the stream stays claimed.
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 3
        );
        Assert.True(condition: device.HasStream);
        Assert.Equal(
            expected: 0,
            actual: parser.ResetCount
        );

        // Read 3 expires at t = 32 ms, exactly the window, which releases the stream.
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 4
        );
        Assert.False(condition: device.HasStream);
        Assert.Equal(
            expected: 1,
            actual: parser.ResetCount
        );

        var effect = new RumbleEffect(
            DurationMilliseconds: 100u,
            HighFrequency: 1f,
            LowFrequency: 1f
        );

        Assert.False(condition: device.Output.Rumble(effect: in effect));
    }
    [Fact]
    public async Task Zero_duration_rumble_is_an_immediate_stop() {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hid = new TestHidDevice();
        var parser = new TestParser();

        hid.EnqueueReport(1);
        using var device = CreateDevice(
            hid: hid,
            parser: parser
        );

        device.Start();
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 2
        );

        var effect = new RumbleEffect(
            DurationMilliseconds: 0u,
            HighFrequency: 0.5f,
            LowFrequency: 1f
        );

        Assert.True(condition: device.Output.Rumble(effect: in effect));
        hid.Time.Advance(by: SteadyStatePoll);
        await hid.WhenReadAsync(
            cancellationToken: cancellationToken,
            ordinal: 3
        );

        Assert.Equal(
            expected: [(0f, 0f)],
            actual: parser.RumbleWrites
        );
    }
}
