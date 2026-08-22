using Xunit;

using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a physical camera is an input device on <see cref="PlayerRoster"/> — observed explicitly
/// through <see cref="PlayerRoster.ObserveDevice"/> (never through the router's first-touch door), minted its own
/// <c>camera&lt;N&gt;</c> token independent of the keyboard/mouse/gamepad token spaces, seated by the default camera
/// policy (the lowest occupied, unclaimed, camera-less slot, player 1 first — never creating a player), and moved
/// by the SAME <see cref="PlayerRoster.AssignDevice"/> gesture a gamepad uses despite never having produced a
/// router signal. A camera also never counts toward a slot's device presence/activity bookkeeping, exercised here
/// through <see cref="PlayerRoster.TryClaimSlot"/>'s "already driven by a human device" refusal. A keyboard and a
/// mouse are ordinary roster devices too — classified by <see cref="PlayerRoster.ObserveDeviceKind"/> (the router's
/// own per-signal first-touch classification, called directly here in place of a real <c>InputRouter</c>) and
/// seated through <see cref="PlayerRoster.Confirm(InputDeviceId, WorldPrincipal)"/> exactly like a gamepad's first
/// press. When several devices of one kind share a slot, <see cref="PlayerRoster.TryGetSeatDevice"/> resolves
/// whichever was assigned to it most recently.
/// </summary>
public sealed class PlayerRosterDeviceRosterLawTests {
    private static WorldDefinition SingleActiveSeatDocument() {
        var baseDefinition = Fixtures.BuildDocument();

        return baseDefinition with {
            PopulationRaw = baseDefinition.Population with {
                SeatActivationRaw = [
                    SeatActivationPolicy.Eager,
                    SeatActivationPolicy.OnDemand,
                    SeatActivationPolicy.OnDemand,
                    SeatActivationPolicy.OnDemand,
                ],
            },
        };
    }
    private static WorldDefinition TwoActiveSeatsDocument() {
        var baseDefinition = Fixtures.BuildDocument();

        return baseDefinition with {
            PopulationRaw = baseDefinition.Population with {
                SeatActivationRaw = [
                    SeatActivationPolicy.Eager,
                    SeatActivationPolicy.Eager,
                    SeatActivationPolicy.OnDemand,
                    SeatActivationPolicy.OnDemand,
                ],
            },
        };
    }
    private static PlayerRoster BuildRoster(WorldFixture fixture) => new(
        definition: fixture.Server.Definition,
        link: new LoopbackTransport(server: fixture.Server),
        seatBindings: new WorldSeatBindings(definition: fixture.Server.Definition)
    );

