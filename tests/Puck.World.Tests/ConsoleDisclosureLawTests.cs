using System.Numerics;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the console's disclosure chokepoint: every read-back of state values a non-operator principal
/// reaches answers through <see cref="WorldStateReadView"/>, so the seat a row's visibility admits reads it, another
/// seat is refused or shown only what its disclosure carries, and the operator reads the live document whole. The
/// evaluation diagnostics are operator verbs, refused for every seat.</summary>
public sealed class ConsoleDisclosureLawTests {
    // Distinct values no fixture row or echo carries by accident, so their absence from an output is a real absence.
    private const long Vault = 918273645L;
    private const long Occupied = 736451829L;
    private const long Sealed = 527364819L;

    private static StateCell Cell(string key, long value, StateVisibility? visibility = null) => new(
        Key: CellName.Parse(candidate: key),
        Value: CellValue.Int(value: value),
        Visibility: visibility
    );
    private static WorldDefinition Document() {
        var creation = CreationFixtures.Sphere(id: "piece", scale: 0.2f);
        var document = Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementsRaw = new WorldPlacementsSection(
                Policy: Fixtures.StandardAuthoring,
                Rows: [
                    new WorldPlacement(
                        Board: new WorldPlacementBoard(
                            Occupancy: "cells",
                            Topology: "strip"
                        ),
                        Id: "table",
                        Position: Vector3.Zero,
                        PrototypeId: creation.Id,
                        Scale: 1f,
                        YawDegrees: 0f
                    ),
                ]
            ),
            StateRaw = new WorldStateSection(
                Lattices: [new LatticeTopology.Grid(
                    CellSize: 2f,
                    Depth: 1,
                    Name: "strip",
                    Origin: Vector3.Zero,
                    Width: 2
                )],
                World: [
                    // A slot only seat 1 reads.
                    new WorldStateRow(
                        Cells: [Cell(key: StateRow.SlotKey.Value, value: Vault)],
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "vault"),
                        Visibility: new StateVisibility(Readers: ["seat1"])
                    ),
                    // A row both seats read, with one cell only seat 1 reads; the others see a placeholder for it.
                    new WorldStateRow(
                        Cells: [
                            Cell(key: "open", value: 7L),
                            Cell(key: "sealed", value: Sealed, visibility: new StateVisibility(Readers: ["seat1"])),
                        ],
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "ledger"),
                        Visibility: new StateVisibility(Hidden: HiddenCells.Placeholder, Readers: ["seat1", "seat2"])
                    ),
                    // A board's occupancy only seat 1 reads.
                    new WorldStateRow(
                        Cells: [Cell(key: "0", value: Occupied)],
                        Domain: new StateDomain.CellsOf(Topology: "strip"),
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "cells"),
                        Visibility: new StateVisibility(Readers: ["seat1"])
                    ),
                    new WorldStateRow(
                        Cells: [Cell(key: StateRow.SlotKey.Value, value: 3L)],
                        Kind: CellKind.Int,
                        Name: CellName.Parse(candidate: "score")
                    ),
                ]
            ),
        };

        return document;
    }

    // Every read verb and argument shape the sweep offers each module's verbs: enough to name every fixture row and
    // cell in each grain a read-back accepts.
    private static readonly string[] SweepArguments = [
        "",
        " vault",
        " vault $value",
        " ledger",
        " ledger sealed",
        " ledger open",
        " cells",
        " cells 0",
        " state vault",
        " state ledger",
        " state cells",
        " table",
        " fullRow vault",
        " seat1",
        " {state.vault}",
        " {state.ledger.sealed}",
    ];

    private static bool Leaks(string output) => (
        output.Contains(comparisonType: StringComparison.Ordinal, value: Vault.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)) ||
        output.Contains(comparisonType: StringComparison.Ordinal, value: Sealed.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)) ||
        output.Contains(comparisonType: StringComparison.Ordinal, value: Occupied.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))
    );

    [Fact]
    public void TheOwnerReadsAHiddenSlotAnotherSeatIsRefusedAndTheOperatorReadsIt() {
        using var host = new DisclosureHost(definition: Document());

        Assert.Contains(actualString: host.Console(line: "world.state vault $value").Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "value=918273645");
        Assert.Contains(actualString: host.Seat(line: "world.state vault $value", slot: 0).Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "value=918273645");

        var refused = host.Seat(line: "world.state vault $value", slot: 1);

        Assert.True(condition: refused.IsError);
        Assert.Equal(
            actual: refused.Output,
            expected: "[world.state: 'vault'.'$value' is not disclosed to seat2 — its visibility withholds it]"
        );

        var row = host.Seat(line: "world.state vault", slot: 1).Output;

        Assert.Contains(actualString: row, comparisonType: StringComparison.Ordinal, expectedSubstring: "value=withheld withheld=row");
        Assert.False(condition: Leaks(output: row));
        Assert.False(condition: Leaks(output: host.Seat(line: "world.state", slot: 1).Output));
        Assert.True(condition: Leaks(output: host.Console(line: "world.state").Output));
    }
    [Fact]
    public void AWithheldCellShowsAsItsRowsPlaceholderAndAnAbsentKeyIsRefusedAlike() {
        using var host = new DisclosureHost(definition: Document());
        var row = host.Seat(line: "world.state ledger", slot: 1).Output;

        Assert.Contains(actualString: row, comparisonType: StringComparison.Ordinal, expectedSubstring: "[world.state.cell 'ledger'.'open' value=7]");
        Assert.Contains(actualString: row, comparisonType: StringComparison.Ordinal, expectedSubstring: "[world.state.cell 'ledger' hidden]");
        Assert.False(condition: Leaks(output: row));

        // A withheld key and an absent one answer the same refusal, so a refusal never says which keys exist.
        Assert.Equal(
            actual: host.Seat(line: "world.state ledger sealed", slot: 1).Output,
            expected: "[world.state: 'ledger'.'sealed' is not disclosed to seat2 — its visibility withholds it]"
        );
        Assert.Equal(
            actual: host.Seat(line: "world.state ledger nothing", slot: 1).Output,
            expected: "[world.state: 'ledger'.'nothing' is not disclosed to seat2 — its visibility withholds it]"
        );
        Assert.Contains(actualString: host.Seat(line: "world.state ledger sealed", slot: 0).Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "value=527364819");
        // A row nothing withholds from anyone keeps its ordinary absent-key answer.
        Assert.Equal(
            actual: host.Seat(line: "world.state score nothing", slot: 1).Output,
            expected: "[world.state score nothing: no such cell]"
        );
    }
    [Fact]
    public void TheRowReadBackShowsTheDisclosedRow() {
        using var host = new DisclosureHost(definition: Document());

        Assert.True(condition: Leaks(output: host.Console(line: "world.row state vault").Output));
        Assert.True(condition: Leaks(output: host.Seat(line: "world.row state vault", slot: 0).Output));
        Assert.False(condition: Leaks(output: host.Seat(line: "world.row state vault", slot: 1).Output));
        Assert.False(condition: Leaks(output: host.Seat(line: "world.row state ledger", slot: 1).Output));
        Assert.Contains(actualString: host.Seat(line: "world.row state ledger", slot: 1).Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"open\"");
    }
    [Fact]
    public void TheTabletopEchoesOnlyTheOccupancyTheCallerReads() {
        using var host = new DisclosureHost(definition: Document());

        Assert.True(condition: Leaks(output: host.Console(line: "world.tabletop").Output));
        Assert.True(condition: Leaks(output: host.Seat(line: "world.tabletop", slot: 0).Output));
        Assert.Contains(actualString: host.Seat(line: "world.tabletop", slot: 1).Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "occupancy=(empty)");
    }
    [Fact]
    public void APatternWalkOverAWithheldRowIsRefused() {
        using var host = new DisclosureHost(definition: Document());

        Assert.Equal(
            actual: host.Seat(line: "world.match fullRow vault", slot: 1).Output,
            expected: "[world.match: 'vault' is not disclosed to seat2 — its visibility withholds it]"
        );
        Assert.DoesNotContain(actualString: host.Seat(line: "world.match fullRow vault", slot: 0).Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "not disclosed");
        Assert.DoesNotContain(actualString: host.Console(line: "world.match fullRow vault").Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "not disclosed");
    }
    [Fact]
    public void ASeatObservesOnlyItselfAndTheOperatorObservesAnyone() {
        using var host = new DisclosureHost(definition: Document());

        Assert.Equal(
            actual: host.Seat(line: "world.observe seat1", slot: 1).Output,
            expected: "[world.observe: refused — seat2 may observe only itself, not seat1]"
        );
        Assert.False(condition: host.Seat(line: "world.observe seat2", slot: 1).IsError);
        Assert.False(condition: Leaks(output: host.Seat(line: "world.observe seat2", slot: 1).Output));
        Assert.True(condition: Leaks(output: host.Seat(line: "world.observe seat1", slot: 0).Output));
        Assert.True(condition: Leaks(output: host.Console(line: "world.observe seat1").Output));
    }
    [InlineData("world.rule.trace")]
    [InlineData("world.rule.failures")]
    [InlineData("world.search")]
    [Theory]
    public void AnEvaluationDiagnosticIsAnOperatorVerb(string verb) {
        using var host = new DisclosureHost(definition: Document());
        var refused = host.Seat(line: verb, slot: 0);

        Assert.True(condition: refused.IsError);
        Assert.Equal(
            actual: refused.Output,
            expected: $"[{verb}: refused — an operator verb; seat1 is not the operator]"
        );
        Assert.DoesNotContain(actualString: host.Console(line: verb).Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "an operator verb");
    }
    // The mechanical guard behind the chokepoint: every verb every console module registers, offered every argument
    // shape above, as the seat the fixture's restricted rows exclude. None may print a withheld value.
    [Fact]
    public void NoConsoleVerbDisclosesAWithheldValueToAnotherSeat() {
        using var host = new DisclosureHost(definition: Document());
        var leaking = host.Sweep(leaks: Leaks, slot: 1);

        Assert.Empty(collection: leaking);
    }
    // The mutation proof: an authority that mints every view as the operator's removes the chokepoint, and the same
    // sweep then finds the withheld values in several verbs at once — so the sweep above is a check that can fail.
    [Fact]
    public void WithoutTheChokepointSeveralVerbsLeakAtOnce() {
        using var host = new DisclosureHost(definition: Document(), leaky: true);
        var leaking = host.Sweep(leaks: Leaks, slot: 1);

        Assert.Contains(collection: leaking, expected: "world.state");
        Assert.Contains(collection: leaking, expected: "world.row");
        Assert.Contains(collection: leaking, expected: "world.tabletop");
        Assert.True(condition: (leaking.Count >= 3), userMessage: string.Join(separator: ", ", values: leaking));
    }

    // One host row with every constructible console module over it, a console door, and a seat session per local
    // seat through the same text source a host's seat consoles use.
    private sealed class DisclosureHost : IDisposable {
        private readonly HostRow m_row;
        private readonly CommandRegistry m_registry;
        private readonly TextCommandSource m_source;
        private readonly TextCommandSession[] m_seats;

        private CommandResult m_last;

        public DisclosureHost(WorldDefinition definition, bool leaky = false) {
            m_row = HostRow.Build(
                definition: definition,
                name: "boot"
            );

            IWorldConsoleAuthority authority = (leaky
                ? new LeakyAuthority(instance: m_row.Instance)
                : new FixedAuthority(instance: m_row.Instance));
            var link = m_row.Instance.Link;
            var echoes = new WorldDeferredVerbEchoes();
            var guard = new WorldRowStepWindowGuard();

            m_registry = new CommandRegistry(modules: [
                new WorldContributionCommandModule(authority: authority),
                new WorldCurveCommandModule(authority: authority),
                new WorldDynamicsCommandModule(authority: authority),
                new WorldExtensionsCommandModule(authority: authority),
                new WorldGrantCommandModule(authority: authority, link: link),
                new WorldGroupCommandModule(authority: authority, link: link),
                new WorldLightingCommandModule(authority: authority),
                new WorldLookCommandModule(authority: authority, link: link),
                new WorldMachineCommandModule(authority: authority, link: link),
                new WorldNetworkCommandModule(authority: authority),
                new WorldRowCommandModule(authority: authority, echoes: echoes, link: link, stepGuard: guard),
                new WorldSculptCommandModule(authority: authority, echoes: echoes, link: link, stepGuard: guard),
                new WorldStateCommandModule(authority: authority, echoes: echoes, link: link),
                new WorldTabletopCommandModule(authority: authority),
                new WorldUpdateCommandModule(authority: authority),
            ]);

            var router = new InputRouter(
                bindings: new NoBindings(),
                principalResolver: new SeatPrincipals(),
                registry: m_registry
            );

            m_source = new TextCommandSource(registry: m_registry);
            m_seats = [.. Enumerable.Range(count: 2, start: 0).Select(selector: slot => m_source.CreateSeatSession(
                onResult: (_, result) => m_last = result,
                router: router,
                slot: slot
            ))];
        }

        public CommandResult Console(string line) => m_registry.Submit(line: line);
        public void Dispose() => m_row.Dispose();
        public CommandResult Seat(int slot, string line) {
            m_last = CommandResult.None;
            m_seats[slot].Enqueue(line: line);
            m_source.Collect();

            return m_last;
        }
        public IReadOnlyList<string> Sweep(int slot, Func<string, bool> leaks) {
            var leaking = new SortedSet<string>(comparer: StringComparer.Ordinal);

            foreach (var command in m_registry.Definitions) {
                // A simulation-routed verb answers at the tick it applies, and none is applied here; only the reads
                // that answer inline are swept.
                if (command.Routing != CommandRouting.Immediate) {
                    continue;
                }

                foreach (var arguments in SweepArguments) {
                    if (leaks(Seat(line: (command.Name + arguments), slot: slot).Output)) {
                        _ = leaking.Add(item: command.Name);
                    }
                }
            }

            return [.. leaking];
        }
    }
    private class FixedAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }
    private sealed class LeakyAuthority(WorldInstance instance) : FixedAuthority(instance: instance), IWorldConsoleAuthority {
        public WorldStateReadView ReadView(CommandContext context, WorldInstance instance) => WorldStateReadView.Of(
            reader: Principal.Console,
            server: instance.Server
        );
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class SeatPrincipals : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Seat(slot: slot);
    }
}
