using System.Numerics;
using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Output;

namespace Puck.Input.Tests;

public sealed class ArbiterTests {
    [Fact]
    public void Drain_copies_every_device_in_player_order_into_caller_storage() {
        var source = new TestAcquisitionSource();
        using var manager = new GamepadManager(
            acquisitionSource: source,
            clock: new ManualInputClock(),
            hidSource: new EmptyHidDeviceSource()
        );

        manager.Start();

        source.PlayerOne!.Publish(state: GamepadState.Neutral with {
            LeftStick = new Vector2(
            x: 0.75f,
            y: 0f
        ),
        });
        source.PlayerZero!.Publish(state: GamepadState.Neutral with {
            LeftStick = new Vector2(
            x: 0.25f,
            y: 0f
        ),
        });

        var arbiter = new InputArbiter(manager: manager);

        arbiter.DrainFrame(frameKey: 1UL);

        var destination = new List<GamepadDrain> { default, default, default, };

        arbiter.CopyDrainedDevices(destination: destination);

        Assert.Equal(
            expected: 2,
            actual: destination.Count
        );
        Assert.Equal(
            expected: source.PlayerZero.DeviceId,
            actual: destination[0].DeviceId
        );
        Assert.Equal(
            expected: source.PlayerOne.DeviceId,
            actual: destination[1].DeviceId
        );
    }
    private sealed class TestAcquisitionSource : IGamepadAcquisitionSource {
        public TestConnection? PlayerOne { get; private set; }
        public TestConnection? PlayerZero { get; private set; }

        public void Dispose() { }
        public void Start(IGamepadConnectionRegistry registry) {
            PlayerOne = ((TestConnection)registry.Register(connectionFactory: _ => new TestConnection(
                key: "player-one",
                playerIndex: 1
            )));
            PlayerZero = ((TestConnection)registry.Register(connectionFactory: _ => new TestConnection(
                key: "player-zero",
                playerIndex: 0
            )));
        }
    }
    private sealed class TestConnection(int playerIndex, string key) : IGamepadConnection {
        private readonly GamepadOutput m_output = new(
            capabilities: GamepadOutputCapabilities.None,
            deviceId: InputDeviceId.FromConnectionKey(key: key),
            queue: new GamepadOutputQueue()
        );

        public int PlayerIndex { get; } = playerIndex;
        public GamepadCoalescer Coalescer { get; } = new();
        public string Key { get; } = key;

        public InputDeviceId DeviceId => m_output.DeviceId;
        public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.None;
        public bool IsFaulted => false;
        public IGamepadOutput Output => m_output;
        public GamepadType Type => GamepadType.Unknown;

        public void Dispose() => m_output.Kill();
        public void Publish(GamepadState state) => Coalescer.Update(state: in state);
        public void Start() { }
    }
}
