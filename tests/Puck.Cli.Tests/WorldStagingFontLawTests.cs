using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Testing;
using Puck.World;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Staged fonts preserve portable references, distinct bytes and authored pins across drive boundaries.</summary>
public sealed class WorldStagingFontLawTests {
    [Fact]
    public void A_staged_world_may_share_its_name_with_a_source_relative_basis() {
        using var files = new TemporaryDirectory(prefix: "puck-staging-identity-");

        files.WriteText(name: "source/base.world.json", text: """
            {"title":"inherited title"}
            """);
        var world = new JsonObject { ["schema"] = WorldDefinition.SchemaVersion, ["basis"] = "base" };

        Assert.True(condition: WorldStaging.TryWrite(catalog: CliWorldVocabulary.EnsureInstalled(), world: world, name: "base", sourceDirectory: files.PathOf(name: "source"),
            directory: files.PathOf(name: "staged"), path: out var staged, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: "inherited title", actual: JsonNode.Parse(utf8Json: File.ReadAllBytes(path: staged))!["title"]!.GetValue<string>());
    }
    [InlineData("basis")]
    [InlineData("import")]
    [InlineData("alias")]
    [Theory]
    public void Inherited_fonts_remain_portable_when_composition_crosses_drives(string mode) {
        using var files = new TemporaryDirectory(prefix: "puck-inherited-font-");
        var source = Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: ".tmp", path3: $"staging-fonts-{Guid.NewGuid():N}");

        if (string.Equals(a: Path.GetPathRoot(path: source), b: Path.GetPathRoot(path: files.RootPath), comparisonType: StringComparison.OrdinalIgnoreCase)) {
            Assert.Skip(reason: "The checkout and temporary directory must sit on different drives for this relocation leg.");
        }
        Directory.CreateDirectory(path: source);
        try {
            files.WriteText(name: "font.ttf", text: "font bytes");
            files.WriteText(name: "basis.world.json", text: """
                {"text":{"defaultFont":"body","fonts":[{"name":"body","source":"font.ttf","hash":"sha256-64/0000000000000000"}]}}
                """);
            var basis = files.PathOf(name: "basis").Replace(newChar: '/', oldChar: '\\');
            var world = new JsonObject { ["schema"] = WorldDefinition.SchemaVersion };

            if (mode == "basis") {
                world["basis"] = basis;
            } else {
                var import = new JsonObject { ["document"] = basis };

                if (mode == "alias") {
                    import["as"] = "library";
                }
                world["imports"] = new JsonArray { import };
            }
            Assert.True(condition: WorldStaging.TryWrite(catalog: CliWorldVocabulary.EnsureInstalled(),
                world: world, name: "entry", sourceDirectory: source, directory: files.PathOf(name: "staged"),
                path: out var staged, reason: out var reason
            ), userMessage: reason);
            var font = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: staged))!["text"]!["fonts"]![0]!;
            var path = font["source"]!.GetValue<string>();

