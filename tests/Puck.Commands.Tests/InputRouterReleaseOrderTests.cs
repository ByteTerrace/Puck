using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the total order of every synthesized release a slot can owe. Each of these paths gathers its work by
/// walking a <see cref="Dictionary{TKey, TValue}"/>, whose enumeration order is an implementation detail of its
/// insertion and removal history, so each sorts on (command id, source) before emitting — otherwise a snapshot's
/// entry order would be a property of how the table happened to be built rather than of the input.</summary>
public sealed class InputRouterReleaseOrderTests {
    private const string AlphaChannelCommand = "test.channel.alpha";
    private const string AlphaCommand = "test.alpha";
    private const string BetaChannelCommand = "test.channel.beta";
    private const string BetaCommand = "test.beta";

    [Fact]
    public void AClosingMapCancelsItsHoldsInCommandIdOrder() {
        var bindings = DescendingBindings(channel: false);
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: bindings,
            principalResolver: new ConsolePrincipal()
        );

        Assert.True(condition: registry.TryGetId(id: out var alphaId, name: AlphaCommand));
        Assert.True(condition: registry.TryGetId(id: out var betaId, name: BetaCommand));
        Assert.True(condition: (alphaId < betaId));

        router.SetActiveMaps(maps: ["play"], slot: 0);
        router.Capture(signal: InputSignal.Press(source: "key.x"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        // Both commands live in the map that closes, so both are cancelled in one transition.
        router.SetActiveMaps(maps: [], slot: 0);

        var canceled = CanceledOf(
            phase: CommandPhase.Canceled,
            router: router,
            tick: 2UL
        );

        Assert.Equal(actual: canceled.Length, expected: 2);
        Assert.Equal(actual: canceled[0].CommandId, expected: alphaId);
        Assert.Equal(actual: canceled[1].CommandId, expected: betaId);
    }
    [Fact]
    public void ADisconnectCancelsItsDevicesHoldsInCommandIdOrder() {
        var bindings = DescendingBindings(channel: false);
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: bindings,
            principalResolver: new ConsolePrincipal()
        );
        var device = InputDeviceId.FromConnectionKey(key: "pad-1");

        Assert.True(condition: registry.TryGetId(id: out var alphaId, name: AlphaCommand));
        Assert.True(condition: registry.TryGetId(id: out var betaId, name: BetaCommand));

        router.SetActiveMaps(maps: ["play"], slot: 0);
        router.Capture(signal: InputSignal.Press(
            deviceId: device,
            source: "key.x"
        ));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.ReleaseHeld(device: device);

        var canceled = CanceledOf(
            phase: CommandPhase.Canceled,
            router: router,
            tick: 2UL
        );

        Assert.Equal(actual: canceled.Length, expected: 2);
        Assert.Equal(actual: canceled[0].CommandId, expected: alphaId);
        Assert.Equal(actual: canceled[1].CommandId, expected: betaId);
    }
    [Fact]
    public void StrandedChannelContributionsEmitInCommandIdOrder() {
        var bindings = DescendingBindings(channel: true);
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: bindings,
            principalResolver: new ConsolePrincipal()
        );

        Assert.True(condition: registry.TryGetId(id: out var alphaId, name: AlphaChannelCommand));
        Assert.True(condition: registry.TryGetId(id: out var betaId, name: BetaChannelCommand));
        Assert.True(condition: (alphaId < betaId));

        router.Capture(signal: InputSignal.Press(source: "key.x"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        // One control feeding TWO channel contributions: the page stops binding it while it is down, so both
        // contributions are stranded and both must run their release.
        bindings.Current = null;
        router.Capture(signal: InputSignal.Release(source: "key.x"));

        var stranded = CanceledOf(
            phase: CommandPhase.Completed,
            router: router,
            tick: 2UL
        );

        Assert.Equal(actual: stranded.Length, expected: 2);
        Assert.Equal(actual: stranded[0].CommandId, expected: alphaId);
        Assert.Equal(actual: stranded[1].CommandId, expected: betaId);
    }

    private static CommandEntry[] CanceledOf(InputRouter router, ulong tick, CommandPhase phase) {
        return Assert.Single(collection: router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue).Lanes)
            .Entries
            .ToArray()
            .Where(predicate: entry => (entry.Phase == phase))
            .ToArray();
    }
    // Deliberately reversed: the two holds enter the slot's held table in DESCENDING command-id order, so an
    // emission that simply walked that table would answer descending too.
    private static SwitchableBindings DescendingBindings(bool channel) {
        return new SwitchableBindings {
            Current = (channel
                ? [
                    new CommandBinding(Command: BetaChannelCommand, ChannelScale: 1f),
                    new CommandBinding(Command: AlphaChannelCommand, ChannelScale: 1f),
                ]
                : [
                    new CommandBinding(Command: BetaCommand),
                    new CommandBinding(Command: AlphaCommand),
                ]),
        };
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class SwitchableBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Current { get; set; }

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => Current;
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: AlphaCommand,
                description: "Ordering probe (lower id).",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable,
                map: "play"
            );
            yield return CommandDefinition.Verb(
                name: BetaCommand,
                description: "Ordering probe (higher id).",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable,
                map: "play"
            );
            yield return CommandDefinition.Verb(
                name: AlphaChannelCommand,
                description: "Channel ordering probe (lower id).",
                valueKind: CommandValueKind.Axis1D,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: BetaChannelCommand,
                description: "Channel ordering probe (higher id).",
                valueKind: CommandValueKind.Axis1D,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
}
