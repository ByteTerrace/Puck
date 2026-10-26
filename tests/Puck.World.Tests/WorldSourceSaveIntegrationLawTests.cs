using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A live placement edit persists through the source printer and reloads as the same authored document.</summary>
public sealed class WorldSourceSaveIntegrationLawTests {
    [Fact]
    public void LiveNudgeSavesAndReloadsWithUnrelatedSourceIntact() {
        using var row = EditorPlacementFixtures.Build();
        using var files = new TemporaryDirectory();
        var source = (("// Keep this authored heading.\n" + WorldDecompiler.Decompile(root: JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: row.Server.Definition))!.AsObject())) + "\n// Keep this constant and its spacing.\nlet retained = 3\n");
        var path = files.WriteText(name: "world.puck", text: source);
        var registry = WorldEditorPlacementLawTests.BuildRegistry(row: row, seats: new WorldEditorSeats());
        var original = row.Server.Definition.Placements.Single().Position;

        var submitted = registry.Submit(line: "world.nudge crate1 x 1");

        Assert.False(condition: submitted.IsError, userMessage: submitted.Output);
        row.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.NotEqual(expected: original, actual: row.Server.Definition.Placements.Single().Position);
        Assert.True(condition: WorldSourceSave.TrySave(definition: row.Server.Definition, path: path, bytesWritten: out _, reason: out var reason), userMessage: reason);

        var saved = File.ReadAllText(path: path);

        Assert.StartsWith(actualString: saved, expectedStartString: "// Keep this authored heading.\n");
        Assert.EndsWith(actualString: saved, expectedEndString: "\n// Keep this constant and its spacing.\nlet retained = 3\n");
        var compiled = WorldCompiler.Compile(source: saved, sourcePath: path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: WorldSourceLoader.TryReadAuthored(path: path, document: Encoding.UTF8.GetBytes(s: compiled.RequireJson().ToJsonString()), authored: out var reloaded, reason: out reason), userMessage: reason);
        Assert.True(condition: JsonNode.DeepEquals(node1: JsonNode.Parse(utf8Json: WorldDefinitionSerialization.SerializeBeside(definition: row.Server.Definition, path: path)), node2: JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: reloaded!))));
    }
    [Fact]
    public void RuleSaveEffectPreservesAuthoredPuckSource() {
        using var row = EditorPlacementFixtures.Build();
        using var files = new TemporaryDirectory(bestEffortDelete: true, prefix: "puck-rule-save-");
        var definition = row.Server.Definition;
        var source = (("// Keep this authored heading.\n" + WorldDecompiler.Decompile(root: JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition))!.AsObject())) + "\n// Keep this constant and its spacing.\nlet retained = 3\n");
        var path = files.WriteText(name: "world.puck", text: source);
        var extensions = Puck.Abstractions.PuckExtensionSet.Compose(extensions: []);
        var builder = Host.CreateEmptyApplicationBuilder(settings: new());

        builder.Services.AddLogging();
        builder.Services.AddWorldBoot(inputs: new WorldBootInputs(
            Authenticator: new InertAuthenticator(),
            Caches: new WorldCacheRoots(
                compilations: Path.Join(path1: files.RootPath, path2: "compilations"),
                compiledWorlds: Path.Join(path1: files.RootPath, path2: "compiled-worlds"),
                meshes: Path.Join(path1: files.RootPath, path2: "meshes")
            ),
            Extensions: extensions,
            HostSettings: WorldHostSettings.Resolve(
                backendOverride: null,
                defaults: definition.Host,
                directXAvailable: false,
                exitAfterSecondsOverride: null,
                heightOverride: null,
                presentationOverride: WorldHostPresentation.None,
                presentModeOverride: null,
                widthOverride: null
            ),
            MachineCatalog: Puck.World.Machines.WorldMachineCatalog.From(extensions: extensions),
            Source: new WorldDefinitionSource(
                Definition: definition,
                SourcePath: path
            ),
            StateRoot: new WorldStateRoot(path: Path.Join(path1: files.RootPath, path2: "state"))
        ));
        using var host = builder.Build();

        Assert.True(condition: WorldPostBuildWiring.Install(services: host.Services));

        var server = host.Services.GetRequiredService<WorldServer>();
        var crate = server.Definition.Placements.Single();

        server.EnqueueMutation(mutation: new Puck.World.Protocol.WorldMutation.UpsertPlacement(
            Principal: Puck.Commands.Principal.Console,
            Placement: crate with { YawDegrees = 90f }
        ));
        server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.NotNull(@object: server.SaveEffectTap);
        server.SaveEffectTap!(obj: (server.NextInputTick - 1UL));

        var saved = File.ReadAllText(path: path);

        Assert.StartsWith(actualString: saved, expectedStartString: "// Keep this authored heading.\n");
        Assert.EndsWith(actualString: saved, expectedEndString: "\n// Keep this constant and its spacing.\nlet retained = 3\n");
        Assert.Contains("90", saved);
    }
}
