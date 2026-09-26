using System.Text.Json;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>Laws for the film grain package's declaration (<see cref="RenderGraphPackageCatalog.SdfFilmGrain"/>): its
/// config binds every default and refuses a value by field, its schema emits types, ranges and defaults, its interface
/// lays the config out after the extent in ordinal name order under the name its checked-in declarations carry, its
/// stages ship for both backends, and the config binder refuses a required field and binds vectors.</summary>
public sealed class SdfFilmGrainPackageLawTests {
    private static RenderGraphPackage FilmGrain => (RenderGraphPackageCatalog.Engine.TryGet(
        id: RenderGraphPackageCatalog.SdfFilmGrain,
        package: out var package
    )
        ? package
        : throw new InvalidOperationException(message: "The engine catalog declares no film grain package."));

    private static bool Bind(JsonElement? config, out ShaderConfigValues values, out string reason, IReadOnlyDictionary<string, ShaderConfigField>? schema = null) => ShaderConfigBinding.TryBind(
        config: config,
        ownerName: RenderGraphPackageCatalog.SdfFilmGrain,
        reason: out reason,
        schema: (schema ?? FilmGrain.Config),
        values: out values
    );
    private static JsonElement Parse(string json) => JsonDocument.Parse(json: json).RootElement.Clone();

