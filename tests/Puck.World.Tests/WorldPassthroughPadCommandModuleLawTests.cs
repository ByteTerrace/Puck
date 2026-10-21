using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the pad's desktop verbs act only for a local pad's own bound control. A typed line and a signal that names
/// no device (what <c>player.signal</c> synthesizes) are refused by name, a boot with no window refuses the local pad
/// by name, and a windowed boot hands every verb's press and release to the passthrough door.
/// </summary>
public sealed class WorldPassthroughPadCommandModuleLawTests {
    private static readonly string[] Verbs = [
        WorldPassthroughPadCommandModule.PointerCommand,
        WorldPassthroughPadCommandModule.ScrollCommand,
        "source.passthrough.button.left",
        "source.passthrough.button.right",
        "source.passthrough.button.middle",
        "source.passthrough.button.back",
        "source.passthrough.button.forward",
        "source.passthrough.arrow.up",
        "source.passthrough.arrow.down",
        "source.passthrough.arrow.left",
        "source.passthrough.arrow.right",
    ];

    // Presses and releases one bound gamepad control on `verb` for `device`, and returns each dispatch's result.
    private static List<CommandResult> Bound(WorldPassthroughPadCommandModule module, string verb, InputDeviceId device) {
        var observer = new Results();
        var registry = new CommandRegistry(
            modules: [module],
            observers: [observer]
        );
        var router = new InputRouter(
            bindings: new FixedBindings(binding: new CommandBinding(Command: verb)),
            principalResolver: new SeatPrincipal(),
            registry: registry
        );
        var stick = verb is WorldPassthroughPadCommandModule.PointerCommand or WorldPassthroughPadCommandModule.ScrollCommand;
        var source = (stick ? "gamepad.leftStick" : "gamepad.buttonSouth");

        router.Capture(signal: new InputSignal(
            DeviceId: device,
            Phase: (stick ? CommandPhase.Active : CommandPhase.Started),
            Source: source,
            Value: (stick ? CommandValue.Axis(value: Vector2.UnitX) : CommandValue.Digital(active: true))
        ));
        var pressed = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in pressed);
        router.Capture(signal: new InputSignal(
            DeviceId: device,
            Phase: CommandPhase.Completed,
            Source: source,
            Value: (stick ? CommandValue.Axis(value: Vector2.Zero) : CommandValue.Digital(active: false))
        ));
        var released = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in released);

        return observer.Seen;
    }

    [Fact]
    public void OnlyALocalPadsBoundControlReachesThePassthroughDoor() {
        var pad = InputDeviceId.FromConnectionKey(key: "xinput-0");
        var headless = new WorldPassthroughPadCommandModule();
        var typed = new CommandRegistry(modules: [headless]).Submit(line: "source.passthrough.button.left");

        Assert.True(condition: typed.IsError);
        Assert.Contains(expectedSubstring: "only a local pad's own control", actualString: typed.Output);

        foreach (var verb in Verbs) {
            var scripted = Bound(device: default, module: headless, verb: verb);
            var local = Bound(device: pad, module: headless, verb: verb);

            Assert.NotEmpty(collection: scripted);
            Assert.NotEmpty(collection: local);
            Assert.All(
                collection: scripted,
                action: result => Assert.Contains(expectedSubstring: "only a local pad's own control", actualString: result.Output)
            );
            Assert.All(
                collection: local,
                action: result => Assert.Contains(expectedSubstring: "requires a windowed boot", actualString: result.Output)
            );
        }

        using var files = new TemporaryDirectory();
        using var graphs = new WorldViewGraphHost(
            documentDirectory: files.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(cacheDirectory: files.PathOf(name: "cache")))
        );
        var windowed = new WorldPassthroughPadCommandModule(passthrough: new WorldSourcePassthrough(
            anchor: new WorldPerceptionAnchor(),
            graphs: graphs,
            seats: new NoSeats(),
            time: TimeProvider.System,
            viewports: new WorldSeatViewports(),
            windows: new NoWindows()
        ));

        foreach (var verb in Verbs) {
            var results = Bound(device: pad, module: windowed, verb: verb);

            Assert.NotEmpty(collection: results);
            Assert.All(collection: results, action: result => Assert.False(condition: result.IsError, userMessage: result.Output));
        }
    }

    private sealed class Results : ICommandObserver {
        public List<CommandResult> Seen { get; } = [];

        public void OnCommand(in CommandActivation activation) => Seen.Add(item: activation.Result);
    }
    private sealed class FixedBindings(CommandBinding binding) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [binding];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }
    private sealed class SeatPrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Seat(slot: slot);
    }
    private sealed class NoSeats : IWorldPassthroughSeats {
        public WorldScreenPadPointer? PadPointerOf(int screen) => null;
        public void Leave(int slot) {
        }
    }
    private sealed class NoWindows : IWorldPassthroughWindows {
        public ISourcePassthroughWindow? PassthroughWindowOf(string instance, out string? fault) {
            fault = "none";

            return null;
        }
    }
}
