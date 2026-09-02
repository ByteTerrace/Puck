using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Exercises the borrowed per-tick command view: its bounds, its structural equality, and its non-allocating
/// enumerator.</summary>
public sealed class CommandBufferTests {
    [Fact]
    public void ADefaultViewIsEmptyOnEverySurface() {
        var buffer = default(CommandBuffer<CommandLane>);

        Assert.True(condition: buffer.IsEmpty);
        Assert.True(condition: (buffer.Length == 0));
        Assert.True(condition: buffer.Span.IsEmpty);
        Assert.Empty(collection: buffer);
    }
    [Fact]
    public void ANegativeIndexIsRefusedRatherThanDereferencingTheBackingArray() {
        var buffer = default(CommandBuffer<CommandLane>);

        // The upper bound alone left a negative index falling through to a null backing array — an out-of-range read
        // reported as a NullReferenceException.
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => buffer[-1]);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => buffer[0]);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => buffer[int.MinValue]);
    }
    [Fact]
    public void AnEmptyViewEqualsTheDefaultOne() {
        var empty = CommandSnapshot.Empty(tick: 1UL).Lanes;
        var fallback = default(CommandBuffer<CommandLane>);

        Assert.True(condition: (empty == fallback));
        Assert.False(condition: (empty != fallback));
        Assert.True(condition: empty.Equals(other: fallback));
        Assert.True(condition: empty.Equals(obj: fallback));
        Assert.Equal(actual: empty.GetHashCode(), expected: fallback.GetHashCode());
    }
    [Fact]
    public void APopulatedViewEnumeratesTheSameSequenceAfterAReset() {
        var registry = new CommandRegistry(modules: [new SingleCommandModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(),
            principalResolver: new ConsolePrincipal()
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));

        var entries = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes[0].Entries;

        Assert.False(condition: entries.IsEmpty);

        var enumerator = entries.GetEnumerator();
        var first = new List<ushort>();
        var second = new List<ushort>();

        while (enumerator.MoveNext()) {
            first.Add(item: enumerator.Current.CommandId);
        }

        enumerator.Reset();

        while (enumerator.MoveNext()) {
            second.Add(item: enumerator.Current.CommandId);
        }

        Assert.NotEmpty(collection: first);
        Assert.Equal(actual: second, expected: first);
        Assert.Equal(actual: entries.Span.Length, expected: first.Count);
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class FixedBindings : IInputBindings {
        private readonly CommandBinding[] m_bindings = [new CommandBinding(Command: "buffered")];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }
    private sealed class SingleCommandModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "buffered",
                description: "A bound verb whose entry populates a lane.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
}
