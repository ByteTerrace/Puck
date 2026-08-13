using System.Numerics;
using Puck.Commands;
using Puck.Input.Devices;

namespace Puck.Input.Tests;

public sealed class CaptureSourceTests {
    private const string Command = "test.touch";

    [Fact]
    public void Touch_release_emits_one_zero_and_clears_the_carried_axis() {
        var router = new InputRouter(
            bindings: new TouchBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: new CommandRegistry(modules: [new AxisModule()])
        );
        var clock = new ManualInputClock { NowTicks = 1UL, };
        var capture = new GamepadCaptureSource(router: router, clock: clock);
        var deviceId = InputDeviceId.New();
        var active = Drain(
            deviceId: deviceId,
            touch: new GamepadTouchPoint(IsActive: true, Id: 7, Position: new Vector2(x: 0.4f, y: 0.6f))
        );

        capture.Capture(drains: [active]);
        var activeEntry = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);
        Assert.Equal(expected: new Vector2(x: 0.4f, y: 0.6f), actual: activeEntry.Value.AsAxis2D);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: Command));

        clock.NowTicks = 2UL;
        capture.Capture(drains: [Drain(deviceId: deviceId, touch: default)]);
        var releaseEntries = Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries;
        var releaseEntry = Assert.Single(collection: releaseEntries, predicate: static entry => !entry.Value.IsActive);

        Assert.Equal(expected: Vector2.Zero, actual: releaseEntry.Value.AsAxis2D);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: Command));

        clock.NowTicks = 3UL;
        capture.Capture(drains: [Drain(deviceId: deviceId, touch: default)]);
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    [Fact]
    public void Departed_device_pays_the_release_edges_it_still_owes() {
        var router = new InputRouter(
            bindings: new TouchBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: new CommandRegistry(modules: [new AxisModule()])
        );
        var clock = new ManualInputClock { NowTicks = 1UL, };
        var capture = new GamepadCaptureSource(router: router, clock: clock);
        var deviceId = InputDeviceId.New();

        capture.Capture(drains: [Drain(
            deviceId: deviceId,
            touch: new GamepadTouchPoint(IsActive: true, Id: 3, Position: new Vector2(x: 0.2f, y: 0.8f))
        )]);
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: Command));

        // The device drops out of the drain list entirely (unplug, fault prune, or receiver park): the
        // carried axis must be cleared by the capture layer, because the device can no longer report rest.
        clock.NowTicks = 2UL;
        capture.Capture(drains: []);
        var releaseEntries = Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries;

        Assert.Single(collection: releaseEntries, predicate: static entry => !entry.Value.IsActive);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: Command));

        clock.NowTicks = 3UL;
        capture.Capture(drains: []);
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    [Fact]
    public void Focus_rejected_device_pays_the_release_edges_it_still_owes() {
        var activeForCapture = true;
        var router = new InputRouter(
            bindings: new TouchBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: new CommandRegistry(modules: [new AxisModule()])
        );
        var clock = new ManualInputClock { NowTicks = 1UL, };
        var capture = new GamepadCaptureSource(
            router: router,
            clock: clock,
            isActiveFor: _ => activeForCapture
        );
        var deviceId = InputDeviceId.New();
        var active = Drain(
            deviceId: deviceId,
            touch: new GamepadTouchPoint(IsActive: true, Id: 9, Position: new Vector2(x: 0.3f, y: 0.7f))
        );

        capture.Capture(drains: [active]);
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: Command));

        // The device is still physically present in the drain, but this consumer no longer owns it. Its carried
        // state must be released just as if the device had departed.
        activeForCapture = false;
        clock.NowTicks = 2UL;
        capture.Capture(drains: [active]);
        var releaseEntries = Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries;

        Assert.Single(collection: releaseEntries, predicate: static entry => !entry.Value.IsActive);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: Command));

        clock.NowTicks = 3UL;
        capture.Capture(drains: [active]);
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    private static GamepadDrain Drain(InputDeviceId deviceId, GamepadTouchPoint touch) => new(
        DeviceId: deviceId,
        Gyro: Vector3.Zero,
        Latest: GamepadState.Neutral with { Touch0 = touch, },
        Pressed: GamepadButtons.None,
        PressEdges: default,
        Released: GamepadButtons.None
    );

    private sealed class TouchBindings : IInputBindings {
        private static readonly CommandBinding[] Bindings = [new(Command: Command)];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) =>
            ((source == InputSources.Gamepad.Touchpad0) ? Bindings : null);
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }

    private sealed class AxisModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Bindable,
                description: "Touch axis probe.",
                handler: static _ => CommandResult.None,
                name: Command,
                valueKind: CommandValueKind.Axis2D
            );
        }
    }
}
