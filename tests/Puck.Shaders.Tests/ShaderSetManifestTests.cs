using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

public sealed class ShaderSetManifestTests {
    private static string FilmGrainManifestPath =>
        Path.Combine(
            path1: ShaderDirectory,
            path2: "Sdf",
            path3: "sdf-film-grain.puck.shader.json"
        );
    private static string ShaderDirectory =>
        Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Shaders"
        );

    private static DirectoryInfo CopyShaders() {
        var scratch = Directory.CreateTempSubdirectory(prefix: "puck-shader-manifest-");

        foreach (var file in Directory.EnumerateFiles(
            path: ShaderDirectory,
            searchPattern: "*",
            searchOption: SearchOption.AllDirectories
        )) {
            var relative = Path.GetRelativePath(
                relativeTo: ShaderDirectory,
                path: file
            );
            var destination = Path.Combine(
                path1: scratch.FullName,
                path2: relative
            );

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.Copy(
                destFileName: destination,
                sourceFileName: file
            );
        }

        return scratch;
    }
    private static JsonElement Parse(string json) => JsonDocument.Parse(json: json).RootElement.Clone();

    [Fact]
    public void Absent_config_binds_every_default() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);

        Assert.True(condition: manifest.TryBindConfig(
            config: null,
            reason: out _,
            values: out var values
        ));
        Assert.Equal(
            expected: 0.05f,
            actual: BitConverter.UInt32BitsToSingle(value: values["intensity"].ComponentBits(index: 0))
        );
        Assert.Equal(
            expected: 1f,
            actual: BitConverter.UInt32BitsToSingle(value: values["size"].ComponentBits(index: 0))
        );
        Assert.Equal(
            expected: 0u,
            actual: values["seed"].ComponentBits(index: 0)
        );
        Assert.Equal(
            expected: 24u,
            actual: values["flickerHz"].ComponentBits(index: 0)
        );
    }
    [Fact]
    public void Authored_config_overrides_and_refuses_by_field() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);

        Assert.True(condition: manifest.TryBindConfig(
            config: Parse(json: """{ "intensity": 0.08, "size": 1.5, "seed": 7, "flickerHz": 1 }"""),
            values: out var values,
            reason: out _
        ));
        Assert.Equal(
            expected: 0.08f,
            actual: BitConverter.UInt32BitsToSingle(value: values["intensity"].ComponentBits(index: 0))
        );
        Assert.Equal(
            expected: 7u,
            actual: values["seed"].ComponentBits(index: 0)
        );

        Assert.False(condition: manifest.TryBindConfig(
            config: Parse(json: """{ "intensity": 1.5 }"""),
            values: out _,
            reason: out var reason
        ));
        Assert.Equal(
            actual: reason,
            expected: "'intensity' must be a number in [0, 1]."
        );
        Assert.False(condition: manifest.TryBindConfig(
            config: Parse(json: """{ "size": 0.5 }"""),
            values: out _,
            reason: out reason
        ));
        Assert.Equal(
            actual: reason,
            expected: "'size' must be a number greater than or equal to 1."
        );
        Assert.False(condition: manifest.TryBindConfig(
            config: Parse(json: """{ "seed": -1 }"""),
            values: out _,
            reason: out reason
        ));
        Assert.Equal(
            actual: reason,
            expected: "'seed' must be a non-negative integer."
        );
        Assert.False(condition: manifest.TryBindConfig(
            config: Parse(json: """{ "flickerHz": 0 }"""),
            values: out _,
            reason: out reason
        ));
        Assert.Equal(
            actual: reason,
            expected: "'flickerHz' must be a non-negative integer greater than or equal to 1."
        );
        Assert.False(condition: manifest.TryBindConfig(
            config: Parse(json: """{ "grain": 1 }"""),
            values: out _,
            reason: out reason
        ));
        Assert.Equal(
            actual: reason,
            expected: "'grain' is not a config field of 'sdf-film-grain'."
        );
        Assert.False(condition: manifest.TryBindConfig(
            config: Parse(json: """[1]"""),
            values: out _,
            reason: out reason
        ));
        Assert.Equal(
            actual: reason,
            expected: "config must be an object."
        );
    }
    [Fact]
    public void Catalog_finds_shipped_sets_across_subdirectories_and_refuses_a_duplicate_id() {
        var catalog = ShaderSetCatalog.Scan(rootDirectory: ShaderDirectory);

        Assert.Equal(
            expected: ["sdf-film-grain"],
            actual: catalog.Ids
        );
        Assert.True(condition: catalog.Contains(id: "sdf-film-grain"));
        Assert.False(condition: catalog.Contains(id: "film-grain"));
        Assert.Equal(
            expected: "sdf-film-grain",
            actual: catalog.Load(id: "sdf-film-grain").Name
        );
        Assert.Throws<KeyNotFoundException>(testCode: () => catalog.Load(id: "film-grain"));
        Assert.Empty(collection: ShaderSetCatalog.Scan(rootDirectory: Path.Combine(
            path1: ShaderDirectory,
            path2: "no-such-directory"
        )).Ids);

        var scratch = CopyShaders();

        try {
            File.Copy(
                sourceFileName: Path.Combine(
                    path1: scratch.FullName,
                    path2: "Sdf",
                    path3: "sdf-film-grain.puck.shader.json"
                ),
                destFileName: Path.Combine(
                    path1: scratch.FullName,
                    path2: "sdf-film-grain.puck.shader.json"
                )
            );
            Assert.Throws<InvalidDataException>(testCode: () => ShaderSetCatalog.Scan(rootDirectory: scratch.FullName));
        } finally {
            scratch.Delete(recursive: true);
        }
    }
    [Fact]
    public void Config_schema_emits_types_ranges_and_defaults() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);
        var schema = manifest.ConfigJsonSchema();
        var properties = schema["properties"]!.AsObject();

        Assert.False(condition: schema["additionalProperties"]!.GetValue<bool>());
        Assert.Null(@object: schema["required"]);
        Assert.Equal(
            expected: "number",
            actual: properties["intensity"]!["type"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: 0d,
            actual: properties["intensity"]!["minimum"]!.GetValue<double>()
        );
        Assert.Equal(
            expected: 1d,
            actual: properties["intensity"]!["maximum"]!.GetValue<double>()
        );
        Assert.Equal(
            expected: 0.05,
            actual: properties["intensity"]!["default"]!.GetValue<double>()
        );
        Assert.Equal(
            expected: "integer",
            actual: properties["seed"]!["type"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: ((double)uint.MaxValue),
            actual: properties["seed"]!["maximum"]!.GetValue<double>()
        );

        Assert.Equal(
            expected: 1d,
            actual: properties["flickerHz"]!["minimum"]!.GetValue<double>()
        );
    }
    [Fact]
    public void Film_grain_manifest_loads_against_its_shipped_bytecode() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);

        Assert.Equal(
            expected: "sdf-film-grain",
            actual: manifest.Name
        );
        Assert.Equal(
            expected: "fullscreen.vert",
            actual: manifest.Stages.Vertex
        );
        Assert.Equal(
            expected: "sdf-film-grain.frag",
            actual: manifest.Stages.Fragment
        );
        Assert.True(condition: manifest.IsGraphics);
        Assert.Equal(
            actual: manifest.Bindings,
            expected: [
                new ShaderSetManifestBinding(
                    Kind: GpuBindingKind.SampledImage,
                    Name: "source"
                ),
                new ShaderSetManifestBinding(
                    Kind: GpuBindingKind.Sampler,
                    Name: "sourceSampler"
                ),
            ]
        );
        Assert.Equal(
            expected: Path.GetDirectoryName(path: FilmGrainManifestPath),
            actual: manifest.Directory
        );

        var layout = manifest.FrameLayout;

        // The pass block holds the extent, then the config in ordinal name order, and rounds up to a row.
        Assert.Equal(
            expected: "sdf-film-grain",
            actual: layout.Interface.Name
        );
        Assert.Equal(
            expected: 32u,
            actual: layout.SizeBytes
        );
        Assert.Equal(
            expected: [8u, 12u, 16u, 20u],
            actual: layout.Slots.Select(selector: slot => slot.Offset).ToArray()
        );
        Assert.Equal(
            expected: ["flickerHz", "intensity", "seed", "size"],
            actual: layout.Slots.Select(selector: slot => slot.Name).ToArray()
        );
    }
    [Fact]
    public void Malformed_bytecode_and_missing_spirv_refuse_by_stage() {
        var scratch = CopyShaders();

        try {
            var manifestPath = Path.Combine(
                path1: scratch.FullName,
                path2: "Sdf",
                path3: "sdf-film-grain.puck.shader.json"
            );
            var fragmentSpirv = Path.Combine(
                path1: scratch.FullName,
                path2: "Sdf",
                path3: "sdf-film-grain.frag.spv"
            );

            File.WriteAllBytes(
                bytes: [1, 2, 3, 4, 5, 6, 7, 8],
                path: fragmentSpirv
            );

            var malformed = Assert.Throws<InvalidDataException>(testCode: () => ShaderSetManifest.Load(manifestPath: manifestPath));

            Assert.Contains(
                expectedSubstring: "fragment bytecode",
                actualString: malformed.Message,
                comparisonType: StringComparison.Ordinal
            );

            File.Delete(path: fragmentSpirv);

            var missing = Assert.Throws<FileNotFoundException>(testCode: () => ShaderSetManifest.Load(manifestPath: manifestPath));

            Assert.Contains(
                expectedSubstring: "fragment stage",
                actualString: missing.Message,
                comparisonType: StringComparison.Ordinal
            );
        } finally {
            scratch.Delete(recursive: true);
        }
    }
    [Fact]
    public void Manifest_named_unlike_its_file_refuses() {
        var scratch = CopyShaders();

        try {
            var renamed = Path.Combine(
                path1: scratch.FullName,
                path2: "Sdf",
                path3: "grain.puck.shader.json"
            );

            File.Move(
                sourceFileName: Path.Combine(
                    path1: scratch.FullName,
                    path2: "Sdf",
                    path3: "sdf-film-grain.puck.shader.json"
                ),
                destFileName: renamed
            );

            var exception = Assert.Throws<InvalidDataException>(testCode: () => ShaderSetManifest.Load(manifestPath: renamed));

            Assert.Contains(
                expectedSubstring: "sdf-film-grain.puck.shader.json",
                actualString: exception.Message,
                comparisonType: StringComparison.Ordinal
            );
        } finally {
            scratch.Delete(recursive: true);
        }
    }
    [Fact]
    public void A_config_field_named_for_a_frame_member_refuses_at_load() {
        var scratch = CopyShaders();

        try {
            var manifestPath = Path.Combine(
                path1: scratch.FullName,
                path2: "Sdf",
                path3: "sdf-film-grain.puck.shader.json"
            );
            var node = JsonNode.Parse(json: File.ReadAllText(path: manifestPath))!.AsObject();

            node["config"]!["time"] = JsonNode.Parse(json: """{ "type": "float", "default": 0 }""");
            File.WriteAllText(
                path: manifestPath,
                contents: node.ToJsonString()
            );

            var exception = Assert.Throws<InvalidDataException>(testCode: () => ShaderSetManifest.Load(manifestPath: manifestPath));

            Assert.Contains(
                expectedSubstring: "member 'time': the name is declared twice",
                actualString: exception.Message,
                comparisonType: StringComparison.Ordinal
            );
        } finally {
            scratch.Delete(recursive: true);
        }
    }
    [Fact]
    public void Required_fields_and_vectors_bind() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath) with {
            Config = new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) {
                ["b"] = new(
                    Default: Parse(json: "[1, 2]"),
                    Max: 10,
                    Min: 0,
                    Type: ShaderValueType.Float2
                ),
                ["f"] = new(Type: ShaderValueType.Float),
                ["i"] = new(
                    Default: Parse(json: "-5"),
                    Max: 10,
                    Min: -10,
                    Type: ShaderValueType.Int
                ),
            },
        };

        Assert.False(condition: manifest.TryBindConfig(
            config: null,
            reason: out var reason,
            values: out _
        ));
        Assert.Equal(
            actual: reason,
            expected: "'f' is required."
        );
        Assert.True(condition: manifest.TryBindConfig(
            config: Parse(json: """{ "f": 2, "b": [3, 4] }"""),
            values: out var values,
            reason: out _
        ));
        Assert.Equal(
            expected: 4f,
            actual: BitConverter.UInt32BitsToSingle(value: values["b"].ComponentBits(index: 1))
        );
        Assert.Equal(
            expected: -5,
            actual: ((int)values["i"].ComponentBits(index: 0))
        );
        Assert.False(condition: manifest.TryBindConfig(
            config: Parse(json: """{ "f": 2, "b": [3] }"""),
            values: out _,
            reason: out reason
        ));
        Assert.Equal(
            actual: reason,
            expected: "'b' must be an array of 2 numbers in [0, 10]."
        );

        Assert.Contains(
            expected: "f",
            collection: manifest.ConfigJsonSchema()["required"]!.AsArray().Select(selector: node => node!.GetValue<string>())
        );
    }
}
