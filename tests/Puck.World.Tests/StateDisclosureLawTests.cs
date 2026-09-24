using Puck.Commands;
using System.Text;
using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class StateDisclosureLawTests {
    private static WorldDefinition Cards(string first = "seat1", string second = "seat2") => Fixtures.BuildDocument() with {
        StateRaw = new(World: [
            new(
            Name(value: "cards"),
            CellKind.Int,
            Cells: [StateFixtures.Cell(
                    key: "ace",
                    value: 101
                ), StateFixtures.Cell(
                    key: "king",
                    value: 202
                )],
            Visibility: new()
        ),
            new(
            Name(value: "handA"),
            CellKind.Bool,
            Cells: [StateFixtures.Cell(
                    key: "ace",
                    kind: CellKind.Bool,
                    value: 1
                )],
            Domain: new StateDomain.KeysOf(
                CellName.Parse(candidate: "cards"),
                Ordered: true
            ),
            Visibility: new([first])
        ),
            new(
            Name(value: "handB"),
            CellKind.Bool,
            Cells: [StateFixtures.Cell(
                    key: "king",
                    kind: CellKind.Bool,
                    value: 1
                )],
            Domain: new StateDomain.KeysOf(
                CellName.Parse(candidate: "cards"),
                Ordered: true
            ),
            Visibility: new([second])
        )
        ]),
    };
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    [Fact]
    public void KnowledgeRetainsLastSeenValueWhenSightIsLostAndRoundTrips() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(
            Lattices: [new LatticeTopology.Grid(
                    "map",
                    new DocumentVector3(
                        x: 0,
                        y: 0,
                        z: 0
                    ),
                    1,
                    2,
                    1
                )],
            World: [
                new(
                    Name(value: "pieces"),
                    CellKind.Int,
                    Capacity: 2,
                    Cells: [StateFixtures.Cell(key: "piece0", value: 0), StateFixtures.Cell(key: "piece1", value: 1)]
                ),
                new(
                    Name(value: "truth"),
                    CellKind.Int,
                    Cells: [StateFixtures.Cell(
                            key: "piece0",
                            value: 7
                        ), StateFixtures.Cell(
                            key: "piece1",
                            value: 9
                        )],
                    Domain: new StateDomain.KeysOf(Row: Name(value: "pieces")),
                    Visibility: new([])
                ),
                new(
                    Name(value: "positions"),
                    CellKind.Int,
                    Cells: [StateFixtures.Cell(key: "piece0", value: 0), StateFixtures.Cell(key: "piece1", value: 1)],
                    Domain: new StateDomain.KeysOf(Row: Name(value: "pieces")),
                    Visibility: new([])
                ),
                new(
                    Name(value: "sight"),
                    CellKind.Bool,
                    Cells: [StateFixtures.Cell(
                            key: "0",
                            kind: CellKind.Bool,
                            value: 1
                        )],
                    Domain: new StateDomain.CellsOf("map"),
                    Visibility: new([])
                ),
                new(
                    Name(value: "known"),
                    CellKind.Int,
                    Cells: [new StateCell(
                        Key: Name(value: "piece0"),
                        Value: CellValue.Int(value: 0L),
                        Observation: new StateObservation(Tick: 0, Visible: false)
                    )],
                    Domain: new StateDomain.KeysOf(Row: Name(value: "pieces")),
                    Visibility: new(["seat1"]),
                    Knowledge: new(
                        Mask: "sight",
                        Positions: "positions",
                        Source: "truth"
                    )
                )
            ]
        ),
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );
        // The arena kernels know no principals, so the ingress that stamped the acting principal is where an
        // observe submitted by anyone but the world's own rules is refused, by name.
        Assert.False(condition: WorldArenaTransforms.TryApply(
            definition,
            new StateTransform.Observe(Row: "known"),
            Principal.Seat(slot: 0),
            8,
            "test",
            out _,
            out var denial
        ));
        Assert.Contains(
            actualString: denial,
            expectedSubstring: "observe is a world-authored operation"
        );
        Assert.True(
            condition: WorldArenaTransforms.TryApply(
                definition,
                new StateTransform.Observe(Row: "known"),
                Principal.World,
                8,
                "test",
                out var seen,
                out reason
            ),
            userMessage: reason
        );
        var changed = seen.WithWorldState(rows: seen.State.Select(selector: r => r.Name.Value switch {
            "truth" => r with {
                Cells = [StateFixtures.Cell(
                key: "piece0",
                value: 42
            )],
            },
            "sight" => r with { Cells = [] },
            _ => r
        }).ToArray());

        Assert.True(
            condition: WorldArenaTransforms.TryApply(
                changed,
                new StateTransform.Observe(Row: "known"),
                Principal.World,
                12,
                "test",
                out var remembered,
                out reason
            ),
            userMessage: reason
        );
        var cell = Assert.Single(collection: Assert.Single(collection: Fixtures.Disclose(
            definition: remembered,
            recipient: Principal.Seat(slot: 0)
        )!).Cells);

        Assert.Equal(
            7,
            cell.Value
        );
        Assert.Equal(
            new StateObservation(
                Tick: 8,
                Visible: false
            ),
            cell.Observation
        );
        var moved = remembered.WithWorldState(rows: remembered.State.Select(selector: row => row.Name.Value switch {
            "positions" => row with { Cells = [StateFixtures.Cell(key: "piece0", value: 1), StateFixtures.Cell(key: "piece1", value: 0)] },
            "sight" => row with { Cells = [StateFixtures.Cell(key: "1", kind: CellKind.Bool, value: 1)] },
            _ => row,
        }).ToArray());

        Assert.True(
            condition: WorldArenaTransforms.TryApply(
                moved,
                new StateTransform.Observe(Row: "known"),
                Principal.World,
                16,
                "test",
                out var followed,
                out reason
            ),
            userMessage: reason
        );
        var followedCell = Assert.Single(collection: Assert.Single(collection: Fixtures.Disclose(
            definition: followed,
            recipient: Principal.Seat(slot: 0)
        )!).Cells);

        Assert.Equal("piece0", followedCell.Key);
        Assert.Equal(42, followedCell.Value);
        Assert.Equal(new StateObservation(Tick: 16, Visible: true), followedCell.Observation);
        var bytes = WorldDefinitionSerialization.Serialize(definition: remembered);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize(
            bytes,
            WorldJsonContext.Default.WorldDefinition
        )!;

        Assert.Equal(
            bytes,
            WorldDefinitionSerialization.Serialize(definition: reloaded)
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: reloaded,
                reason: out reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void ProjectionOmitsHiddenIdentitiesAndAttributesWithoutChangingAuthority() {
        var definition = Cards();
        var before = WorldDefinitionSerialization.Serialize(definition: definition);
        var a = Encoding.UTF8.GetString(bytes: WorldProjection.Serialize(projection: Fixtures.Project(
            definition,
            WorldDisclosureTier.Presentation,
            "test",
            1,
            Principal.Seat(slot: 0)
        )!));
        var b = Encoding.UTF8.GetString(bytes: WorldProjection.Serialize(projection: Fixtures.Project(
            definition,
            WorldDisclosureTier.Presentation,
            "test",
            1,
            Principal.Seat(slot: 1)
        )!));

        Assert.Contains(
            actualString: a,
            expectedSubstring: "ace"
        );
        Assert.DoesNotContain(
            actualString: a,
            expectedSubstring: "king"
        );
        Assert.DoesNotContain(
            actualString: a,
            expectedSubstring: "202"
        );
        Assert.Contains(
            actualString: b,
            expectedSubstring: "king"
        );
        Assert.DoesNotContain(
            actualString: b,
            expectedSubstring: "\"ace\""
        );
        Assert.DoesNotContain(
            actualString: a,
            expectedSubstring: "drawCursor"
        );
        Assert.Equal(
            before,
            WorldDefinitionSerialization.Serialize(definition: definition)
        );
        var persisted = System.Text.Json.JsonSerializer.Deserialize(
            before,
            WorldJsonContext.Default.WorldDefinition
        )!;

        Assert.Equal(
            before,
            WorldDefinitionSerialization.Serialize(definition: persisted)
        );
    }
    [Fact]
    public void SecretStreamsResumeAndRefuseIncompatibleSources() {
        var key = new ClosedBitset256(
            Word0: 1,
            Word1: 2,
            Word2: 3,
            Word3: 4
        );
        var generator = new StateGenerator(Source: GeneratorSource.StreamDraw);

        Assert.True(condition: GeneratorEngine.TryFire(
            generator,
            CellKind.Int,
            8,
            9,
            100,
            null,
            out var a,
            out _,
            key
        ));
        Assert.True(condition: GeneratorEngine.TryFire(
            generator,
            CellKind.Int,
            8,
            9,
            100,
            null,
            out var b,
            out _,
            key
        ));
        Assert.Equal(
            actual: b,
            expected: a
        );
        Assert.True(condition: GeneratorEngine.TryFire(
            generator,
            CellKind.Int,
            8,
            9,
            101,
            null,
            out var c,
            out _,
            key
        ));
        Assert.NotEqual(
            a.Numeric,
            c.Numeric
        );
        Assert.False(condition: GeneratorEngine.TryFire(
            generator,
            CellKind.Fixed,
            8,
            9,
            100,
            null,
            out _,
            out _,
            key
        ));
    }
    [Fact]
    public async Task TwoAuthenticatedSocketsReceiveOnlyTheirOwnCells() {
        var first = AdmissionWireFixture.GenerateIdentity(subject: "table-player-a");
        var second = AdmissionWireFixture.GenerateIdentity(subject: "table-player-b");
        using var firstKey = first.Key;
        using var secondKey = second.Key;
        var grants = new[] { new WorldAdmissionGrant(
            WorldCapability.Observe,
            GrantSubject.State(name: "hands"),
            Budget: 100
        ) };
        var baseline = AdmissionWireFixture.BuildAdmissionDocument(entry: AdmissionWireFixture.BuildEntry(
            grants: grants,
            identity: first
        ));
        var start = AdmissionWireFixture.PeerBodyIndex;
        var definition = baseline with {
            Admission = [AdmissionWireFixture.BuildEntry(
                grants: grants,
                identity: first
            ), AdmissionWireFixture.BuildEntry(
                grants: grants,
                identity: second
            )],
            PopulationRaw = baseline.Population with { CapacityRaw = (start + 2), NetworkPlayers = 2 },
            StateRaw = new(World: [new(
                Name(value: "hands"),
                CellKind.Int,
                Capacity: 2,
                Visibility: new(),
                Cells: [
                StateFixtures.Cell(
                        key: "cardA",
                        value: 101
                    ) with { Visibility = new([Principal.Peer(
                            generation: 1,
                            index: start
                        ).Describe()]) },
                StateFixtures.Cell(
                        key: "cardB",
                        value: 202
                    ) with { Visibility = new([Principal.Peer(
                            generation: 1,
                            index: (start + 1)
                        ).Describe()]) }
            ]
            )]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        using var host = AdmissionWireFixture.StartHost(
            clock: out _,
            server: fixture.Server
        );

        await using (AdmissionWireFixture.StartPump(
            fixture: fixture,
            host: host
        )) {
            var testToken = TestContext.Current.CancellationToken;
            var a = await AdmissionWireFixture.ConnectAndAdmitAsync(
                ct: testToken,
                host: host,
                identity: first
            );
            using var clientA = a.Client;
            var b = await AdmissionWireFixture.ConnectAndAdmitAsync(
                ct: testToken,
                host: host,
                identity: second
            );
            using var clientB = b.Client;

            Assert.Equal(
                1,
                a.Generation
            );
            Assert.Equal(
                1,
                b.Generation
            );
            var seenA = await AdmissionWireFixture.SubmitQueryAsync(
                clientA.GetStream(),
                new WorldQuery.StateObservations(Row: "hands"),
                testToken
            );
            var seenB = await AdmissionWireFixture.SubmitQueryAsync(
                clientB.GetStream(),
                new WorldQuery.StateObservations(Row: "hands"),
                testToken
            );

            Assert.False(
                condition: seenA.Refused,
                userMessage: seenA.Text
            );
            Assert.False(
                condition: seenB.Refused,
                userMessage: seenB.Text
            );
            var expectedA = ((a.PeerIndex == start)
                ? "cardA"
                : "cardB"
            );
            var expectedB = ((b.PeerIndex == start)
                ? "cardA"
                : "cardB"
            );

            Assert.NotEqual(
                actual: expectedB,
                expected: expectedA
            );
            Assert.Contains(
                expectedA,
                seenA.Text
            );
            Assert.DoesNotContain(
                expectedB,
                seenA.Text
            );
            Assert.Contains(
                expectedB,
                seenB.Text
            );
            Assert.DoesNotContain(
                expectedA,
                seenB.Text
            );
        }
    }
}
