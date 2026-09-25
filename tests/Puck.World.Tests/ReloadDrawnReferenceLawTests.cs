using System.Numerics;

using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <c>world.reload</c> of a world whose creation driver reads a Boot-timing draw cell: the reload
/// reads its document through the loader door, which fills the draw before admission, so the embedded document
/// crosses the loopback codec and rebuilds; and a rebuild the codec refuses answers its own line and counts one
/// <c>wire.errors</c> refusal on the stdin driver's path.</summary>
public sealed class ReloadDrawnReferenceLawTests {
    private const string Verb = "world.reload";

    private static DocumentScalar Bound(string reference) =>
        System.Text.Json.JsonSerializer.Deserialize<DocumentScalar>(
            json: $"\"{reference}\"",
            options: DocumentJsonOptions.Shared
        )!;
    // A creation driver whose cadence reads a Fixed cell that holds no value until the boot draw fills it, in [5, 8),
    // beside one authored grant, which a rebuild replays under its own correlation before its verdict.
    private static WorldDefinition DrawnCadenceWorld() {
        var world = DrawnCadenceRig();

        return world with {
            GrantsRaw = [
                .. world.Grants,
                new WorldGrant(
                    Grantee: Principal.Seat(slot: 0),
                    Capability: WorldCapability.Observe,
                    Subject: GrantSubject.Body(index: 1),
                    Exclusive: false
                ),
            ],
        };
    }
    private static WorldDefinition DrawnCadenceRig() => CreationFixtures.RigWorld(
        creation: CreationFixtures.Rig(
            drivers: [new CreationDriverDocument(
                Name: "stride",
                Signal: CreationDriverDocument.SignalPlanarTravel,
                Cadence: Bound(reference: "state.strideCadence"),
                When: ["always"]
            )],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(
                Driver: "stride",
                Pivot: Vector3.Zero,
                Axis: Vector3.UnitZ,
                Amplitude: 1f
            ))
        ),
        state: [new WorldStateRow(
            Name: CellName.Parse(candidate: "strideCadence"),
            Kind: CellKind.Fixed,
            Draw: new Draw(
                Generator: new StateGenerator(
                    Source: GeneratorSource.UniformRange,
                    RangeMin: 327680L,
                    RangeMax: 524288L
                ),
                Timing: DrawTiming.Boot
            )
        )]
    );
    private static float Cadence(WorldDefinition definition) => definition.Creations[^1].Document.Drivers![0].Cadence.Value;

    [Fact]
    public void AReloadOfABootDrawnDriverCadenceCrossesTheCodecAndRebuilds() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-reload-draw-");

        try {
            var path = Path.Combine(path1: directory.FullName, path2: "drawn.world.json");

            File.WriteAllBytes(bytes: WorldDefinitionSerialization.Serialize(definition: DrawnCadenceWorld()), path: path);
            Assert.True(
                condition: WorldDefinitionLoader.TryLoadFileForAdmission(
                    admission: out var admission,
                    contentHash: out var contentHash,
                    path: path,
                    reason: out var reason
                ),
                userMessage: reason
            );
            Assert.InRange(actual: Cadence(definition: admission!.Definition), high: 8f, low: 5f);

            using var fixture = Fixtures.FreshServer(definition: admission.Definition);
            var link = new LoopbackTransport(server: fixture.Server);
            var echoes = new WorldDeferredVerbEchoes();
            string? verdict = null;

            fixture.Server.EchoTap = echo => verdict ??= echoes.Settle(echo: in echo);

            var submitted = link.SubmitRebuild(
                echoes: echoes,
                principal: Principal.Console,
                request: new WorldRebuildRequest(
                    ContentHash: contentHash,
                    Definition: admission.Definition,
                    Force: false,
                    Kind: WorldRebuildKind.Reload,
                    PathHint: path
                ),
                verb: Verb
            );

            Assert.False(condition: submitted.IsError, userMessage: submitted.Output);
            fixture.Step();
            Assert.StartsWith(actualString: verdict, expectedStartString: $"[{Verb}: {Verb} applied — base is '{path}'");
            Assert.Equal(actual: Cadence(definition: fixture.Server.Definition), expected: Cadence(definition: admission.Definition));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void ACodecRefusedReloadAnswersItsLineAndCountsAWireError() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-reload-codec-");

        try {
            var path = Path.Combine(path1: directory.FullName, path2: "drawn.world.json");

            File.WriteAllBytes(bytes: WorldDefinitionSerialization.Serialize(definition: DrawnCadenceWorld()), path: path);
            Assert.True(
                condition: WorldDefinitionLoader.TryLoadFileForAdmission(
                    admission: out var drawn,
                    contentHash: out _,
                    path: path,
                    reason: out var drawnReason
                ),
                userMessage: drawnReason
            );
            // The parsed, undrawn shape: the driver's reference still names an empty cell, exactly the document the
            // codec's strict parse must refuse. No load door returns it, so the law parses it itself.
            Assert.True(
                condition: WorldDefinitionFileSource.TryReadContentPin(
                    contentHash: out var contentHash,
                    path: path,
                    reason: out var pinReason
                ),
                userMessage: pinReason
            );
            Assert.True(
                condition: WorldDefinitionFileSource.TryParseDocument(
                    definition: out var undrawn,
                    json: File.ReadAllText(path: path),
                    reason: out var undrawnReason,
                    sourceName: path
                ),
                userMessage: undrawnReason
            );

            using var fixture = Fixtures.FreshServer(definition: drawn!.Definition);
            var link = new LoopbackTransport(server: fixture.Server);
            var echoes = new WorldDeferredVerbEchoes();
            var answers = new List<CommandResult>();
            var registry = new CommandRegistry(
                modules: [new ReloadModule(submit: () => link.SubmitRebuild(
                    echoes: echoes,
                    principal: Principal.Console,
                    request: new WorldRebuildRequest(
                        ContentHash: contentHash,
                        Definition: undrawn,
                        Force: false,
                        Kind: WorldRebuildKind.Reload,
                        PathHint: path
                    ),
                    verb: Verb
                ))],
                observers: [new AnswerObserver(answers: answers)]
            );

            using (var router = new InputRouter(bindings: new NoBindings(), principalResolver: new ConsolePrincipal(), registry: registry)) {
                var source = new TextCommandSource(registry: registry);

                using (var session = source.CreateSession(principal: Principal.Console, simulationSink: router.ConsoleTextSink)) {
                    session.Enqueue(line: Verb);
                    source.Collect();

                    var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

                    registry.ApplySnapshot(snapshot: in snapshot);
                }
            }

            var answer = Assert.Single(collection: answers);

            Assert.True(condition: answer.IsError);
            Assert.StartsWith(actualString: answer.Output, expectedStartString: $"[{Verb}: world.transport.codec_refused ");
            Assert.Contains(actualString: answer.Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "'state.strideCadence' names a cell that holds no value");
            Assert.Equal(actual: registry.Submit(line: "wire.errors").Output, expected: "[wire.errors: 1 rejected]");
        } finally {
            directory.Delete(recursive: true);
        }
    }

    private sealed class ReloadModule(Func<CommandResult> submit) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Submits the reload under test.",
                handler: (_, _) => submit(),
                name: Verb,
                routing: CommandRouting.Simulation
            );
        }
    }
    // Prints what the stdin driver's output observer prints: every text line's non-empty result.
    private sealed class AnswerObserver(List<CommandResult> answers) : ICommandObserver {
        public void OnCommand(in CommandActivation activation) {
            if (
                (activation.Text is not null) &&
                !string.IsNullOrEmpty(value: activation.Result.Output)
            ) {
                answers.Add(item: activation.Result);
            }
        }
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
}
