using System.Text.Json.Nodes;
using Puck.Cli.Shaders;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>The echo fixture writer derives both SDF families from the live package blocks and keeps their
/// sources and capture extents together. Check mode reports drift without changing the fixture.</summary>
public sealed class ShadersInterfaceEchoLawTests {
    private static readonly (string Echo, string Package)[] Families = [
        ("sdf-world", RenderGraphPackageCatalog.SdfWorld),
        ("indirect", RenderGraphPackageCatalog.Indirect),
    ];

    private static JsonObject Manifest() {
        var fixtures = new JsonArray();
        var positive = new JsonArray();
        var discriminating = new JsonArray();

        foreach (var (echo, _) in Families) {
            foreach (var name in ((string[])[echo, $"{echo}-perturbed"])) {
                fixtures.Add(item: $"{name}.graph.json");
                fixtures.Add(item: $"{name}.echo.hlsl");
            }
            JsonObject Observation(bool holds, double right) => new() {
                ["type"] = "imageRegion",
                ["capture"] = $"{echo}.png",
                ["holds"] = holds,
                ["extent"] = new JsonArray(items: [JsonValue.Create(value: 1), JsonValue.Create(value: 1)]),
                ["region"] = new JsonArray(items: [JsonValue.Create(value: 0), JsonValue.Create(value: 0), JsonValue.Create(value: right), JsonValue.Create(value: 1)]),
            };
            positive.Add(item: Observation(holds: true, right: 1));
            discriminating.Add(item: Observation(holds: false, right: 1));
            discriminating.Add(item: Observation(holds: true, right: 0.5));
        }

        return new JsonObject {
            ["fixtures"] = fixtures,
            ["positive"] = new JsonObject { ["commands"] = "keep the positive commands", ["expect"] = positive },
            ["discriminating"] = new JsonObject { ["commands"] = "keep the discriminating commands", ["expect"] = discriminating },
        };
    }
    private static (int ExitCode, string Error) Run(string directory, bool check) {
        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => InterfaceEchoFixtures.Run(directory: directory, check: check));

