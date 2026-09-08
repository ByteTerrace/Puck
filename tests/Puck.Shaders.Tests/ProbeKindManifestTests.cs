using System.Text.Json;

namespace Puck.Shaders.Tests;

public sealed class ProbeKindManifestTests {
    private static string IrBlobManifestPath =>
        Path.Combine(path1: ProbesDirectory, path2: "ir-blob.puck.probe.json");
    private static string ProbesDirectory =>
        Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Probes");

    [Fact]
    public void Ir_blob_manifest_loads_with_its_channels_and_kernel_source() {
        var manifest = ProbeKindManifest.Load(manifestPath: IrBlobManifestPath);

        Assert.Equal(expected: "ir-blob", actual: manifest.Name);
        Assert.Equal(expected: ProbeKindClass.Kernel, actual: manifest.Class);
        Assert.Equal(expected: 0, actual: manifest.TriggerSocket);
        Assert.Single(collection: manifest.Inputs);
        Assert.Equal(expected: "lit", actual: manifest.Inputs[0].Name);
        Assert.Equal(expected: ProbeSocketClass.Frame, actual: manifest.Inputs[0].Class);
        Assert.False(condition: manifest.Inputs[0].Optional);
        Assert.Null(@object: manifest.Output);
        Assert.Equal(expected: ["x", "y", "coverage", "luminance"], actual: manifest.Channels.Select(selector: channel => channel.Name).ToArray());
        Assert.Equal(expected: Path.GetDirectoryName(path: IrBlobManifestPath), actual: manifest.Directory);

        var kernel = Assert.IsType<ProbeKindKernel>(@object: manifest.Kernel);

        Assert.Equal(expected: "ir-blob.hlsl", actual: kernel.Source);
        Assert.Equal(expected: "accumulate", actual: kernel.Accumulate);
        Assert.Equal(expected: "finalize", actual: kernel.Finalize);
        Assert.True(condition: File.Exists(path: Path.Combine(path1: manifest.Directory, path2: kernel.Source)));
    }
    [Fact]
    public void Absent_config_binds_every_default() {
        var manifest = ProbeKindManifest.Load(manifestPath: IrBlobManifestPath);

        Assert.True(condition: manifest.TryBindConfig(config: null, reason: out _, values: out var values));
        Assert.Equal(expected: 0.5f, actual: BitConverter.UInt32BitsToSingle(value: values["threshold"].ComponentBits(index: 0)));
        Assert.Equal(expected: 0.02f, actual: BitConverter.UInt32BitsToSingle(value: values["minCoverage"].ComponentBits(index: 0)));
    }
    [Fact]
    public void Authored_config_overrides_and_refuses_by_field() {
        var manifest = ProbeKindManifest.Load(manifestPath: IrBlobManifestPath);

        Assert.True(condition: manifest.TryBindConfig(config: Parse(json: """{ "threshold": 0.6, "minCoverage": 0.05 }"""), values: out var values, reason: out _));
        Assert.Equal(expected: 0.6f, actual: BitConverter.UInt32BitsToSingle(value: values["threshold"].ComponentBits(index: 0)));

        Assert.False(condition: manifest.TryBindConfig(config: Parse(json: """{ "threshold": 1.5 }"""), values: out _, reason: out var reason));
        Assert.Equal(actual: reason, expected: "'threshold' must be a number in [0, 1].");
        Assert.False(condition: manifest.TryBindConfig(config: Parse(json: """{ "gain": 1 }"""), values: out _, reason: out reason));
        Assert.Equal(actual: reason, expected: "'gain' is not a config field of 'ir-blob'.");
    }
    [Fact]
    public void Config_schema_emits_types_ranges_and_defaults() {
        var manifest = ProbeKindManifest.Load(manifestPath: IrBlobManifestPath);
        var schema = manifest.ConfigJsonSchema();
        var properties = schema["properties"]!.AsObject();

        Assert.False(condition: schema["additionalProperties"]!.GetValue<bool>());
        Assert.Null(@object: schema["required"]);
        Assert.Equal(expected: "number", actual: properties["threshold"]!["type"]!.GetValue<string>());
        Assert.Equal(expected: 0d, actual: properties["threshold"]!["minimum"]!.GetValue<double>());
        Assert.Equal(expected: 1d, actual: properties["threshold"]!["maximum"]!.GetValue<double>());
        Assert.Equal(expected: 0.5, actual: properties["threshold"]!["default"]!.GetValue<double>());
    }
    [Fact]
    public void Constants_block_packs_bound_config_in_declaration_order() {
        var manifest = ProbeKindManifest.Load(manifestPath: IrBlobManifestPath);
        var values = manifest.BindConfig(config: Parse(json: """{ "threshold": 0.75, "minCoverage": 0.1 }"""));
        var block = manifest.ConstantsBlock(values: values).ToArray();

        // Two floats occupy eight bytes; the block pads to the 16-byte granule a D3D11 constant buffer requires.
        Assert.Equal(expected: ProbeKindManifest.ConstantsBlockAlignment, actual: block.Length);
        Assert.Equal(expected: 0.75f, actual: BitConverter.ToSingle(startIndex: 0, value: block));
        Assert.Equal(expected: 0.1f, actual: BitConverter.ToSingle(startIndex: 4, value: block));
        Assert.All(collection: block[8..], action: static padding => Assert.Equal(actual: padding, expected: ((byte)0)));
    }
    [Fact]
    public void Wrong_schema_tag_refuses() {
        var scratch = WriteScratchManifest(mutate: node => node["$schema"] = "puck.shader.v1");

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: "expected 'puck.probe.v1'", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Manifest_named_unlike_its_file_refuses() {
        var scratch = CopySenses();

        try {
            var renamed = Path.Combine(path1: scratch.FullName, path2: "blob.puck.probe.json");

            File.Move(sourceFileName: Path.Combine(path1: scratch.FullName, path2: "ir-blob.puck.probe.json"), destFileName: renamed);

            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: renamed));

            Assert.Contains(expectedSubstring: "ir-blob.puck.probe.json", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            scratch.Delete(recursive: true);
        }
    }
    [Fact]
    public void Too_many_channels_refuses() {
        var scratch = WriteScratchManifest(mutate: node => {
            var channels = node["channels"]!.AsArray();

            for (var index = 0; (index < (ProbeKindManifest.MaxChannels + 1)); index++) {
                channels.Add(item: new System.Text.Json.Nodes.JsonObject {
                    ["name"] = $"extra{index}",
                    ["min"] = -1,
                    ["max"] = 1,
                    ["neutral"] = 0,
                });
            }
        });

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: $"at most {ProbeKindManifest.MaxChannels}", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Duplicate_channel_name_refuses() {
        var scratch = WriteScratchManifest(mutate: node => node["channels"]![1]!["name"] = "x");

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: "declares channel 'x' twice", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Neutral_outside_channel_range_refuses() {
        var scratch = WriteScratchManifest(mutate: node => node["channels"]![0]!["neutral"] = 2);

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: "neutral 2 outside [-1, 1]", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Kernel_class_without_a_kernel_block_refuses() {
        var scratch = WriteScratchManifest(mutate: node => node.Remove(propertyName: "kernel"));

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: "must declare a 'kernel' block", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Kernel_source_that_does_not_exist_refuses() {
        var scratch = WriteScratchManifest(mutate: node => node["kernel"]!["source"] = "no-such-file.hlsl");

        try {
            Assert.Throws<FileNotFoundException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Unrecognized_class_refuses_at_deserialization() {
        var scratch = WriteScratchManifest(mutate: node => node["class"] = "sniff");

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: "Unrecognized probe kind class 'sniff'", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Duplicate_socket_name_refuses() {
        var scratch = WriteMultiSocketManifest(mutate: node => node["inputs"]![2]!["name"] = "color");

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: "declares socket 'color' twice", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Trigger_naming_no_socket_refuses() {
        var scratch = WriteMultiSocketManifest(mutate: node => node["trigger"] = "no-such-socket");

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: "trigger 'no-such-socket' does not name a declared socket", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void Output_of_naming_no_socket_refuses() {
        var scratch = WriteMultiSocketManifest(mutate: node => node["output"]!["of"] = "no-such-socket");

        try {
            var exception = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindManifest.Load(manifestPath: scratch));

            Assert.Contains(expectedSubstring: "output.of 'no-such-socket' does not name a declared socket", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void StrobePair_and_optional_sockets_round_trip() {
        var scratch = WriteMultiSocketManifest(mutate: static _ => { });

        try {
            var manifest = ProbeKindManifest.Load(manifestPath: scratch);

            Assert.Equal(expected: "strobe", actual: manifest.Inputs[1].Name);
            Assert.Equal(expected: ProbeSocketClass.StrobePair, actual: manifest.Inputs[1].Class);
            Assert.False(condition: manifest.Inputs[1].Optional);
            Assert.Equal(expected: "painting", actual: manifest.Inputs[2].Name);
            Assert.Equal(expected: ProbeSocketClass.Frame, actual: manifest.Inputs[2].Class);
            Assert.True(condition: manifest.Inputs[2].Optional);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: scratch)!, recursive: true);
        }
    }
    [Fact]
    public void TriggerSocket_resolves_the_authored_name_or_defaults_to_the_first_socket() {
        var authored = WriteMultiSocketManifest(mutate: node => node["trigger"] = "strobe");

        try {
            Assert.Equal(expected: 1, actual: ProbeKindManifest.Load(manifestPath: authored).TriggerSocket);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: authored)!, recursive: true);
        }

        var defaulted = WriteMultiSocketManifest(mutate: node => node.Remove(propertyName: "trigger"));

        try {
            Assert.Equal(expected: 0, actual: ProbeKindManifest.Load(manifestPath: defaulted).TriggerSocket);
        } finally {
            Directory.Delete(path: Path.GetDirectoryName(path: defaulted)!, recursive: true);
        }
    }
    [Fact]
    public void Catalog_finds_shipped_kinds_and_refuses_a_duplicate_id() {
        var catalog = ProbeKindCatalog.Scan(rootDirectory: ProbesDirectory);

        Assert.Contains(expected: "faerie", collection: catalog.Ids);
        Assert.Contains(expected: "ir-blob", collection: catalog.Ids);
        Assert.True(condition: catalog.Contains(id: "ir-blob"));
        Assert.False(condition: catalog.Contains(id: "no-such-kind"));
        Assert.Equal(expected: "ir-blob", actual: catalog.Load(id: "ir-blob").Name);
        Assert.Throws<KeyNotFoundException>(testCode: () => catalog.Load(id: "no-such-kind"));
        Assert.Empty(collection: ProbeKindCatalog.Scan(rootDirectory: Path.Combine(path1: ProbesDirectory, path2: "no-such-directory")).Ids);

        var scratch = CopySenses();

        Directory.CreateDirectory(path: Path.Combine(path1: scratch.FullName, path2: "nested"));
        File.Copy(sourceFileName: Path.Combine(path1: scratch.FullName, path2: "ir-blob.puck.probe.json"), destFileName: Path.Combine(path1: scratch.FullName, path2: "nested", path3: "ir-blob.puck.probe.json"));

        try {
            Assert.Throws<InvalidDataException>(testCode: () => ProbeKindCatalog.Scan(rootDirectory: scratch.FullName));
        } finally {
            scratch.Delete(recursive: true);
        }
    }

    private static DirectoryInfo CopySenses() {
        var scratch = Directory.CreateTempSubdirectory(prefix: "puck-sense-manifest-");

        foreach (var file in Directory.EnumerateFiles(path: ProbesDirectory, searchPattern: "*", searchOption: SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(relativeTo: ProbesDirectory, path: file);
            var destination = Path.Combine(path1: scratch.FullName, path2: relative);

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.Copy(destFileName: destination, sourceFileName: file);
        }

        return scratch;
    }
    private static JsonElement Parse(string json) => JsonDocument.Parse(json: json).RootElement.Clone();
    private static string WriteScratchManifest(Action<System.Text.Json.Nodes.JsonObject> mutate) {
        var scratch = CopySenses();
        var manifestPath = Path.Combine(path1: scratch.FullName, path2: "ir-blob.puck.probe.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(json: File.ReadAllText(path: manifestPath))!.AsObject();

        mutate(obj: node);
        File.WriteAllText(path: manifestPath, contents: node.ToJsonString());

        return manifestPath;
    }
    private static string WriteMultiSocketManifest(Action<System.Text.Json.Nodes.JsonObject> mutate) {
        var scratch = Directory.CreateTempSubdirectory(prefix: "puck-sense-manifest-");
        var manifestPath = Path.Combine(path1: scratch.FullName, path2: "multi-socket.puck.probe.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(json: MultiSocketManifestJson)!.AsObject();

        mutate(obj: node);
        File.WriteAllText(path: manifestPath, contents: node.ToJsonString());

        return manifestPath;
    }

    // A MODEL-class kind (no kernel block, so no HLSL source needs to exist on disk) exercising every socket
    // shape: a required frame socket, a required strobePair socket, and an optional frame socket.
    private const string MultiSocketManifestJson = """
    {
      "$schema": "puck.probe.v1",
      "name": "multi-socket",
      "class": "model",
      "inputs": [
        { "name": "color", "class": "frame" },
        { "name": "strobe", "class": "strobePair" },
        { "name": "painting", "class": "frame", "optional": true }
      ],
      "trigger": "color",
      "output": { "of": "color" },
      "channels": [
        { "name": "x", "min": -1, "max": 1, "neutral": 0 }
      ]
    }
    """;
}
