using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Proves a HELD verb (<see cref="CommandDefinition.Held"/>) bound plainly — no <c>activateOn</c> — has its
/// release edge dispatched by the router, exactly as a channel destination does, while an ordinary verb bound the same
/// way has only its press dispatched (the release is recorded, never dispatched); an explicit <c>activateOn</c> stays
/// edge-selective either way.</summary>
public sealed class HeldCommandReleaseLawTests {
    private const string HeldVerb = "test.hold";
    private const string PlainVerb = "test.plain";

    [Fact]
    public void APlainlyBoundHeldVerbHearsItsRelease_APlainlyBoundOrdinaryVerbDoesNot() {
        foreach (var (command, expectsRelease) in new[] { (HeldVerb, true), (PlainVerb, false) }) {
            var registry = new CommandRegistry(modules: [new Module()]);
            var router = new InputRouter(
                registry: registry,
                bindings: new FixedBindings(binding: new CommandBinding(Command: command)),
                principalResolver: new ConsolePrincipal()
            );
            var device = InputDeviceId.FromConnectionKey(key: "keyboard-1");

            router.Capture(signal: InputSignal.Press(source: "keyboard.q", deviceId: device));
            var pressed = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

            Assert.Equal(expected: CommandPhase.Started, actual: Assert.Single(collection: Assert.Single(collection: pressed.Lanes).Entries).Phase);

            router.Capture(signal: InputSignal.Release(source: "keyboard.q", deviceId: device));
            var released = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);
            var releaseEntries = ((released.Lanes.Length == 0)
                ? []
                : released.Lanes[0].Entries.ToArray());

            if (expectsRelease) {
                Assert.Contains(collection: releaseEntries, filter: static entry => ((entry.Phase == CommandPhase.Completed) && entry.Dispatch));
            } else {
                Assert.DoesNotContain(collection: releaseEntries, filter: static entry => ((entry.Phase == CommandPhase.Completed) && entry.Dispatch));
            }
        }
    }
    [Fact]
    public void AnExplicitActivateOnStaysEdgeSelectiveOnAHeldVerb() {
        var registry = new CommandRegistry(modules: [new Module()]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(binding: new CommandBinding(Command: HeldVerb, ActivateOn: CommandPhase.Started)),
            principalResolver: new ConsolePrincipal()
        );
        var device = InputDeviceId.FromConnectionKey(key: "keyboard-1");

        router.Capture(signal: InputSignal.Press(source: "keyboard.q", deviceId: device));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Release(source: "keyboard.q", deviceId: device));
        var released = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        Assert.DoesNotContain(collection: released.Lanes.SelectMany(selector: static lane => lane.Entries.ToArray()), filter: static entry => ((entry.Phase == CommandPhase.Completed) && entry.Dispatch));
    }

    private sealed class FixedBindings(CommandBinding binding) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [binding];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }
    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class Module : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: HeldVerb,
                description: "Held probe.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable,
                held: true
            );
            yield return CommandDefinition.Verb(
                name: PlainVerb,
                description: "Plain probe.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
}
