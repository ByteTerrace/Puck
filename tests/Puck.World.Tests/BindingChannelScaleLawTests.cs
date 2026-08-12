using Xunit;

using Puck.Commands;

namespace Puck.World.Tests;

/// <summary>Proves a channel scale is always a finite member of its authored [-1, 1] domain.</summary>
public sealed class BindingChannelScaleLawTests {
    [Fact]
    public void NonFiniteChannelScalesAreStructurallyRefusedOnPagesAndChords() {
        foreach (var scale in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity }) {
            _ = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: Document(pageScale: scale, chordScale: null)));
            _ = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: Document(pageScale: null, chordScale: scale)));
        }
    }

    [Fact]
    public void DefaultChannelBindingDispatchesItsOwnCompletedRelease() {
        const string command = "test.channel";
        var registry = new CommandRegistry(modules: [new ChannelModule(command)]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(new CommandBinding(Command: command, ChannelScale: 1f)),
            principalResolver: new ConsolePrincipal()
        );
        var device = InputDeviceId.FromConnectionKey(key: "pad-1");

        router.Capture(signal: new InputSignal(
            Source: "gamepad.rightTrigger",
            DeviceId: device,
            Value: CommandValue.Axis(value: 1f),
            Phase: CommandPhase.Active
        ));
        var held = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        Assert.Equal(expected: CommandPhase.Active, actual: Assert.Single(Assert.Single(held.Lanes).Entries).Phase);

        router.Capture(signal: new InputSignal(
            Source: "gamepad.rightTrigger",
            DeviceId: device,
            Value: CommandValue.Axis(value: 0f),
            Phase: CommandPhase.Completed
        ));
        var released = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);
        var releaseLane = Assert.Single(released.Lanes);
        Assert.Equal(expected: 2, actual: releaseLane.Entries.Length);
        var release = releaseLane.Entries[^1];

        Assert.Equal(expected: CommandPhase.Completed, actual: release.Phase);
        Assert.Equal(expected: 0f, actual: release.Value.AsAxis1D);
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    private static BindingProfileDocument Document(float? pageScale, float? chordScale) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [new BindingModifierDefinition(Id: "shift", Source: "key.shift")],
        Chords: [
            new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(
                    Id: "base",
                    Entries: [new BindingPageEntryDefinition(Source: "key.fire", Channel: new ChannelRef.Name(Value: "fire"), Scale: pageScale)]
                )
            ),
            new BindingChordDefinition(
                Group: "play",
                Chord: ["shift"],
                Command: new BindingCommandDefinition(Channel: new ChannelRef.Name(Value: "fire"), Scale: chordScale)
            ),
        ]
    );

    private sealed class FixedBindings(CommandBinding binding) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [binding];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }

    private sealed class ChannelModule(string command) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: command,
                description: "Channel release probe.",
                valueKind: CommandValueKind.Axis1D,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
}
