using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Output;
using Puck.Platform.Windows.Gamepad;
using Puck.Testing;

namespace Puck.Input.Tests;

/// <summary>Pins the Xbox connection's rumble scheduling, its disposed behavior and its headset reading. The
/// GameInput COM binding and the poll thread need a physical pad and cannot be driven from here.</summary>
public sealed class XboxConnectionTests {
    private static XInputGamepadConnection Open(TimeProvider? time = null) =>
        new(
            clock: new ManualInputClock(),
            deviceId: InputDeviceId.New(),
            haptics: new GameInputHaptics(),    // never initialized: it only grants the trigger-rumble capability
            playerIndex: 0,
            slot: 0u,
            timeProvider: time,
            type: GamepadType.XboxSeries
        );

    [Fact]
    public void Body_and_trigger_rumble_expire_independently() {
        var time = new VirtualClock();
        using var connection = Open(time: time);

        Assert.True(condition: connection.Output.Rumble(effect: new RumbleEffect(DurationMilliseconds: 5000u, HighFrequency: 0.5f, LowFrequency: 1f)));
        Assert.True(condition: connection.Output.RumbleTriggers(effect: new TriggerRumbleEffect(DurationMilliseconds: 100u, Left: 1f, Right: 0.25f)));
        Assert.True(condition: connection.TryTakeRumble(rumble: out var running));
        Assert.Equal(expected: 1f, actual: running.LowFrequency);
        Assert.Equal(expected: 1f, actual: running.LeftTrigger);

        time.Advance(by: TimeSpan.FromMilliseconds(value: 99));

        Assert.False(condition: connection.TryTakeRumble(rumble: out _));

        time.Advance(by: TimeSpan.FromMilliseconds(value: 1));

        Assert.True(condition: connection.TryTakeRumble(rumble: out var triggersExpired));
        Assert.Equal(expected: 1f, actual: triggersExpired.LowFrequency);
        Assert.Equal(expected: 0.5f, actual: triggersExpired.HighFrequency);
        Assert.Equal(expected: 0f, actual: triggersExpired.LeftTrigger);
        Assert.Equal(expected: 0f, actual: triggersExpired.RightTrigger);

        time.Advance(by: TimeSpan.FromMilliseconds(value: 4900));

        Assert.True(condition: connection.TryTakeRumble(rumble: out var allExpired));
        Assert.Equal(expected: default(GameInputRumbleParams), actual: allExpired);
    }
    [Fact]
    public void A_zero_duration_trigger_effect_stops_only_the_triggers() {
        using var connection = Open();

        Assert.True(condition: connection.Output.Rumble(effect: new RumbleEffect(DurationMilliseconds: 5000u, HighFrequency: 0.5f, LowFrequency: 1f)));
        Assert.True(condition: connection.Output.RumbleTriggers(effect: new TriggerRumbleEffect(DurationMilliseconds: 5000u, Left: 1f, Right: 1f)));
        Assert.True(condition: connection.TryTakeRumble(rumble: out _));
        Assert.True(condition: connection.Output.RumbleTriggers(effect: new TriggerRumbleEffect(DurationMilliseconds: 0u, Left: 1f, Right: 1f)));
        Assert.True(condition: connection.TryTakeRumble(rumble: out var stopped));

        Assert.Equal(expected: 1f, actual: stopped.LowFrequency);
        Assert.Equal(expected: 0.5f, actual: stopped.HighFrequency);
        Assert.Equal(expected: 0f, actual: stopped.LeftTrigger);
        Assert.Equal(expected: 0f, actual: stopped.RightTrigger);
    }
    [Fact]
    public void A_zero_duration_body_effect_leaves_the_triggers_running() {
        using var connection = Open();

        Assert.True(condition: connection.Output.Rumble(effect: new RumbleEffect(DurationMilliseconds: 5000u, HighFrequency: 0.5f, LowFrequency: 1f)));
        Assert.True(condition: connection.Output.RumbleTriggers(effect: new TriggerRumbleEffect(DurationMilliseconds: 5000u, Left: 1f, Right: 0.5f)));
        Assert.True(condition: connection.TryTakeRumble(rumble: out _));
        Assert.True(condition: connection.Output.Rumble(effect: RumbleEffect.Off));
        Assert.True(condition: connection.TryTakeRumble(rumble: out var stopped));

        Assert.Equal(expected: 0f, actual: stopped.LowFrequency);
        Assert.Equal(expected: 0f, actual: stopped.HighFrequency);
        Assert.Equal(expected: 1f, actual: stopped.LeftTrigger);
        Assert.Equal(expected: 0.5f, actual: stopped.RightTrigger);
    }
    [Fact]
    public void A_disposed_connection_ignores_polled_state_and_output() {
        var connection = Open();
        var state = new XInputState { PacketNumber = 1, Gamepad = new XInputGamepad { Buttons = 0x1000, }, };

        Assert.True(condition: connection.Output.Rumble(effect: new RumbleEffect(DurationMilliseconds: 5000u, HighFrequency: 1f, LowFrequency: 1f)));

        connection.Dispose();
        connection.Apply(state: in state);
        connection.ApplyBattery(information: new XInputBatteryInformation { BatteryLevel = 3, BatteryType = XInput.BatteryTypeAlkaline, });
        connection.ApplyHeadset(information: new XInputBatteryInformation { BatteryType = XInput.BatteryTypeWired, });
        connection.ServiceOutput(soleConnection: false);

        Assert.False(condition: connection.TryTakeRumble(rumble: out var rumble));
        Assert.Equal(expected: default(GameInputRumbleParams), actual: rumble);
        Assert.Equal(expected: 0UL, actual: connection.Coalescer.Latest.SequenceNumber);
        Assert.True(condition: connection.IsFaulted);

        connection.Dispose();
    }
    [Fact]
    public void A_headset_report_flows_into_the_published_accessories() {
        using var connection = Open();
        var state = new XInputState { PacketNumber = 7, Gamepad = new XInputGamepad { Buttons = 0x1000, }, };

        Assert.Equal(
            expected: GamepadAccessories.None,
            actual: XInputGamepadConnection.ToAccessories(information: new XInputBatteryInformation { BatteryType = XInput.BatteryTypeDisconnected, })
        );

        connection.Apply(state: in state);

        Assert.Equal(expected: GamepadAccessories.None, actual: connection.Coalescer.Latest.Accessories);

        connection.ApplyHeadset(information: new XInputBatteryInformation { BatteryLevel = 2, BatteryType = XInput.BatteryTypeAlkaline, });
        connection.Apply(state: in state);

        Assert.Equal(
            expected: (GamepadAccessories.Headphones | GamepadAccessories.Microphone),
            actual: connection.Coalescer.Latest.Accessories
        );

        connection.ApplyHeadset(information: new XInputBatteryInformation { BatteryType = XInput.BatteryTypeDisconnected, });
        connection.Apply(state: in state);

        Assert.Equal(expected: GamepadAccessories.None, actual: connection.Coalescer.Latest.Accessories);
    }
}
