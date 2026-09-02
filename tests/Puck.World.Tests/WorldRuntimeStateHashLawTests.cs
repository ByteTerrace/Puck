using System.Text;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The named state hashes keep their declared boundaries deterministic and distinguish stored state that
/// can produce different future decisions even when its present resolved value happens to agree.</summary>
public sealed class WorldRuntimeStateHashLawTests {
    [Fact]
    public void CaptureScope_PreservesTheHistoricalPoseThenWorldValueFold() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: "count"),
                    Kind: CellKind.Int,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 17L)]
                ),
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: "label"),
                    Kind: CellKind.Text,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Text: "café")]
                ),
            ]),
        };

        using var fixture = Fixtures.FreshServer(definition: definition);
        var expected = Fnv1aHash.Create();

        expected.Add(value: WorldReplaySnapshot.HashState(population: fixture.Server.Population));
        expected.Add(value: 17L);
        expected.Add(value: 0L);
        expected.Add(values: Encoding.UTF8.GetBytes(s: "café"));

        Assert.Equal(
            expected: expected.Value,
            actual: WorldRuntimeStateHash.Hash(
                scope: WorldStateHashScope.Capture,
                server: fixture.Server,
                tick: 0UL
            )
        );
    }

    [Fact]
    public void PoseScope_IsTheReplayPoseDigest() {
        using var fixture = Fixtures.FreshServer();

        Assert.Equal(
            expected: WorldReplaySnapshot.HashState(population: fixture.Server.Population),
            actual: WorldRuntimeStateHash.Hash(
                scope: WorldStateHashScope.Pose,
                server: fixture.Server,
                tick: 0UL
            )
        );
    }

    [Fact]
    public void WorldScope_DistinguishesStoredAdvanceTraitsWithTheSameCurrentValue() {
        var name = WorldCellName.Parse(candidate: "future-state");
        var plain = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 5L)]
            )]),
        };
        var advancing = plain with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 5L)],
                Advance: new WorldStateAdvance(RateNumerator: 1L, RateDenominator: 1L, EpochTick: 0L)
            )]),
        };

        using var plainFixture = Fixtures.FreshServer(definition: plain);
        using var advancingFixture = Fixtures.FreshServer(definition: advancing);

        Assert.NotEqual(
            expected: WorldRuntimeStateHash.HashWorld(server: plainFixture.Server, tick: 0UL),
            actual: WorldRuntimeStateHash.HashWorld(server: advancingFixture.Server, tick: 0UL)
        );
    }

    [Fact]
    public void WorldScope_DistinguishesDrawTraitsWithTheSameCurrentValue() {
        var name = WorldCellName.Parse(candidate: "future-draw");
        var plain = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 5L)]
            )]),
        };
        var drawn = plain with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 5L)],
                Draw: new WorldDraw(
                    Generator: new WorldGenerator(
                        Source: WorldGeneratorSource.UniformRange,
                        RangeMin: 0L,
                        RangeMax: 10L
                    ),
                    Timing: WorldDrawTiming.Event
                )
            )]),
        };

        using var plainFixture = Fixtures.FreshServer(definition: plain);
        using var drawnFixture = Fixtures.FreshServer(definition: drawn);

        Assert.NotEqual(
            expected: WorldRuntimeStateHash.HashWorld(server: plainFixture.Server, tick: 0UL),
            actual: WorldRuntimeStateHash.HashWorld(server: drawnFixture.Server, tick: 0UL)
        );
    }

    [Fact]
    public void WorldScope_DistinguishesLiveLatticeCells() {
        var fields = new WorldFieldsSection(
            Lattice: new WorldFieldLatticeDefinition(
                Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
                CellSize: 1f,
                Width: 1,
                Depth: 1
            ),
            Fields: [new WorldFieldRow(Name: "heat", Min: 0f, Max: 10f)]
        );
        var definition = Fixtures.WithLattice(
            definition: Fixtures.BuildDocument(),
            composite: fields
        );

        using var left = Fixtures.FreshServer(definition: definition);
        using var right = Fixtures.FreshServer(definition: definition);

        Assert.IsType<WorldFieldLattice>(@object: right.Server.Population.Fields).Restore(
            checkpoint: new WorldFieldLattice.WorldFieldCheckpoint(Raw: [[FixedQ4816.One.Value]])
        );

        Assert.NotEqual(
            expected: WorldRuntimeStateHash.HashWorld(server: left.Server, tick: 0UL),
            actual: WorldRuntimeStateHash.HashWorld(server: right.Server, tick: 0UL)
        );
    }

    [Fact]
    public void AuthoritativeScope_IsStableAcrossEquivalentServers() {
        var definition = Fixtures.BuildDocument();

        using var left = Fixtures.FreshServer(definition: definition);
        using var right = Fixtures.FreshServer(definition: definition);

        Assert.Equal(
            expected: WorldRuntimeStateHash.HashAuthoritative(server: left.Server, tick: 0UL),
            actual: WorldRuntimeStateHash.HashAuthoritative(server: right.Server, tick: 0UL)
        );
    }
}
