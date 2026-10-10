using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary>Owns the interface-echo canary's model-derived graph blocks, echo sources and capture extents.
/// Planning the entire family precedes writing any file; no shader compiler or GPU is involved.</summary>
public static class InterfaceEchoFixtures {
    private static readonly JsonSerializerOptions JsonOptions = new() { NewLine = "\n", WriteIndented = true };
    private static readonly (string Echo, string Package)[] ModelEchoes = [
        ("sdf-world", RenderGraphPackageCatalog.SdfWorld),
        ("indirect", RenderGraphPackageCatalog.Indirect),
    ];

    private static string Serialize(JsonNode node) => (node.ToJsonString(options: JsonOptions) + "\n");
    private static JsonObject Read(string path) => (JsonNode.Parse(json: File.ReadAllText(path: path))?.AsObject()
        ?? throw new InvalidDataException(message: $"{path} holds no JSON object."));
    private static ShaderInterface InterfaceOf(string name, string directory, string text) =>
        new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ParseDefinition(
            name: name,
            path: Path.Combine(path1: directory, path2: $"{name}.graph.json"),
            text: text
        )).Passes.Single().Parameters.Interface;
    // The graph language supplies the common frame block and extent. Package pass values become config;
    // the layout comparison below refuses a package whose blocks this representation cannot reproduce.
    private static JsonObject Graph(string name, ShaderInterface target) {
        var config = new JsonObject();

        foreach (var member in target.Members.Where(predicate: static member => (
            (member.Group == ShaderInterfaceGroup.Pass) && (member.Kind == ShaderInterfaceMemberKind.Value) &&
            (member.Name != ShaderFrameInterface.Extent)))) {
            var field = new JsonObject { ["type"] = member.Type!.Value.Spelling() };

            if (member.Length is { } length) {
                field["length"] = length;
                field["default"] = new JsonArray(items: [.. Enumerable.Range(count: checked((int)length), start: 0)
                    .Select(selector: _ => Zero(type: member.Type.Value))]);
            } else {
                field["default"] = Zero(type: member.Type.Value);
            }
            field["description"] = $"The {target.Name} pass's {member.Name} sentinel.";
            config[member.Name] = field;
        }

        return new JsonObject {
            ["$schema"] = "puck.render.graph.v1",
            ["name"] = name,
            ["resources"] = new JsonArray(items: [new JsonObject {
                ["name"] = ShaderInterfaceEcho.OutputName,
                ["kind"] = "Image",
                ["format"] = "R8G8B8A8Unorm",
                ["dimensions"] = new JsonObject {
                    ["mode"] = "Absolute",
                    ["width"] = ShaderInterfaceEcho.Width(shaderInterface: ShaderInterfaceEcho.InterfaceOf(shaderInterface: target)),
                    ["height"] = 1u,
                },
            }]),
            ["passes"] = new JsonArray(items: [new JsonObject {
                ["name"] = name,
                ["source"] = $"{name}.echo.hlsl",
                ["entryPoint"] = "main",
                ["kind"] = "Compute",
                ["outputs"] = new JsonArray(items: [new JsonObject { ["name"] = ShaderInterfaceEcho.OutputName }]),
                ["config"] = config,
            }]),
            ["outputs"] = new JsonArray(items: [JsonValue.Create(value: ShaderInterfaceEcho.OutputName)]),
        };
    }
    private static JsonNode Zero(ShaderValueType type) => ((type.ComponentCount() == 1)
        ? JsonValue.Create(value: 0)!
        : new JsonArray(items: [.. Enumerable.Range(start: 0, count: ((int)type.ComponentCount()))
            .Select(selector: static _ => JsonValue.Create(value: 0))]));
    private static void RequireSameBlocks(string name, ShaderInterface expected, ShaderInterface actual) {
        static IEnumerable<(ShaderInterfaceGroup Group, uint Set, uint Size, string Name, uint Offset, ShaderValueType Type, uint Length)> Members(ShaderInterface value) =>
            value.Layout().Groups.SelectMany(selector: static group => group.BlockMembers.Select(selector: member =>
                (group.Group, group.Set, group.BlockSizeBytes, member.Name, member.Offset, member.Type, member.Length)));

        if (!Members(value: expected).SequenceEqual(second: Members(value: actual))) {
            throw new InvalidDataException(message: $"{name}: the echo graph does not reproduce its target's block layout.");
        }
    }
    private static Dictionary<string, string> Plan(string directory) {
        var manifest = Read(path: Path.Combine(path1: directory, path2: "canary.json"));
        var fixtures = manifest["fixtures"]!.AsArray().Select(selector: static item => item!.GetValue<string>()).ToHashSet(comparer: StringComparer.Ordinal);
        var echoes = fixtures.Where(predicate: static file => (file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".echo.hlsl")
            && !file.EndsWith(comparisonType: StringComparison.Ordinal, value: "-perturbed.echo.hlsl")))
            .Select(selector: static file => file[..^".echo.hlsl".Length]).Order(comparer: StringComparer.Ordinal).ToArray();
        var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var (echo, _) in ModelEchoes) {
            if (!echoes.Contains(value: echo, comparer: StringComparer.Ordinal)) {
                throw new InvalidDataException(message: $"canary.json must declare {echo}.echo.hlsl.");
            }
        }
        foreach (var echo in echoes) {
            if ((echo.Length == 0) || echo.Contains(value: '/') || echo.Contains(value: '\\') || (echo is "." or "..")) {
                throw new InvalidDataException(message: $"The echo name '{echo}' must be a file stem in the canary directory.");
            }
            var model = ModelEchoes.FirstOrDefault(predicate: item => (item.Echo == echo));
            ShaderInterface? target = null;

            if (model.Package is not null) {
                if (!RenderGraphPackageCatalog.Engine.TryGet(id: model.Package, package: out var package)) {
                    throw new InvalidDataException(message: $"No engine package declares {model.Package}.");
                }
                target = ShaderPipelineParameterLayout.ForPackage(config: package.Config, members: package.Members,
                    package: package.Id, pushesIndex: package.PushesIndex).Interface;
            }
            ShaderInterface? positive = null;
            var width = 0U;

            foreach (var name in ((string[])[echo, $"{echo}-perturbed"])) {
                var graphPath = $"{name}.graph.json";
                var hlslPath = $"{name}.echo.hlsl";

                if (!fixtures.Contains(item: graphPath) || !fixtures.Contains(item: hlslPath)) {
                    throw new InvalidDataException(message: $"canary.json must declare both {graphPath} and {hlslPath}.");
                }
                var original = ((target is null) ? File.ReadAllText(path: Path.Combine(path1: directory, path2: graphPath)) : null);
                var graph = ((target is null) ? JsonNode.Parse(json: original!)!.AsObject() : Graph(name: name, target: target));
                var shaderInterface = InterfaceOf(name: name, directory: directory, text: Serialize(node: graph));

                if (target is not null) {
                    RequireSameBlocks(actual: shaderInterface, expected: target, name: name);
                }
                if (positive is null) {
                    positive = shaderInterface;
                    width = ShaderInterfaceEcho.Width(shaderInterface: shaderInterface);
                } else {
                    RequireSameBlocks(actual: shaderInterface, expected: positive, name: name);
                }
                var dimensions = graph["resources"]!.AsArray().Single(predicate: resource =>
                    (resource!["name"]!.GetValue<string>() == ShaderInterfaceEcho.OutputName))!["dimensions"]!.AsObject();
                var changed = ((dimensions["width"]!.GetValue<uint>() != width) || (dimensions["height"]!.GetValue<uint>() != 1u));

                dimensions["width"] = width;
                dimensions["height"] = 1u;
                files[graphPath] = (((original is not null) && !changed) ? original.ReplaceLineEndings(replacementText: "\n") : Serialize(node: graph));
                files[hlslPath] = ((name == echo) ? ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface)
                    : ShaderInterfaceEcho.GeneratePerturbed(shaderInterface: shaderInterface));
            }
            UpdateCapture(echo: echo, manifest: manifest, width: width);
        }
        files["canary.json"] = Serialize(node: manifest);

        return files;
    }
    private static void UpdateCapture(JsonObject manifest, string echo, uint width) {
        var count = 0;

        foreach (var leg in ((string[])["positive", "discriminating"])) {
            foreach (var observation in manifest[leg]!["expect"]!.AsArray()) {
                if ((observation!["type"]!.GetValue<string>() != "imageRegion") ||
                    (observation["capture"]!.GetValue<string>() != $"{echo}.png")) {
                    continue;
                }
                observation["extent"] = new JsonArray(items: [JsonValue.Create(value: width), JsonValue.Create(value: 1u)]);
                if (observation["region"]![2]!.GetValue<double>() < 1) {
                    observation["region"]![2] = ((width - 1d) / width);
                }
                count++;
            }
        }
        if (count != 3) {
            throw new InvalidDataException(message: $"{echo}: expected the positive, negative and preceding-member capture observations; found {count}.");
        }
    }

    public static int Run(string directory, bool check) {
        try {
            var files = Plan(directory: directory);
            var matched = true;

            foreach (var (path, text) in files) {
                matched &= CliGeneratedFile.WriteOrCheck(check: check, detail: "", relativePath: path,
                    repositoryRoot: directory, source: "the interface-echo model", text: text,
                    verb: $"shaders interface \"{CliPaths.ToDisplay(fullPath: directory)}\" --echo-fixtures --write");
            }

            return (matched ? CliExit.Success : CliExit.Failed);
        } catch (Exception exception) when ((exception is IOException or JsonException or InvalidOperationException or ArgumentException or ShaderPipelineCompilationException)) {
            return CliExit.Refuse(verb: "shaders interface", what: CliPaths.ToDisplay(fullPath: directory), why: exception.Message.ReplaceLineEndings(replacementText: " "));
        }
    }
}
