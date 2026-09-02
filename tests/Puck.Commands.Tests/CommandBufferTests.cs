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
    [Fact]
    public void AViewRetainedAcrossTheNextSnapshotRefusesToBeRead() {
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new SingleCommandModule()]),
            bindings: new FixedBindings(),
            principalResolver: new ConsolePrincipal()
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));

        var first = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        var retainedLane = first.Lanes[0];

        router.Capture(signal: InputSignal.Press(source: "key.b"));

        var second = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        // The router has rewritten the storage `first` points at. Reading it would answer with tick 2's contents
        // under tick 1's number — the one failure a borrowed buffer must never produce silently.
        Assert.Equal(actual: first.Tick, expected: 1UL);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => first.Lanes[0]);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => first.Lanes.Span.Length);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => first.TryGetLane(lane: out _, slot: 0));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => {
            foreach (var lane in first.Lanes) {
                _ = lane;
            }
        });
        _ = Assert.Throws<InvalidOperationException>(testCode: () => retainedLane.Entries[0]);

        // The snapshot the router actually produced this tick reads normally.
        Assert.NotEmpty(collection: second.Lanes[0].Entries.ToArray());
    }
    [Fact]
    public void TheEmptySnapshotsLanesStayReadableForever() {
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new SingleCommandModule()]),
            bindings: new FixedBindings(),
            principalResolver: new ConsolePrincipal()
        );
        var empty = CommandSnapshot.Empty(tick: 1UL);

        router.Capture(signal: InputSignal.Press(source: "key.a"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        // Empty borrows no router storage at all, so no generation can retire it.
        Assert.True(condition: empty.Lanes.IsEmpty);
        Assert.True(condition: empty.Lanes.Span.IsEmpty);
        Assert.Empty(collection: empty.Lanes);
        Assert.False(condition: empty.TryGetLane(lane: out _, slot: 0));
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
