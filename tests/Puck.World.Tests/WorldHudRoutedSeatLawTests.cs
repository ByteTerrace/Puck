using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a seat's player-scope HUD panel reads the world its seat is routed to, the same rule as its bar,
/// pages, wheels and contexts. The seat registers its panel's bindings on the mirror its route reads through
/// (<see cref="WorldPresentationManifest.SeatBindings"/>), and <see cref="WorldHudBindingResolver"/> resolves a seat's
/// <c>state.*</c> token through that mirror (<see cref="WorldSeatBindings.GetRoutedState"/>). Neither world's own
/// document binds the row, so a value on the panel can only come from the seat's registration on its routed mirror.
/// </summary>
public sealed class WorldHudRoutedSeatLawTests {
    private const string Binding = "state.score";

    private static WorldStateRow Score(long value) => new(
        Name: CellName.Parse(candidate: "score"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 100,
        Cells: [new StateCell(
            Key: WorldStateRow.SlotKey,
            Value: CellValue.Int(value: value)
        )]
    );
    // An owned-world identity whose private HUD panel shows the score row as text.
    private static WorldIdentity ScoreWatcher() {
        var basis = Fixtures.BuildDocument();
        var identity = new WorldIdentity(
            defaults: basis.PlayerDefaults,
            document: (basis with {
                Identity = new WorldIdentityDefinition(
                    Color: "#ffffff",
                    Id: SafeName.Parse(candidate: "score-watcher"),
                    MoveSpeedState: CellName.Parse(candidate: "move"),
                    Name: "Watcher",
                    TurnSpeedState: CellName.Parse(candidate: "turn")
                ),
            })
        ) {
            Hud = new WorldHudPanel(
                Elements: [new WorldHudElement(
                    Binding: Binding,
                    Id: "score",
                    Kind: WorldHudElementKind.Text,
                    Rect: new WorldHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f),
                    Style: WorldHudStyleToken.Primary
                )],
                Id: "watch",
                Layer: WorldHudLayer.Over,
                Rect: new WorldHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f),
                Style: WorldHudPanelStyle.Chip
            ),
        };

        return identity;
    }
    private static string? Shown(WorldHudBindingResolver resolver, int seat) => (resolver.TryResolve(
        binding: Binding,
        fraction: out _,
        seat: seat,
        text: out var text
    )
        ? text
        : null
    );

    [Fact]
    public void ACrossedSeatsHudShowsItsRoutedWorld_AndAnUncrossedSeatReadsHome() {
        var home = Fixtures.BuildDocument().WithWorldState(rows: [Score(value: 1)]);
        var away = Fixtures.BuildDocument().WithWorldState(rows: [Score(value: 7)]);
        var client = ClientFixtures.Client(definition: home);
        var seats = new WorldSeatBindings(definition: home);
        var awayMirror = ClientFixtures.StateMirror(definition: away);

        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Authority: "boot",
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            Revision: 0,
            StepTicks: 1680UL,
            Tick: 1UL
        ));

        var resolver = new WorldHudBindingResolver(
            client: client,
            continuum: new WorldContinuum(
                client,
                new WorldSeatAuthorityRouter(),
                new NoNeighbours()
            ),
            frameRate: new FrameRateMonitor(),
            population: new WorldPopulation(definition: home),
            seatBindings: seats
        );

        foreach (var slot in ((int[])[0, 1])) {
            seats.SetProfileLayers(
                profile: ScoreWatcher(),
                slot: slot
            );
        }

        // Nothing either world authored binds the row, so neither mirror answers it before a seat registers.
        Assert.Equal(
            actual: (client.StateMirror.SlotOf(conversion: WorldStateConversion.Number, token: Binding), awayMirror.SlotOf(conversion: WorldStateConversion.Number, token: Binding)),
            expected: (-1, -1)
        );

        // Seat 1 reads home; seat 2 has crossed to the second authority and reads its mirror.
        seats.SyncSeat(
            definition: home,
            engineTick: 1680UL,
            entityIndex: 0,
            nextInputTick: 2UL,
            slot: 0,
            state: client.StateMirror
        );
        seats.SyncSeat(
            definition: away,
            engineTick: 1680UL,
            entityIndex: 1,
            nextInputTick: 2UL,
            slot: 1,
            state: awayMirror
        );

        Assert.Equal(
            actual: (Shown(resolver: resolver, seat: 0), Shown(resolver: resolver, seat: 1)),
            expected: ("1", "7")
        );

        // The seat crosses back: its panel reads home again, and its reads leave the second authority's mirror.
        seats.SyncSeat(
            definition: home,
            engineTick: 3360UL,
            entityIndex: 1,
            nextInputTick: 3UL,
            slot: 1,
            state: client.StateMirror
        );

        Assert.Equal(
            actual: Shown(resolver: resolver, seat: 1),
            expected: "1"
        );
        Assert.Equal(
            actual: awayMirror.SlotOf(conversion: WorldStateConversion.Number, token: Binding),
            expected: -1
        );
    }

    private sealed class NoNeighbours : IWorldAdjacencySource {
        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            return false;
        }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;

            return false;
        }
    }
}