        return (exitCode, error);
    }
    private static ShaderInterface Target(string packageId) {
        Assert.True(condition: RenderGraphPackageCatalog.Engine.TryGet(id: packageId, package: out var package));

        return ShaderPipelineParameterLayout.ForPackage(config: package.Config, members: package.Members,
            package: package.Id, pushesIndex: package.PushesIndex).Interface;
    }

    [Fact]
    public void Writer_rebuilds_both_model_graphs_sources_and_all_three_capture_regions() {
        using var root = new TemporaryDirectory(prefix: "puck-echo-fixtures-");
        var original = Manifest();

        root.WriteText(name: "canary.json", text: original.ToJsonString());
        var written = Run(directory: root.RootPath, check: false);

        Assert.True(condition: (written.ExitCode == 0), userMessage: written.Error);
        var manifest = JsonNode.Parse(json: File.ReadAllText(path: Path.Combine(path1: root.RootPath, path2: "canary.json")))!;

        foreach (var (echo, package) in Families) {
            var expected = Target(packageId: package).Layout().Groups.Where(predicate: static group => (group.BlockMembers.Count != 0)).ToArray();
            var width = expected.Sum(selector: static group => group.BlockMembers.Count(predicate: static member =>
                !member.Name.StartsWith(value: "_pad", comparisonType: StringComparison.Ordinal)));

            foreach (var name in ((string[])[echo, $"{echo}-perturbed"])) {
                var path = Path.Combine(path1: root.RootPath, path2: $"{name}.graph.json");
                var definition = ShaderPipelineLoader.ReadDefinition(name: name, path: path);
                var pass = Assert.Single(collection: new ShaderPipelineCompiler().Compile(definition: definition).Passes);
                var actual = pass.Parameters.Layout.Groups.Where(predicate: static group => (group.BlockMembers.Count != 0)).ToArray();

                Assert.Equal(expected: expected.Select(selector: static group => (group.Group, group.Set, group.BlockSizeBytes)),
                    actual: actual.Select(selector: static group => (group.Group, group.Set, group.BlockSizeBytes)));
                for (var index = 0; (index < expected.Length); index++) {
                    Assert.Equal(expected: expected[index].BlockMembers, actual: actual[index].BlockMembers);
                }
                Assert.Equal(expected: (((uint)width), 1u), actual: Assert.Single(collection: definition.Resources).Dimensions!.Resolve(frameWidth: 256, frameHeight: 256));
                var source = File.ReadAllText(path: Path.Combine(path1: root.RootPath, path2: $"{name}.echo.hlsl"));

                Assert.Equal(expected: ((name == echo) ? ShaderInterfaceEcho.Generate(shaderInterface: pass.Parameters.Interface)
                    : ShaderInterfaceEcho.GeneratePerturbed(shaderInterface: pass.Parameters.Interface)), actual: source);
            }
            foreach (var leg in ((string[])["positive", "discriminating"])) {
                Assert.Equal(expected: original[leg]!["commands"]!.GetValue<string>(), actual: manifest[leg]!["commands"]!.GetValue<string>());
                var observations = manifest[leg]!["expect"]!.AsArray().Where(predicate: node => (node!["capture"]!.GetValue<string>() == $"{echo}.png")).ToArray();

                Assert.Equal(expected: ((leg == "positive") ? 1 : 2), actual: observations.Length);
                for (var index = 0; (index < observations.Length); index++) {
                    var observation = observations[index]!;

                    Assert.Equal(expected: width, actual: observation["extent"]![0]!.GetValue<int>());
                    Assert.Equal(expected: 1, actual: observation["extent"]![1]!.GetValue<int>());
                    Assert.Equal(expected: ((index == 0) ? 1d : ((width - 1d) / width)), actual: observation["region"]![2]!.GetValue<double>());
                    Assert.Equal(expected: ((leg == "positive") || (index == 1)), actual: observation["holds"]!.GetValue<bool>());
                }
            }
        }
        Assert.Equal(expected: (0, ""), actual: Run(directory: root.RootPath, check: true));
    }
    [Fact]
    public void Check_reports_drift_without_writing_and_record_repairs_the_family() {
        using var root = new TemporaryDirectory(prefix: "puck-echo-fixtures-");

        root.WriteText(name: "canary.json", text: Manifest().ToJsonString());
        Assert.Equal(expected: (0, ""), actual: Run(directory: root.RootPath, check: false));
        var names = new[] { "sdf-world.graph.json", "indirect-perturbed.echo.hlsl", "canary.json" };
        var texts = names.ToDictionary(keySelector: static name => name,
            elementSelector: name => File.ReadAllText(path: Path.Combine(path1: root.RootPath, path2: name)), comparer: StringComparer.Ordinal);

        root.WriteText(name: names[0], text: "{}");
        root.WriteText(name: names[1], text: "stale echo");
        var manifest = JsonNode.Parse(json: texts["canary.json"])!;

        manifest["positive"]!["expect"]![0]!["extent"]![0] = 1;
        root.WriteText(name: "canary.json", text: manifest.ToJsonString());
        var before = Directory.GetFiles(path: root.RootPath).ToDictionary(keySelector: static path => path, elementSelector: File.ReadAllText);
        var check = Run(directory: root.RootPath, check: true);

        Assert.Equal(expected: 1, actual: check.ExitCode);
        foreach (var name in names) {
            Assert.Contains(expectedSubstring: name, actualString: check.Error);
        }
        foreach (var (path, text) in before) {
            Assert.Equal(expected: text, actual: File.ReadAllText(path: path));
        }
        Assert.Equal(expected: (0, ""), actual: Run(directory: root.RootPath, check: false));
        foreach (var (name, text) in texts) {
            Assert.Equal(expected: text, actual: File.ReadAllText(path: Path.Combine(path1: root.RootPath, path2: name)));
        }
    }
}