            Assert.Equal(expected: "body", actual: font["name"]!.GetValue<string>());
            Assert.False(condition: Path.IsPathRooted(path: path));
            Assert.Equal(expected: "font bytes", actual: File.ReadAllText(path: WorldDocumentPaths.Resolve(
                documentDirectory: Path.GetDirectoryName(path: staged), path: path)));
        } finally {
            Directory.Delete(path: source, recursive: true);
        }
    }
    [Fact]
    public void Winning_origins_follow_keyed_fields_and_tombstones_after_an_ordinary_cached_composition() {
        using var files = new TemporaryDirectory(prefix: "puck-font-origins-");

        files.WriteText(name: "base/keep.ttf", text: "inherited");
        files.WriteText(name: "root.ttf", text: "replacement");
        var invalid = files.PathOf(name: "missing.ttf").Replace(newChar: '/', oldChar: '\\');
        var basis = new JsonObject {
            ["text"] = new JsonObject {
                ["fonts"] = new JsonArray {
                    new JsonObject { ["name"] = "keep", ["source"] = "keep.ttf", ["hash"] = "sha256-64/0000000000000000" },
                    new JsonObject { ["name"] = "replace", ["source"] = invalid, ["hash"] = "sha256-64/0000000000000000" },
                    new JsonObject { ["name"] = "drop", ["source"] = invalid, ["hash"] = "sha256-64/0000000000000000" },
                },
            },
        };

        files.WriteText(name: "base/basis.world.json", text: basis.ToJsonString());
        var world = new JsonObject {
            ["schema"] = WorldDefinition.SchemaVersion,
            ["basis"] = "base/basis",
            ["text"] = new JsonObject {
                ["defaultFont"] = "keep",
                ["fonts"] = new JsonArray {
                    new JsonObject { ["name"] = "keep", ["hash"] = "sha256-64/1111111111111111" },
                    new JsonObject { ["name"] = "replace", ["source"] = "root.ttf" },
                    new JsonObject { ["name"] = "drop", ["$drop"] = true },
                },
            },
        };
        var rootPath = files.PathOf(name: "entry.world.json").Replace(newChar: '/', oldChar: '\\');
        var bytes = CanonicalJsonDocument.Serialize(node: world);

        Assert.True(condition: PuckDocumentComposer.TryComposeWorldDocument(rootResolvedPath: rootPath, rootBytes: bytes,
            composed: out var cached, chainBytes: out _, reason: out var reason), userMessage: reason);
        var origins = new WorldDocumentOrigins();

        Assert.True(condition: PuckDocumentComposer.TryComposeWorldDocument(rootResolvedPath: rootPath, rootBytes: bytes,
            composed: out var tracked, chainBytes: out _, reason: out reason, origins: origins), userMessage: reason);
        Assert.True(condition: JsonNode.DeepEquals(node1: cached, node2: tracked));
        var fonts = tracked!["text"]!["fonts"]!.AsArray();

        Assert.Equal(expected: 2, actual: fonts.Count);
        Assert.True(condition: origins.TryGetOrigin(value: fonts[0]!["source"], origin: out var kept));
        Assert.Equal(expected: files.PathOf(name: "base/basis.world.json").Replace(newChar: '/', oldChar: '\\'), actual: kept.DocumentPath);
        Assert.Equal(expected: "/text/fonts/0/source", actual: kept.JsonPointer);
        Assert.Equal(expected: "\"keep.ttf\"", actual: kept.Json);
        Assert.True(condition: origins.TryGetOrigin(value: fonts[0]!["hash"], origin: out var hash));
        Assert.Equal(expected: rootPath, actual: hash.DocumentPath);
        Assert.True(condition: origins.TryGetOrigin(value: fonts[1]!["source"], origin: out var replaced));
        Assert.Equal(expected: rootPath, actual: replaced.DocumentPath);
        Assert.Equal(expected: "\"root.ttf\"", actual: replaced.Json);
        Assert.True(condition: WorldStaging.TryWrite(catalog: CliWorldVocabulary.EnsureInstalled(), world: world, name: "entry", sourceDirectory: files.RootPath,
            directory: files.PathOf(name: "staged"), path: out var staged, reason: out reason), userMessage: reason);
        var stagedFonts = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: staged))!["text"]!["fonts"]!.AsArray();

        Assert.Equal(expected: new[] { "inherited", "replacement" }, actual: stagedFonts.Select(selector: row => File.ReadAllText(path: WorldDocumentPaths.Resolve(
            documentDirectory: Path.GetDirectoryName(path: staged), path: row!["source"]!.GetValue<string>()))));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void Staged_fonts_keep_their_bytes_and_pins_without_filename_collisions(bool crossDrive) {
        using var files = new TemporaryDirectory(prefix: "puck-staging-fonts-");
        var source = (crossDrive
            ? Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: ".tmp", path3: $"staging-fonts-{Guid.NewGuid():N}")
            : files.PathOf(name: "source"));
        var target = files.PathOf(name: "staged");

        if (crossDrive && string.Equals(a: Path.GetPathRoot(path: source), b: Path.GetPathRoot(path: target), comparisonType: StringComparison.OrdinalIgnoreCase)) {
            Assert.Skip(reason: "The checkout and temporary directory must sit on different drives for this relocation leg.");
        }

        Directory.CreateDirectory(path: source);
        try {
            var fonts = new JsonArray();

            foreach (var name in new[] { "first", "second" }) {
                var folder = Directory.CreateDirectory(path: Path.Combine(path1: source, path2: name)).FullName;

                File.WriteAllText(path: Path.Combine(path1: folder, path2: "font.ttf"), contents: name);
                fonts.Add(item: new JsonObject {
                    ["name"] = name,
                    ["source"] = $"{name}/font.ttf",
                    ["hash"] = "sha256-64/0000000000000000",
                });
            }
            var absolute = Path.Combine(path1: source, path2: "first", path3: "font.ttf").Replace(newChar: '/', oldChar: '\\');

            fonts.Add(item: new JsonObject {
                ["name"] = "invalid-absolute",
                ["source"] = absolute,
                ["hash"] = "sha256-64/0000000000000000",
            });
            var invalidPaths = new[] { "./first/font.ttf", "first\\font.ttf", "first//font.ttf", "C:font.ttf" };

            for (var index = 0; (index < invalidPaths.Length); index++) {
                fonts.Add(item: new JsonObject {
                    ["name"] = $"invalid-{index}",
                    ["source"] = invalidPaths[index],
                    ["hash"] = "sha256-64/0000000000000000",
                });
            }
            var world = new JsonObject {
                ["schema"] = WorldDefinition.SchemaVersion,
                ["text"] = new JsonObject { ["defaultFont"] = "first", ["fonts"] = fonts },
            };

            Assert.True(condition: WorldStaging.TryWrite(catalog: CliWorldVocabulary.EnsureInstalled(),
                directory: target, name: "entry", path: out var staged, reason: out var reason,
                sourceDirectory: source, world: world
            ), userMessage: reason);
            var written = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: staged))!["text"]!["fonts"]!.AsArray();
            var paths = new List<string>();

            foreach (var row in written) {
                var path = row!["source"]!.GetValue<string>();
                var name = row["name"]!.GetValue<string>();

                if (name == "invalid-absolute") {
                    Assert.Equal(actual: path, expected: absolute);
                    continue;
                }
                if (name.StartsWith(comparisonType: StringComparison.Ordinal, value: "invalid-")) {
                    Assert.Contains(collection: invalidPaths, expected: path);
                    Assert.False(condition: WorldDocumentPaths.IsPortableRelativeFilePath(path: path));
                    continue;
                }
                Assert.False(condition: Path.IsPathRooted(path: path));
                Assert.DoesNotContain(actualString: path, expectedSubstring: "\\");
                Assert.Equal(expected: "sha256-64/0000000000000000", actual: row["hash"]!.GetValue<string>());
                Assert.Equal(expected: name, actual: File.ReadAllText(path: Path.Combine(path1: target, path2: path)));
                if (!crossDrive) {
                    Assert.Equal(actual: path, expected: $"../source/{name}/font.ttf");
                }
                paths.Add(item: path);
            }
            Assert.Equal(expected: 2, actual: paths.Distinct(comparer: StringComparer.Ordinal).Count());
            Assert.True(condition: WorldStaging.TryStageComposition(catalog: CliWorldVocabulary.EnsureInstalled(),
                path: Path.Combine(path1: source, path2: "world.puck"),
                worlds: [new WorldCompiledWorld(Entry: true, Json: CanonicalJsonDocument.Serialize(node: new JsonObject { ["schema"] = WorldDefinition.SchemaVersion }), Name: "empty")],
                entry: null, directory: target, entryPath: out _, entryName: out _, reason: out reason
            ), userMessage: reason);
            foreach (var name in new[] { "first", "second" }) {
                Assert.Equal(expected: name, actual: File.ReadAllText(path: Path.Combine(path1: source, path2: name, path3: "font.ttf")));
            }
            if (crossDrive) {
                Assert.All(collection: paths, action: path => Assert.False(condition: File.Exists(path: Path.Combine(path1: target, path2: path))));
            }
        } finally {
            Directory.Delete(path: source, recursive: true);
        }
    }
}