    [Fact]
    public void AbsentConfigBindsEveryDefault() {
        Assert.True(condition: Bind(
            config: null,
            reason: out _,
            values: out var values
        ));
        Assert.Equal(expected: 0.05f, actual: BitConverter.UInt32BitsToSingle(value: values["intensity"].ComponentBits(index: 0)));
        Assert.Equal(expected: 1f, actual: BitConverter.UInt32BitsToSingle(value: values["size"].ComponentBits(index: 0)));
        Assert.Equal(expected: 0u, actual: values["seed"].ComponentBits(index: 0));
        Assert.Equal(expected: 24u, actual: values["flickerHz"].ComponentBits(index: 0));
    }
    [Fact]
    public void AuthoredConfigOverridesAndRefusesByField() {
        Assert.True(condition: Bind(
            config: Parse(json: """{ "intensity": 0.08, "size": 1.5, "seed": 7, "flickerHz": 1 }"""),
            reason: out _,
            values: out var values
        ));
        Assert.Equal(expected: 0.08f, actual: BitConverter.UInt32BitsToSingle(value: values["intensity"].ComponentBits(index: 0)));
        Assert.Equal(expected: 7u, actual: values["seed"].ComponentBits(index: 0));

        foreach (var (config, expected) in ((ReadOnlySpan<(string, string)>)[
            ("""{ "intensity": 1.5 }""", "'intensity' must be a number in [0, 1]."),
            ("""{ "size": 0.5 }""", "'size' must be a number greater than or equal to 1."),
            ("""{ "seed": -1 }""", "'seed' must be a non-negative integer."),
            ("""{ "flickerHz": 0 }""", "'flickerHz' must be a non-negative integer greater than or equal to 1."),
            ("""{ "grain": 1 }""", "'grain' is not a config field of 'sdf.film-grain'."),
            ("""[1]""", "config must be an object."),
        ])) {
            Assert.False(condition: Bind(
                config: Parse(json: config),
                reason: out var reason,
                values: out _
            ));
            Assert.Equal(actual: reason, expected: expected);
        }
    }
    [Fact]
    public void ConfigSchemaEmitsTypesRangesAndDefaults() {
        var schema = ShaderConfigBinding.JsonSchema(
            description: FilmGrain.Summary,
            schema: FilmGrain.Config
        );
        var properties = schema["properties"]!.AsObject();

        Assert.False(condition: schema["additionalProperties"]!.GetValue<bool>());
        Assert.Null(@object: schema["required"]);
        Assert.Equal(expected: "number", actual: properties["intensity"]!["type"]!.GetValue<string>());
        Assert.Equal(expected: 0d, actual: properties["intensity"]!["minimum"]!.GetValue<double>());
        Assert.Equal(expected: 1d, actual: properties["intensity"]!["maximum"]!.GetValue<double>());
        Assert.Equal(expected: 0.05, actual: properties["intensity"]!["default"]!.GetValue<double>());
        Assert.Equal(expected: "integer", actual: properties["seed"]!["type"]!.GetValue<string>());
        Assert.Equal(expected: ((double)uint.MaxValue), actual: properties["seed"]!["maximum"]!.GetValue<double>());
        Assert.Equal(expected: 1d, actual: properties["flickerHz"]!["minimum"]!.GetValue<double>());
    }
    [Fact]
    public void TheInterfaceLaysTheConfigOutAfterTheExtentAndTheStagesShipForBothBackends() {
        var package = FilmGrain;
        var layout = ShaderPipelineParameterLayout.ForPackage(
            config: package.Config,
            members: package.Members,
            package: package.Id
        );

        // The interface keeps the name the fragment stage includes its declarations by.
        Assert.Equal(expected: "sdf-film-grain", actual: layout.Interface.Name);
        // The pass block holds the extent, then the config in ordinal name order, and rounds up to a row.
        Assert.Equal(expected: 32u, actual: layout.SizeBytes);
        Assert.Equal(expected: [8u, 12u, 16u, 20u], actual: layout.Slots.Select(selector: static slot => slot.Offset).ToArray());
        Assert.Equal(expected: ["flickerHz", "intensity", "seed", "size"], actual: layout.Slots.Select(selector: static slot => slot.Name).ToArray());
        Assert.True(condition: package.IsPostProcess);

        var stages = package.Stages!;

        foreach (var stem in ((string[])[stages.Vertex, stages.Fragment])) {
            foreach (var extension in ((string[])[".spv", ".dxil"])) {
                var path = Path.Combine(path1: AppContext.BaseDirectory, path2: stages.Directory, path3: (stem + extension));

                Assert.True(condition: File.Exists(path: path), userMessage: path);
                ShaderBytecode.ValidateFormat(bytecode: File.ReadAllBytes(path: path));
            }
        }
    }
    [Fact]
    public void AConfigFieldNamedForAFrameMemberRefusesItsInterface() {
        var config = new Dictionary<string, ShaderConfigField>(collection: FilmGrain.Config!, comparer: StringComparer.Ordinal) {
            ["time"] = new(Default: Parse(json: "0"), Type: ShaderValueType.Float),
        };
        var exception = Assert.Throws<InvalidDataException>(testCode: () => ShaderPipelineParameterLayout.ForPackage(
            config: config,
            members: FilmGrain.Members,
            package: FilmGrain.Id
        ));

        Assert.Contains(expectedSubstring: "member 'time': the name is declared twice", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void RequiredFieldsAndVectorsBind() {
        var schema = new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) {
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
        };

        Assert.False(condition: Bind(
            config: null,
            reason: out var reason,
            schema: schema,
            values: out _
        ));
        Assert.Equal(actual: reason, expected: "'f' is required.");
        Assert.True(condition: Bind(
            config: Parse(json: """{ "f": 2, "b": [3, 4] }"""),
            reason: out _,
            schema: schema,
            values: out var values
        ));
        Assert.Equal(expected: 4f, actual: BitConverter.UInt32BitsToSingle(value: values["b"].ComponentBits(index: 1)));
        Assert.Equal(expected: -5, actual: ((int)values["i"].ComponentBits(index: 0)));
        Assert.False(condition: Bind(
            config: Parse(json: """{ "f": 2, "b": [3] }"""),
            reason: out reason,
            schema: schema,
            values: out _
        ));
        Assert.Equal(actual: reason, expected: "'b' must be an array of 2 numbers in [0, 10].");
        Assert.Contains(
            collection: ShaderConfigBinding.JsonSchema(description: null, schema: schema)["required"]!.AsArray().Select(selector: static node => node!.GetValue<string>()),
            expected: "f"
        );
    }
}
