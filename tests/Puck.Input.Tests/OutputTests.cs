using Puck.Commands;
using Puck.Input.Output;
using Puck.Platform.Windows.Gamepad;

namespace Puck.Input.Tests;

public sealed class OutputTests {
    [Fact]
    public void Output_queue_applies_backpressure_at_its_fixed_capacity() {
        var queue = new GamepadOutputQueue();
        var output = new GamepadOutput(
            capabilities: GamepadOutputCapabilities.Rumble,
            deviceId: InputDeviceId.New(),
            queue: queue
        );
        var effect = new RumbleEffect(LowFrequency: 1f, HighFrequency: 1f, DurationMilliseconds: 100u);

        for (var index = 0; (index < GamepadOutputQueue.Capacity); ++index) {
            Assert.True(condition: output.Rumble(effect: in effect));
        }

        Assert.False(condition: output.Rumble(effect: in effect));
    }

    [Fact]
    public void Killed_output_rejects_requests_and_clears_pending_commands() {
        var queue = new GamepadOutputQueue();
        var output = new GamepadOutput(
            capabilities: GamepadOutputCapabilities.Rumble,
            deviceId: InputDeviceId.New(),
            queue: queue
        );
        var effect = new RumbleEffect(LowFrequency: 1f, HighFrequency: 1f, DurationMilliseconds: 100u);

        Assert.True(condition: output.Rumble(effect: in effect));
        output.Kill();

        Assert.False(condition: output.Rumble(effect: in effect));
        Assert.False(condition: queue.TryDequeue(command: out _));
    }

    [Fact]
    public void Suspended_receiver_output_clears_old_requests_and_reopens_cleanly() {
        var queue = new GamepadOutputQueue();
        var output = new GamepadOutput(
            capabilities: GamepadOutputCapabilities.Rumble,
            deviceId: InputDeviceId.New(),
            queue: queue
        );
        var effect = new RumbleEffect(LowFrequency: 1f, HighFrequency: 1f, DurationMilliseconds: 100u);

        Assert.True(condition: output.Rumble(effect: in effect));
        output.Suspend();

        Assert.False(condition: output.Rumble(effect: in effect));
        Assert.False(condition: queue.TryDequeue(command: out _));

        output.Resume();
        Assert.True(condition: output.Rumble(effect: in effect));
    }

    [Fact]
    public void Xbox_zero_duration_stops_every_motor() {
        using var connection = new XInputGamepadConnection(
            deviceId: InputDeviceId.New(),
            haptics: null,
            playerIndex: 0,
            slot: 0u
        );
        var start = new RumbleEffect(LowFrequency: 1f, HighFrequency: 0.5f, DurationMilliseconds: 1000u);
        var stop = new RumbleEffect(LowFrequency: 1f, HighFrequency: 1f, DurationMilliseconds: 0u);

        Assert.True(condition: connection.Output.Rumble(effect: in start));
        Assert.True(condition: connection.TryTakeRumble(rumble: out var running));
        Assert.True(condition: running.LowFrequency > 0f);
        Assert.True(condition: connection.Output.Rumble(effect: in stop));
        Assert.True(condition: connection.TryTakeRumble(rumble: out var stopped));

        Assert.Equal(expected: 0f, actual: stopped.LowFrequency);
        Assert.Equal(expected: 0f, actual: stopped.HighFrequency);
        Assert.Equal(expected: 0f, actual: stopped.LeftTrigger);
        Assert.Equal(expected: 0f, actual: stopped.RightTrigger);
    }
}
