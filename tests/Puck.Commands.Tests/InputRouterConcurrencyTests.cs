using System.Globalization;

using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Drives the headline thread-safety claim: backends capture from their own device I/O threads while the
/// fixed-step thread builds snapshots. The outcome is deterministic even though the interleaving is not — every
/// captured signal appears exactly once, and one producer's signals keep the order that producer captured them in.
/// </summary>
public sealed class InputRouterConcurrencyTests {
    private const string ProbeCommand = "test.probe";

    [Fact]
    public void EveryConcurrentlyCapturedSignalIsSnapshotOnceInItsProducersOrder() {
        const int perProducer = 250;
        const int producerCount = 4;
        const ulong tickCeiling = 1_000_000UL;

        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new ProbeModule()]),
            bindings: new AnySourceBindings(),
            principalResolver: new ConsolePrincipal()
        );
        var start = new Barrier(participantCount: (producerCount + 1));
        var producers = new Thread[producerCount];

        for (var producerIndex = 0; (producerIndex < producerCount); producerIndex++) {
            var producer = producerIndex;
            var thread = new Thread(start: () => {
                start.SignalAndWait();

                for (var index = 0; (index < perProducer); index++) {
                    // A TEXT signal folds into exactly one entry and leaves no held state behind, so the lane carries
                    // the capture stream itself rather than a re-assertion of it.
                    router.Capture(signal: InputSignal.Typed(
                        source: $"p{producer}.{index}",
                        text: "x"
                    ));
                }
            }) {
                IsBackground = true,
            };

            producers[producer] = thread;
            thread.Start();
        }

        var expected = (producerCount * perProducer);
        var observed = new List<string>(capacity: expected);
        var tick = 0UL;

        start.SignalAndWait(cancellationToken: TestContext.Current.CancellationToken);

        while (observed.Count < expected) {
            tick++;

            Assert.True(
                condition: (tick < tickCeiling),
                userMessage: $"The pump produced {tickCeiling} snapshots and saw only {observed.Count} of {expected} captured signals."
            );

            foreach (var lane in router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue).Lanes) {
                foreach (var entry in lane.Entries) {
                    observed.Add(item: entry.Source!);
                }
            }
        }

        foreach (var thread in producers) {
            thread.Join();
        }

        Assert.Equal(actual: observed.Count, expected: expected);
        Assert.Equal(actual: observed.Distinct(comparer: StringComparer.Ordinal).Count(), expected: expected);

        // Per producer, the snapshots replay exactly the order that producer captured in — the interleaving between
        // producers is whatever the threads did, but no producer's own stream may be reordered or torn.
        var nextByProducer = new int[producerCount];

        foreach (var source in observed) {
            var separator = source.IndexOf(value: '.', comparisonType: StringComparison.Ordinal);
            var producer = int.Parse(
                provider: CultureInfo.InvariantCulture,
                s: source.AsSpan(start: 1, length: (separator - 1))
            );
            var index = int.Parse(
                provider: CultureInfo.InvariantCulture,
                s: source.AsSpan(start: (separator + 1))
            );

            Assert.Equal(actual: index, expected: nextByProducer[producer]);

            nextByProducer[producer]++;
        }
    }

    private sealed class AnySourceBindings : IInputBindings {
        private readonly CommandBinding[] m_bindings = [new CommandBinding(Command: ProbeCommand)];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }
    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: ProbeCommand,
                description: "The one destination every concurrently captured source resolves to.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
}