    [Fact]
    public void ObservingTwoCameras_MintsPerKindTokens_AndSeatsOnlyTheFirstByDefault() {
        using var fixture = Fixtures.FreshServer(definition: SingleActiveSeatDocument());
        var roster = BuildRoster(fixture: fixture);
        var brio = InputDeviceId.FromKey(key: "camera:brio");
        var c920 = InputDeviceId.FromKey(key: "camera:c920");

        roster.ObserveDevice(device: brio, kind: InputDeviceKind.Camera, name: "Logitech BRIO");
        roster.ObserveDevice(device: c920, kind: InputDeviceKind.Camera, name: "HD Pro Webcam C920");

        Assert.Equal(expected: "camera1", actual: roster.DeviceToken(device: brio));
        Assert.Equal(expected: "camera2", actual: roster.DeviceToken(device: c920));
        Assert.True(condition: roster.TryResolveDeviceToken(token: "camera1", device: out var resolvedBrio));
        Assert.Equal(expected: brio, actual: resolvedBrio);
        Assert.True(condition: roster.TryResolveDeviceToken(token: "camera2", device: out var resolvedC920));
        Assert.Equal(expected: c920, actual: resolvedC920);

        // Only slot 0 (player 1) is occupied by this fixture's document — the default policy attaches the first
        // camera there and leaves the second unassigned rather than ever minting a player for it.
        Assert.True(condition: roster.TryGetSeatDevice(slot: 0, kind: InputDeviceKind.Camera, device: out var seated));
        Assert.Equal(expected: brio, actual: seated);
        Assert.Equal(expected: 0, actual: roster.DeviceSlot(device: brio));
        Assert.Null(@object: roster.DeviceSlot(device: c920));
        Assert.Equal(expected: "Logitech BRIO", actual: roster.DeviceName(device: brio));

        var devices = roster.DescribeDevices();

        Assert.Contains(expectedSubstring: "camera1 'Logitech BRIO'=p1*", actualString: devices);
        Assert.Contains(expectedSubstring: "camera2 'HD Pro Webcam C920'=unassigned", actualString: devices);
    }
    [Fact]
    public void AssignDevice_MovesAnUnassignedCameraOntoAnOccupiedSlot_JoiningThatTeam() {
        using var fixture = Fixtures.FreshServer(definition: SingleActiveSeatDocument());
        var roster = BuildRoster(fixture: fixture);
        var gamepad = InputDeviceId.New();
        var camera = InputDeviceId.FromKey(key: "camera:c920");

        // Occupy slot 1 (display "2") with a pending participant first, without the camera ever crossing the
        // router — it reaches the roster only through ObserveDevice.
        Assert.Equal(expected: AssignOutcome.CreatedPending, actual: roster.AssignDevice(
            device: gamepad,
            targetSlot: 1,
            actingPrincipal: WorldPrincipal.Console
        ));

        roster.ObserveDevice(device: camera, kind: InputDeviceKind.Camera, name: "HD Pro Webcam C920");

        // Slot 0 (player 1, active from boot) is the lowest occupied slot with no camera yet — the default policy
        // attaches this first camera there, exactly as the two-camera law above attaches its first.
        Assert.Equal(expected: 0, actual: roster.DeviceSlot(device: camera));

        // Reassigning it onto slot 1's pending participant is then an ordinary AssignDevice move, joining that team.
        Assert.Equal(expected: AssignOutcome.JoinedTeam, actual: roster.AssignDevice(
            device: camera,
            targetSlot: 1,
            actingPrincipal: WorldPrincipal.Console
        ));
        Assert.Equal(expected: 1, actual: roster.DeviceSlot(device: camera));
    }
    [Fact]
    public void AssignDevice_RejectsAnUnassignedCameraOnAnEmptySlot_WithoutCreatingPresence() {
        using var fixture = Fixtures.FreshServer(definition: SingleActiveSeatDocument());
        var roster = BuildRoster(fixture: fixture);
        var camera1 = InputDeviceId.FromKey(key: "camera:brio");
        var camera2 = InputDeviceId.FromKey(key: "camera:c920");

        roster.ObserveDevice(device: camera1, kind: InputDeviceKind.Camera, name: "Logitech BRIO");
        roster.ObserveDevice(device: camera2, kind: InputDeviceKind.Camera, name: "HD Pro Webcam C920");

        Assert.Null(@object: roster.DeviceSlot(device: camera2));
        Assert.Equal(expected: AssignOutcome.PassiveDeviceTargetEmpty, actual: roster.AssignDevice(
            device: camera2,
            targetSlot: 1,
            actingPrincipal: WorldPrincipal.Console
        ));
        Assert.Null(@object: roster.DeviceSlot(device: camera2));
        Assert.Contains(expectedSubstring: "p2 empty", actualString: roster.Describe());
    }
    [Fact]
    public void PlayerAssignCommand_RefusesAPassiveCameraOnAnEmptySlot_AndHelpExplainsTheException() {
        using var fixture = Fixtures.FreshServer(definition: SingleActiveSeatDocument());
        var roster = BuildRoster(fixture: fixture);
        var camera1 = InputDeviceId.FromKey(key: "camera:brio");
        var camera2 = InputDeviceId.FromKey(key: "camera:c920");
        var definition = PlayerAssignmentCommand.Create(roster: roster);
        var registry = new CommandRegistry(modules: [new SingleCommandModule(definition: definition)]);

        roster.ObserveDevice(device: camera1, kind: InputDeviceKind.Camera, name: "Logitech BRIO");
        roster.ObserveDevice(device: camera2, kind: InputDeviceKind.Camera, name: "HD Pro Webcam C920");

        var result = registry.Submit(line: "player.assign camera2 2");

        Assert.True(condition: result.IsError);
        Assert.Equal(expected: "[player.assign: a camera can join an existing player but cannot create player 2]", actual: result.Output);
        Assert.Null(@object: roster.DeviceSlot(device: camera2));
        Assert.Contains(expectedSubstring: "p2 empty", actualString: roster.Describe());
        Assert.Contains(expectedSubstring: "passive camera is refused because it cannot create a player", actualString: definition.Description);
    }
    [Fact]
    public void AssignDevice_ReassigningACamera_RaisesDeviceSlotChanging_AndVacatesTheOldSeat() {
        using var fixture = Fixtures.FreshServer(definition: TwoActiveSeatsDocument());
        var roster = BuildRoster(fixture: fixture);
        var camera = InputDeviceId.FromKey(key: "camera:brio");
        var raised = new List<InputDeviceId>();

        roster.DeviceSlotChanging += device => raised.Add(item: device);
        roster.ObserveDevice(device: camera, kind: InputDeviceKind.Camera, name: "Logitech BRIO");

        // Both seats are active at boot, so the default policy seated the camera on slot 0 — no move has happened
        // yet, so the event has not fired.
        Assert.True(condition: roster.TryGetSeatDevice(slot: 0, kind: InputDeviceKind.Camera, device: out _));
        Assert.Empty(collection: raised);

        Assert.Equal(expected: AssignOutcome.JoinedTeam, actual: roster.AssignDevice(
            device: camera,
            targetSlot: 1,
            actingPrincipal: WorldPrincipal.Console
        ));

        Assert.Equal(expected: [camera], actual: raised);
        Assert.False(condition: roster.TryGetSeatDevice(slot: 0, kind: InputDeviceKind.Camera, device: out _));
        Assert.True(condition: roster.TryGetSeatDevice(slot: 1, kind: InputDeviceKind.Camera, device: out var moved));
        Assert.Equal(expected: camera, actual: moved);
    }

