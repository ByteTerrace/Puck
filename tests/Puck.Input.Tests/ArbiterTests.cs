using System.Numerics;
using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Output;

namespace Puck.Input.Tests;

public sealed class ArbiterTests {
    [Fact]
    public void Multicast_uses_player_order_and_copy_reuses_caller_storage() {
        var source = new TestAcquisitionSource();
        using var manager = new GamepadManager(
            acquisitionSource: source,
            clock: new ManualInputClock(),
            hidSource: new EmptyHidDeviceSource()
        );

        manager.Start();

        source.PlayerOne!.Publish(state: GamepadState.Neutral with { LeftStick = new Vector2(x: 0.75f, y: 0f), });
        source.PlayerZero!.Publish(state: GamepadState.Neutral with { LeftStick = new Vector2(x: 0.25f, y: 0f), });

        var arbiter = new InputArbiter(manager: manager);
        var lane = arbiter.RegisterLane(policy: InputLanePolicy.Multicast);

        arbiter.DrainFrame(frameKey: 1UL);

        Assert.Equal(expected: 0.25f, actual: arbiter.Sample(laneToken: lane).LeftStick.X);

        var destination = new List<GamepadDrain> { default, default, default, };

        arbiter.CopyDrainedDevices(destination: destination);

        Assert.Equal(expected: 2, actual: destination.Count);
        Assert.Equal(expected: source.PlayerZero.DeviceId, actual: destination[0].DeviceId);
        Assert.Equal(expected: source.PlayerOne.DeviceId, actual: destination[1].DeviceId);
    }
    [Fact]
    public void Unregistered_lane_is_rejected_by_every_lane_operation() {
        using var manager = new GamepadManager(
            clock: new ManualInputClock(),
            hidSource: new EmptyHidDeviceSource()
        );
        var arbiter = new InputArbiter(manager: manager);
        var lane = arbiter.RegisterLane(policy: InputLanePolicy.Owned);

        arbiter.UnregisterLane(laneToken: lane);

        _ = Assert.Throws<ArgumentException>(testCode: () => arbiter.Sample(laneToken: lane));
        _ = Assert.Throws<ArgumentException>(testCode: () => arbiter.SuppressLane(laneToken: lane, suppressed: true));
        _ = Assert.Throws<ArgumentException>(testCode: () => arbiter.UnregisterLane(laneToken: lane));
    }

    private sealed class TestAcquisitionSource : IGamepadAcquisitionSource {
        public TestConnection? PlayerOne { get; private set; }
        public TestConnection? PlayerZero { get; private set; }

        public void Start(IGamepadConnectionRegistry registry) {
            PlayerOne = ((TestConnection)registry.Register(connectionFactory: _ => new TestConnection(key: "player-one", playerIndex: 1)));
            PlayerZero = ((TestConnection)registry.Register(connectionFactory: _ => new TestConnection(key: "player-zero", playerIndex: 0)));
        }
        public void Dispose() { }
    }
    private sealed class TestConnection(int playerIndex, string key) : IGamepadConnection {
        private readonly GamepadOutput m_output = new(
            capabilities: GamepadOutputCapabilities.None,
            deviceId: InputDeviceId.FromConnectionKey(key: key),
            queue: new GamepadOutputQueue()
        );

        public InputDeviceId DeviceId => m_output.DeviceId;

        public int PlayerIndex { get; } = playerIndex;

        public bool IsFaulted => false;

        public GamepadCoalescer Coalescer { get; } = new();

        public IGamepadOutput Output => m_output;

        public string Key { get; } = key;

        public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.None;
        public GamepadType Type => GamepadType.Unknown;

        public void Publish(GamepadState state) => Coalescer.Update(state: in state);
        public void Start() { }
        public void Dispose() => m_output.Kill();
    }
}
