using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A latch entry's binding is an interned cell-key ordinal, which a catalog that interned its keys in
/// another order gives another name. The checkpoint therefore carries the key's name and re-interns it on the way
/// back in.</summary>
public sealed class WorldCheckpointLatchBindingLawTests {
    private const string RuleName = "bump";

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    // `marks` gates an Edge rule per key; `hits` counts its crossings. Declaring the two marks in one order or the
    // other is what moves 'alpha' and 'beta' between intern ordinals.
    private static WorldDefinition Document(bool alphaFirst) {
        var marks = (alphaFirst
            ? new StateCell[] { new(
                    Key: Name(value: "alpha"),
                    Value: CellValue.Int(value: 1L)
                ), new(
                    Key: Name(value: "beta"),
                    Value: CellValue.Int(value: 0L)
                ), }
            : [new(
                    Key: Name(value: "beta"),
                    Value: CellValue.Int(value: 0L)
                ), new(
                    Key: Name(value: "alpha"),
                    Value: CellValue.Int(value: 1L)
                ),]
        );

        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: Name(value: "marks"),
                Kind: CellKind.Int,
                Cells: marks
            ),
                new WorldStateRow(
                Name: Name(value: "hits"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: Name(value: "alpha"),
                        Value: CellValue.Int(value: 0L)
                    ), new StateCell(
                        Key: Name(value: "beta"),
                        Value: CellValue.Int(value: 0L)
                    )]
            ),
            ]),
            Rules = [new WorldRule(
                    Name: Name(value: RuleName),
                    Effects: [new ActionEffect.AddState(
                            Key: "$each",
                            State: "hits",
                            Value: 1m
                        )],
                    ForEach: "marks",
                    Gate: new ActionPredicate.CompareState(
                        Comparison: ActionStateComparison.GreaterOrEqual,
                        Key: "$each",
                        State: "marks",
                        Value: 1m
                    ),
                    Mode: ActionTriggerMode.Edge
                )],
        };
    }
    private static long Hits(WorldServer server, string key) => (WorldDefinitionRows.FindStateRow(
        server.Definition.State,
        "hits"
    )!.Cells ?? []).First(predicate: cell => (cell.Key.Value == key)).Value.Raw;
    private static WorldServer Restore(WorldAuthorityCheckpoint checkpoint) {
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);

        var (server, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: new WorldMachineHost(
                engines: [],
                screens: definition.Screens
            ),
            profiles: new WorldOwnedWorlds(
                directory: Directory.CreateTempSubdirectory(prefix: "puck-latch-binding-tests-").FullName,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        return server;
    }

    // The checkpoint carries no lane section of its own: the arena's participant and identity slot lanes are not in
    // its export, body action state rides the population's codec, and an identity's fact row is an ordinary document
    // row. This is what says nothing the hash folds is left behind by that division.
    [Fact]
    public void ACheckpointRestoresEveryComponentTheAuthoritativeHashFolds() {
        using var fixture = Fixtures.FreshServer(definition: Document(alphaFirst: true));

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));
        _ = fixture.Server.Population.SetSimulatedCount(count: 2);

        for (var tick = 0; (tick < 8); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);

        var tickAt = (fixture.Server.NextInputTick - 1UL);
        var live = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: tickAt
        );
        var restored = Restore(checkpoint: checkpoint!);

        Assert.Equal(
            actual: WorldStateHashComposition.HashAuthoritative(
                server: restored,
                tick: tickAt
            ),
            expected: live
        );
    }
    [Fact]
    public void ACheckpointCarriesABoundLatchEntrysKeyByName() {
        using var fixture = Fixtures.FreshServer(definition: Document(alphaFirst: true));

        fixture.Step();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);

        var bound = checkpoint!.Server.RuleGateHeld.Where(predicate: entry => (entry.Rule == RuleName)).ToArray();

        Assert.NotEmpty(collection: bound);
        Assert.All(
            action: entry => {
                Assert.NotEqual(
                    actual: entry.Key,
                    expected: string.Empty
                );
                Assert.Equal(
                    actual: entry.Left,
                    expected: -1
                );
            },
            collection: bound
        );
        Assert.Contains(
            collection: bound,
            filter: entry => ((entry.Key == "alpha") && entry.Held)
        );
        Assert.Contains(
            collection: bound,
            filter: entry => ((entry.Key == "beta") && !entry.Held)
        );
    }
    [Fact]
    public void ARestoreUnderACatalogInternedInAnotherOrderKeepsTheSameBindings() {
        using var fixture = Fixtures.FreshServer(definition: Document(alphaFirst: true));

        fixture.Step();
        Assert.Equal(
            actual: Hits(
                key: "alpha",
                server: fixture.Server
            ),
            expected: 1L
        );
        Assert.Equal(
            actual: Hits(
                key: "beta",
                server: fixture.Server
            ),
            expected: 0L
        );
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);

        var alphaOrdinal = fixture.Server.Definition.StateCatalog.Keys.Names.ToList().FindIndex(match: name => (name.Value == "alpha"));
        var swapped = Document(alphaFirst: false);
        var swappedOrdinal = swapped.StateCatalog.Keys.Names.ToList().FindIndex(match: name => (name.Value == "alpha"));

        Assert.NotEqual(
            actual: swappedOrdinal,
            expected: alphaOrdinal
        );

        // The same world, re-declared with its two marks the other way round: the stored values are identical and
        // only the intern order moved. A restore that carried the raw ordinal would land 'alpha''s held gate on
        // 'beta'.
        var restored = Restore(checkpoint: checkpoint! with {
            Server = checkpoint.Server with { DefinitionJson = WorldDefinitionSerialization.Serialize(definition: swapped with { StateRaw = SwapMarksOf(definition: fixture.Server.Definition) }) },
        });

        restored.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Key: "beta",
            Kind: WorldDocumentWriteKind.Set,
            Principal: WorldPrincipal.Console,
            Row: "marks",
            Value: 1L
        ));
        restored.Advance(stepTicks: Fixtures.StepTicks);

        // 'alpha' held across the restore, so it never crosses again; 'beta' does.
        Assert.Equal(
            actual: Hits(
                key: "alpha",
                server: restored
            ),
            expected: 1L
        );
        Assert.Equal(
            actual: Hits(
                key: "beta",
                server: restored
            ),
            expected: 1L
        );
    }

    // The live document's own rows with `marks`' two cells reversed, so the restored catalog interns 'beta' first.
    private static WorldStateSection SwapMarksOf(WorldDefinition definition) {
        var rows = definition.State.Select(selector: static row => ((row.Name.Value == "marks")
            ? (row with { Cells = [.. (row.Cells ?? []).Reverse()] })
            : row
        )).ToArray();

        return (definition.StateRaw! with { World = rows });
    }
}
