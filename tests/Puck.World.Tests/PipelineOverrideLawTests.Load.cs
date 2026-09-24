using Puck.Commands;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Shaders;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the doors that bind a whole document's overrides rather than one mutation's: a <c>world.load</c> or
/// a boot refuses a value its source's config schema refuses, by name, and a recorded commit re-drives through
/// <c>replay.verify</c> to the outcome it had live.</summary>
public sealed partial class PipelineOverrideLawTests {
    private static WorldDefinition WithExposure(WorldDefinition definition, double exposure) => (definition with {
        ViewsRaw = (definition.Views with {
            Pipelines = [.. definition.Views.Pipelines.Select(selector: row => ((row.Name == "left")
                ? (row with { Overrides = new Dictionary<string, JsonElement> { ["visualize"] = Change(json: $"{{\"exposure\":{exposure.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}}}") } })
                : row))],
        }),
    });
    // Submits a world.load of a candidate and answers whether it installed; a refusal's echo is left in m_echoes.
    private bool Load(WorldFixture fixture, WorldDefinition candidate) {
        var before = m_echoes.Count;

        // A world.load reads the candidate from a file, so it carries that file's directory.
        candidate = (candidate with { DocumentDirectory = PuckPaths.Normalize(path: m_directory) });

        fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate)),
                Definition: candidate,
                Force: true,
                Kind: WorldRebuildKind.Load,
                PathHint: Path.Combine(
                    path1: m_directory,
                    path2: "loaded.world.json"
                )
            )
        );
        fixture.Step();

        return !m_echoes.Skip(count: before).Any(predicate: static echo => echo.Rejected);
    }

    [Fact]
    public void AWorldLoadBindsItsOverridesAndRefusesAnUnboundValueByName() {
        using var fixture = Server();

        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.load-unbound",
            controlOutcome: () => Load(
                candidate: WithExposure(definition: fixture.Server.Definition, exposure: 4),
                fixture: fixture
            ),
            deniedOutcome: () => {
                var accepted = Load(
                    candidate: WithExposure(definition: fixture.Server.Definition, exposure: 4.5),
                    fixture: fixture
                );

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.OverrideUnbound));

                return accepted;
            }
        );
        Assert.Equal(
            actual: Exposure(row: Row(fixture: fixture, name: "left")),
            expected: 4
        );
    }
    [Fact]
    public void ABootedDocumentBindsItsOverridesAndRefusesAnUnboundValueByName() {
        bool Boot(double exposure, out string reason) {
            using var fixture = Fixtures.FreshServer(definition: WithExposure(definition: Document(), exposure: exposure));

            fixture.Server.PipelineSources = new WorldPipelineSources(documentDirectory: m_directory);

            return fixture.Server.TryBindPipelineRows(reason: out reason);
        }

        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.boot-unbound",
            controlOutcome: () => Boot(exposure: 4, reason: out _),
            deniedOutcome: () => {
                var bound = Boot(exposure: 4.5, reason: out var reason);

                Assert.StartsWith(
                    actualString: reason,
                    expectedStartString: $"pipeline.overrides/{nameof(WorldPipelineOverrideRefusal.OverrideUnbound)}:"
                );

                return bound;
            }
        );
    }
    // A row's relative source resolves against the LOADED document's own directory, not the boot document's — both
    // where the server's own bind gate reads it (Host.PipelineSources, re-pointed once the load applies) and where
    // the rendering host reads it (WorldPipelineRuntime, re-pointed by WorldPostBuildWiring's identical Rebuild-echo
    // tap). "only-here.pipeline.json" exists in the loaded directory alone, so a resolution that stayed pinned to
    // the boot directory would refuse the load by name instead of accepting it.
    [Fact]
    public void AWorldLoadFromAnotherDirectoryBindsAndTheRuntimeResolvesARelativeSourceThere() {
        using var fixture = Server();
        var otherDirectory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-pipeline-overrides-other-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(path: otherDirectory);

        try {
            const string RelativeSource = "only-here.pipeline.json";

            File.Copy(
                destFileName: Path.Combine(path1: otherDirectory, path2: RelativeSource),
                sourceFileName: SourcePath
            );

            var candidate = (Document() with {
                ViewsRaw = (Document().Views with {
                    Pipelines = [
                        new WorldViewPipeline(Name: "left", Source: RelativeSource, Overrides: new Dictionary<string, JsonElement> { ["visualize"] = Change(json: "{\"exposure\":4}") }),
                        new WorldViewPipeline(Name: "right", Source: RelativeSource),
                    ],
                }),
            });
            var pathHint = Path.Combine(
                path1: otherDirectory,
                path2: "loaded.world.json"
            );

            // A world.load reads the candidate from a file, so it carries that file's directory.
            candidate = (candidate with { DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: pathHint) });

            fixture.Server.EnqueueRebuild(
                principal: Principal.Console,
                request: new WorldRebuildRequest(
                    ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate)),
                    Definition: candidate,
                    Force: true,
                    Kind: WorldRebuildKind.Load,
                    PathHint: pathHint
                )
            );
            fixture.Step();

            Assert.False(
                condition: m_echoes[^1].Rejected,
                userMessage: m_echoes[^1].Message
            );
            Assert.Equal(
                actual: fixture.Server.PipelineSources!.DocumentDirectory,
                expected: PuckPaths.Normalize(path: otherDirectory)
            );

            // The rendering host rebases the identical way WorldPostBuildWiring's Rebuild-echo tap does.
            using var runtime = new WorldPipelineRuntime(
                documentDirectory: m_directory,
                packager: new ShaderPackager(compiler: new ShaderCompiler(
                    cacheDirectory: Path.Combine(path1: m_directory, path2: "cache"),
                    toolchainDirectory: Path.Combine(path1: m_directory, path2: "no-tools")
                ))
            );

            runtime.Rebase(documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: pathHint));

            Assert.Equal(
                actual: runtime.DocumentDirectory,
                expected: Path.GetFullPath(path: otherDirectory)
            );
        } finally {
            Directory.Delete(
                path: otherDirectory,
                recursive: true
            );
        }
    }
    // The shadow server replay.verify re-drives through reads the recording's pipeline sources, so a recorded commit
    // binds there exactly as it bound live. Without the reader the same tape re-drives the commit to a refusal, which
    // the drive names as a mutation outcome that disagrees with the recording.
    [Fact]
    public void ARecordedCommitReDrivesToTheOutcomeItHadLive() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Server();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: fixture.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: fixture.Server.Profiles,
            transport: transport
        );
        var name = $"pipeline-commit-{Guid.NewGuid():N}";

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        // Submitted as a console pipeline.commit submits it, through the envelope door the tape records.
        transport.SubmitWorldMutation(mutation: Preview(exposure: 0.25, fixture: fixture, installed: Installed(), name: "left"));
        fixture.Step();
        tape.NoteTick();
        Assert.Equal(
            actual: Exposure(row: Row(fixture: fixture, name: "left")),
            expected: 0.25
        );
        _ = tape.StopRecording();

        Assert.Equal(
            actual: tape.Verify(name: name).DivergedAt,
            expected: -1
        );

        WorldReplaySnapshot recording;

        using (var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name))) {
            recording = WorldReplaySnapshot.Read(stream: stream);
        }

        Assert.Equal(
            actual: recording.PipelineSourceDirectory,
            expected: fixture.Server.PipelineSources!.DocumentDirectory
        );

        var unread = new WorldReplaySnapshot {
            DefinitionJson = recording.DefinitionJson,
            MountedAddons = recording.MountedAddons,
            PipelineSourceDirectory = null,
            RecordedAuthoritativeHashes = recording.RecordedAuthoritativeHashes,
            RecordedHashes = recording.RecordedHashes,
            Seats = recording.Seats,
            SimulationRate = recording.SimulationRate,
            Ticks = recording.Ticks,
        };
        var mismatch = Assert.Throws<InvalidDataException>(testCode: () => unread.DriveTraces(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: fixture.Server.Profiles
        ));

        Assert.Contains(
            actualString: mismatch.Message,
            expectedSubstring: "mutation #0"
        );
    }
}
