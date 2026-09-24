using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Laws for <c>host.backendRow</c>: admission proves the named row's source over every emission it can
/// produce, so the verdict never moves with the world seed or the instance identity.</summary>
public sealed class WorldBootRowSourceLawTests {
    private static readonly string[] Identities = ["boot", "alpha", "beta"];
    private static readonly ulong[] Seeds = [0UL, 1UL, 7919UL];

    private static WorldDefinition BackendWorld(ulong worldSeed, StateGenerator generator) => new WorldDefinition(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        Generation: new WorldGenerationDefaults(WorldSeed: worldSeed),
        HostRaw: (WorldHostDefaults.Absent with {
            Width = 320,
            Height = 200,
            Backend = null,
            BackendRow = "renderer",
        })
    ).WithWorldState(rows: [new WorldStateRow(
        Name: CellName.Parse(candidate: "renderer"),
        Kind: CellKind.Text,
        Draw: new Draw(Generator: generator, Timing: DrawTiming.Boot)
    )]);
    private static StateGenerator MarkovTokens(params (string Token, ulong Weight)[] alternatives) => new(
        Source: GeneratorSource.Markov,
        Start: CellName.Parse(candidate: "pick"),
        Contexts: [
            new GeneratorContext(
                Key: CellName.Parse(candidate: "pick"),
                Alternatives: [.. alternatives.Select(selector: static alternative => new GeneratorAlternative(
                    Token: alternative.Token,
                    Weight: alternative.Weight,
                    Next: CellName.Parse(candidate: "done")
                ))]
            ),
            new GeneratorContext(Key: CellName.Parse(candidate: "done")),
        ]
    );
    // What the boot resolver's own seed ladder produces at this site, so a law can state that the roll was
    // admissible and the document refused anyway.
    private static string SampledToken(WorldDefinition definition, string instanceIdentity) {
        var row = definition.AuthoredState[0];
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

        return fired.Text!;
    }
    private static bool TryLoad(WorldDefinition definition, string instanceIdentity, out string reason) =>
        WorldDefinitionLoader.TryLoadForAdmission(
            admission: out _,
            instanceIdentity: instanceIdentity,
            reason: out reason,
            sourceName: "boot-row-source",
            utf8: WorldDefinitionSerialization.Serialize(definition: definition)
        );

    [Fact]
    public void BackendSourceEmittingATokenNamingNoBackendIsRefusedWhateverItRolls() {
        // 'metal' carries a weight no leg below reaches, so each one rolls an admissible token and is refused for
        // the outcome it did not roll.
        var generator = MarkovTokens(
            ("auto", 100000UL),
            ("vulkan", 100000UL),
            ("metal", 1UL)
        );

        foreach (var seed in Seeds) {
            var definition = BackendWorld(
                generator: generator,
                worldSeed: seed
            );

            foreach (var identity in Identities) {
                Assert.NotNull(@object: WorldHostTokens.ParseBackend(token: SampledToken(
                    definition: definition,
                    instanceIdentity: identity
                )));
                Assert.False(condition: TryLoad(
                    definition: definition,
                    instanceIdentity: identity,
                    reason: out var reason
                ));
                Assert.Contains(
                    actualString: reason,
                    expectedSubstring: "host.backendRow names state row 'renderer'"
                );
                Assert.Contains(
                    actualString: reason,
                    expectedSubstring: "can emit 'metal'"
                );
            }
        }
    }
    [Fact]
    public void BackendSourceEmittingMoreThanOneTokenIsRefusedByName() {
        var definition = BackendWorld(
            generator: new StateGenerator(
                Source: GeneratorSource.Markov,
                Bound: 4,
                Start: CellName.Parse(candidate: "pick"),
                Contexts: [
                    new GeneratorContext(
                        Key: CellName.Parse(candidate: "pick"),
                        Alternatives: [new GeneratorAlternative(
                            Token: "auto",
                            Weight: 1UL,
                            Next: CellName.Parse(candidate: "again")
                        )]
                    ),
                    new GeneratorContext(
                        Key: CellName.Parse(candidate: "again"),
                        Alternatives: [new GeneratorAlternative(
                            Token: "vulkan",
                            Weight: 1UL,
                            Next: CellName.Parse(candidate: "done")
                        )]
                    ),
                    new GeneratorContext(Key: CellName.Parse(candidate: "done")),
                ]
            ),
            worldSeed: 0UL
        );

        Assert.False(condition: TryLoad(
            definition: definition,
            instanceIdentity: "boot",
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "can emit 'auto vulkan'"
        );
    }
    [Fact]
    public void BackendSourceThatCanReEnterAContextIsRefusedByName() {
        var definition = BackendWorld(
            generator: new StateGenerator(
                Source: GeneratorSource.Markov,
                Bound: 8,
                Start: CellName.Parse(candidate: "pick"),
                Contexts: [
                    new GeneratorContext(
                        Key: CellName.Parse(candidate: "pick"),
                        Alternatives: [new GeneratorAlternative(
                            Token: "auto",
                            Weight: 1UL,
                            Next: CellName.Parse(candidate: "pick")
                        )]
                    ),
                ]
            ),
            worldSeed: 0UL
        );

        Assert.False(condition: TryLoad(
            definition: definition,
            instanceIdentity: "boot",
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "can re-enter context 'pick'"
        );
    }
    [Fact]
    public void BackendSourceWhoseEveryEmissionNamesABackendLoadsUnderEverySeedAndIdentity() {
        var generator = MarkovTokens(
            ("auto", 1UL),
            ("directx", 1UL),
            ("vulkan", 1UL)
        );

        foreach (var seed in Seeds) {
            var definition = BackendWorld(
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
}