    private sealed class SingleCommandModule(CommandDefinition definition) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() => [definition];
    }
    [Fact]
    public void ACameraNeverCountsAsDevicePresence_SoAGamepadCanStillClaimASlotItAloneOccupies() {
        using var fixture = Fixtures.FreshServer(definition: TwoActiveSeatsDocument());
        var roster = BuildRoster(fixture: fixture);
        var brio = InputDeviceId.FromKey(key: "camera:brio");
        var c920 = InputDeviceId.FromKey(key: "camera:c920");
        var gamepad = InputDeviceId.New();

        // Slot 0 and slot 1 are both active and camera-less at observation time — the default policy seats one
        // camera on each, so slot 1 ends up occupied by ONLY a camera (no keyboard, no gamepad).
        roster.ObserveDevice(device: brio, kind: InputDeviceKind.Camera, name: "Logitech BRIO");
        roster.ObserveDevice(device: c920, kind: InputDeviceKind.Camera, name: "HD Pro Webcam C920");

        Assert.True(condition: roster.TryGetSeatDevice(slot: 1, kind: InputDeviceKind.Camera, device: out _));

        // TryClaimSlot's "already driven by a human device" refusal must not fire on the camera alone.
        Assert.True(condition: roster.TryClaimSlot(
            device: gamepad,
            fault: out var fault,
            preferredSlot: 1,
            principal: WorldPrincipal.Console,
            slot: out var claimed
        ));
        Assert.Equal(expected: 1, actual: claimed);
        Assert.Null(@object: fault);
    }
    [Fact]
    public void TryGetSeatDevice_ResolvesTheMostRecentlyAssignedDeviceOfAKind() {
        using var fixture = Fixtures.FreshServer(definition: SingleActiveSeatDocument());
        var roster = BuildRoster(fixture: fixture);
        var camera1 = InputDeviceId.FromKey(key: "camera:1");
        var camera2 = InputDeviceId.FromKey(key: "camera:2");

        roster.ObserveDevice(device: camera1, kind: InputDeviceKind.Camera, name: "Camera One");
        Assert.Equal(expected: 0, actual: roster.DeviceSlot(device: camera1));

        // camera2's own default-policy seating never fires (only one active seat, already taken by camera1), so it
        // stays unassigned — classified as a camera, but with no slot — until the explicit AssignDevice below joins
        // it onto that same team; its later assignment stamp then makes it the seat's resolved camera.
        roster.ObserveDevice(device: camera2, kind: InputDeviceKind.Camera, name: "Camera Two");
        Assert.Null(@object: roster.DeviceSlot(device: camera2));

        Assert.Equal(expected: AssignOutcome.JoinedTeam, actual: roster.AssignDevice(
            device: camera2,
            targetSlot: 0,
            actingPrincipal: WorldPrincipal.Console
        ));

        Assert.True(condition: roster.TryGetSeatDevice(slot: 0, kind: InputDeviceKind.Camera, device: out var resolvedAfterCamera2));
        Assert.Equal(expected: camera2, actual: resolvedAfterCamera2);
        Assert.Contains(expectedSubstring: "camera1 'Camera One'=p1 |", actualString: roster.DescribeDevices());
        Assert.Contains(expectedSubstring: "camera2 'Camera Two'=p1*", actualString: roster.DescribeDevices());

        // Re-assigning camera1 onto the SAME slot it already occupies is a NoOp for occupancy, but a deliberate
        // re-assertion still refreshes its stamp — camera1 becomes the seat's resolved camera again.
        Assert.Equal(expected: AssignOutcome.NoOp, actual: roster.AssignDevice(
            device: camera1,
            targetSlot: 0,
            actingPrincipal: WorldPrincipal.Console
        ));

        Assert.True(condition: roster.TryGetSeatDevice(slot: 0, kind: InputDeviceKind.Camera, device: out var resolvedAfterCamera1));
        Assert.Equal(expected: camera1, actual: resolvedAfterCamera1);
    }
    [Fact]
    public void FirstKeyboardAndFirstMouse_SeatWithPlayer1_LikeAGamepadDoes() {
        using var fixture = Fixtures.FreshServer(definition: SingleActiveSeatDocument());
        var roster = BuildRoster(fixture: fixture);
        var keyboard = InputDeviceId.FromKey(key: "keyboard:1");
        var mouse = InputDeviceId.FromKey(key: "mouse:1");

        // ObserveDeviceKind is the router's own per-signal first-touch classification (InputRouter.ApplySignal),
        // called directly here in place of a real router; Confirm is the same first-press door a gamepad's
        // South/confirm button already uses.
        roster.ObserveDeviceKind(device: keyboard, kind: InputDeviceKind.Keyboard);
        Assert.Equal(expected: (ConfirmOutcome.Seated, 0), actual: roster.Confirm(
            device: keyboard,
            actingPrincipal: WorldPrincipal.Console
        ));
        Assert.Equal(expected: "keyboard1", actual: roster.DeviceToken(device: keyboard));

        roster.ObserveDeviceKind(device: mouse, kind: InputDeviceKind.Mouse);
        Assert.Equal(expected: (ConfirmOutcome.Seated, 0), actual: roster.Confirm(
            device: mouse,
            actingPrincipal: WorldPrincipal.Console
        ));
        Assert.Equal(expected: "mouse1", actual: roster.DeviceToken(device: mouse));

        Assert.Equal(expected: 0, actual: roster.DeviceSlot(device: keyboard));
        Assert.Equal(expected: 0, actual: roster.DeviceSlot(device: mouse));
    }
    [Fact]
    public void ASecondKeyboard_BecomesAPendingPlayer_AndAssignDeviceCanStillMoveItOntoPlayer1() {
        using var fixture = Fixtures.FreshServer(definition: SingleActiveSeatDocument());
        var roster = BuildRoster(fixture: fixture);
        var keyboard1 = InputDeviceId.FromKey(key: "keyboard:1");
        var keyboard2 = InputDeviceId.FromKey(key: "keyboard:2");

        roster.ObserveDeviceKind(device: keyboard1, kind: InputDeviceKind.Keyboard);
        _ = roster.Confirm(device: keyboard1, actingPrincipal: WorldPrincipal.Console);

        // A second keyboard finds slot 0 already carrying a keyboard, so it takes the next free slot as a pending
        // player — exactly the "later gamepad" rule, generalized to every kind.
        roster.ObserveDeviceKind(device: keyboard2, kind: InputDeviceKind.Keyboard);

        var (outcome, slot) = roster.Confirm(device: keyboard2, actingPrincipal: WorldPrincipal.Console);

        Assert.Equal(expected: ConfirmOutcome.Joined, actual: outcome);
        Assert.Equal(expected: 1, actual: slot);
        Assert.Equal(expected: "keyboard2", actual: roster.DeviceToken(device: keyboard2));
        Assert.True(condition: roster.TryResolveDeviceToken(token: "keyboard2", device: out var resolved));
        Assert.Equal(expected: keyboard2, actual: resolved);

        // player.assign keyboard2 1 (display "1" = slot 0) still moves it onto player 1's team like any device.
        Assert.Equal(expected: AssignOutcome.JoinedTeam, actual: roster.AssignDevice(
            device: keyboard2,
            targetSlot: 0,
            actingPrincipal: WorldPrincipal.Console
        ));
        Assert.Equal(expected: 0, actual: roster.DeviceSlot(device: keyboard2));
    }
}
