using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <c>bodies.capacityRow</c>: admission settles every census the named row's source can draw into
/// a candidate and puts it through the whole document validator, so the verdict never moves with the world seed or
/// the instance identity.</summary>
public sealed class WorldCensusSourceLawTests {
    private static readonly string[] Identities = ["boot", "alpha", "beta"];
    private static readonly ulong[] Seeds = [0UL, 1UL, 7919UL];

    private static WorldDefinition CensusWorld(ulong worldSeed, StateGenerator generator) {
        var document = Fixtures.BuildDocument();

        return (document with {
            Generation = new WorldGenerationDefaults(WorldSeed: worldSeed),
            PopulationRaw = (document.Population with {
                CapacityRaw = null,
                CapacityRow = "census",
            }),
        }).WithWorldState(rows: [
            .. document.AuthoredState,
            new WorldStateRow(
                Name: CellName.Parse(candidate: "census"),
                Kind: CellKind.Int,
                Draw: new Draw(Generator: generator, Timing: DrawTiming.Boot)
            ),
        ]);
    }
    // What the boot resolver's own seed ladder produces at this site, so a law can state that the roll was
    // admissible and the document refused anyway.
    private static long SampledCensus(WorldDefinition definition, string instanceIdentity) {
        var row = definition.AuthoredState[^1];
        var site = WorldDrawSites.StateRow(rowName: row.Name);

        Assert.True(condition: GeneratorEngine.TryResolveSource(
            draw: row.Draw!,
            generator: out var generator,
            generators: definition.Generators,
            reason: out _
        ));
        Assert.True(condition: GeneratorEngine.TryFire(
            cursor: 0L,
            generator: generator,
            masks: null,
            reason: out var reason,
            result: out var fired,
            seedState: GeneratorEngine.ComputeSeedState(
                documentSeed: (definition.Generation?.WorldSeed ?? 0UL),
                instanceIdentity: instanceIdentity,
                site: site
            ),
            stream: GeneratorEngine.ComputeStreamId(site: site),
            targetKind: row.Kind
        ), userMessage: reason);

        return fired.Numeric!.Value;
    }
    private static StateGenerator Weighted(params (long Value, ulong Weight)[] outcomes) => new(
        Source: GeneratorSource.WeightedNumeric,
        Weighted: [.. outcomes.Select(selector: static outcome => new GeneratorWeightedNumeric(
            Value: outcome.Value,
            Weight: outcome.Weight
        ))]
    );
    private static bool TryLoad(WorldDefinition definition, string instanceIdentity, out string reason) =>
        WorldDefinitionLoader.TryLoadForAdmission(
            admission: out _,
            instanceIdentity: instanceIdentity,
            reason: out reason,
            sourceName: "census-source",
            utf8: WorldDefinitionSerialization.Serialize(definition: definition)
        );

    [Fact]
    public void CensusSourceSpanningMoreOutcomesThanTheProofAdmitsIsRefusedByName() {
        Assert.False(condition: TryLoad(
            definition: CensusWorld(
                generator: new StateGenerator(
                    Source: GeneratorSource.UniformRange,
                    RangeMin: ((long)WorldBodiesLimits.LocalSeatCount),
                    RangeMax: 200L
                ),
                worldSeed: 0UL
            ),
            instanceIdentity: "boot",
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "bodies.capacityRow names state row 'census'"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"more than the {WorldBodiesLimits.MaxDrawnCensusOutcomes} distinct values"
        );
    }
    [Fact]
    public void CensusSourceWhoseEveryOutcomeIsAdmissibleLoadsUnderEverySeedAndIdentity() {
        var generator = Weighted(
            (4L, 1UL),
            (8L, 1UL),
            (16L, 1UL)
        );

        foreach (var seed in Seeds) {
            var definition = CensusWorld(
                generator: generator,
                worldSeed: seed
            );

            foreach (var identity in Identities) {
                Assert.True(condition: TryLoad(
                    definition: definition,
                    instanceIdentity: identity,
                    reason: out var reason
                ), userMessage: reason);
            }
        }
    }
    [Fact]
    public void CensusSourceWithAnOutcomeBelowAnAuthoredBodyIndexIsRefused() {
        // The floor a drawn census must clear is not the seat count alone. A grant naming body:6 is one of the
        // document's own capacity-bounded references, and the proof reads it through the same validator that owns
        // it rather than through a second list of census terms.
        var definition = CensusWorld(
            generator: Weighted(
                (4L, 1UL),
                (8L, 100000UL)
            ),
            worldSeed: 0UL
        );

        definition = definition with {
            GrantsRaw = [
                .. definition.Grants,
                new WorldGrant(
                    Grantee: Principal.Seat(slot: 0),
                    Capability: WorldCapability.Observe,
                    Subject: GrantSubject.Body(index: 6),
                    Exclusive: false
                ),
            ],
        };

        Assert.Equal(
            actual: SampledCensus(
                definition: definition,
                instanceIdentity: "boot"
            ),
            expected: 8L
        );
        Assert.False(condition: TryLoad(
            definition: definition,
            instanceIdentity: "boot",
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "can draw census 4"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "body:6"
        );
    }
    [Fact]
    public void CensusSourceWithAnOutcomeBelowTheSeatFloorIsRefusedWhateverItRolls() {
        // A census of 2 cannot seat the fixture's four local seats. The bad outcome carries a weight no leg below
        // reaches, so each one rolls an admissible census and is refused anyway.
        var generator = Weighted(
            (2L, 1UL),
            (4L, 100000UL),
            (8L, 100000UL)
        );

        foreach (var seed in Seeds) {
            var definition = CensusWorld(
                generator: generator,
                worldSeed: seed
            );

            foreach (var identity in Identities) {
                Assert.InRange(
                    actual: SampledCensus(
                        definition: definition,
                        instanceIdentity: identity
                    ),
                    low: ((long)WorldBodiesLimits.LocalSeatCount),
                    high: ((long)WorldBodiesLimits.CapacityCeiling)
                );
                Assert.False(condition: TryLoad(
                    definition: definition,
                    instanceIdentity: identity,
                    reason: out var reason
                ));
                Assert.Contains(
                    actualString: reason,
                    expectedSubstring: "bodies.capacityRow names state row 'census'"
                );
                Assert.Contains(
                    actualString: reason,
                    expectedSubstring: "can draw census 2"
                );
            }
        }
    }
}
