using System.Buffers.Binary;
using System.Text.Json;

namespace Puck.Shaders.Tests;

/// <summary>The <c>pipeline-echo</c> canary's fixtures stay what the generator makes of their interfaces: the echo pass is
/// generated, its perturbed twin differs from the generator's output only by the edits it names, and its output image
/// holds one pixel per member. Where DXC is on the search path, compiler reflection over both bytecodes is the check a
/// GPU run repeats: the echo reads its block exactly as laid out, and the perturbed echo does not.</summary>
public sealed class EchoCanaryFixtureTests {
    private static string FixturePath(string fileName) => RepositoryPaths.Resolve(relativePath: $"tests/Puck.World.Canaries/pipeline-echo/{fileName}");
    private static ShaderPipelinePlannedPass PassOf(string document) =>
        Assert.Single(collection: new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: Path.GetFileNameWithoutExtension(path: document),
            path: FixturePath(fileName: document)
        )).Passes);
    private static string ReplaceFirstLine(string text, string line) =>
        (line + text[text.IndexOf(value: '\n')..]);

    [Fact]
    public void The_echo_pass_is_the_generated_echo_of_its_pass_interface() {
        var shaderInterface = PassOf(document: "echo.pipeline.json").Parameters.Interface;

        Assert.Equal(
            actual: File.ReadAllText(path: FixturePath(fileName: "frame.echo.hlsl")),
            expected: ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface)
        );
    }
    [Fact]
    public void The_perturbed_echo_differs_from_the_generator_only_by_its_named_edits() {
        var shaderInterface = PassOf(document: "perturbed.pipeline.json").Parameters.Interface;
        var echo = ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface);
        var declarations = ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface);

        Assert.Equal(
            actual: File.ReadAllText(path: FixturePath(fileName: "perturbed.echo.hlsl")),
            expected: ReplaceFirstLine(
                line: $"// The echo pass of shader interface 'perturbed' ({shaderInterface.Hash}), generated from the interface and pointed by hand at the perturbed declarations.",
                text: echo
            ).Replace(
                newValue: "#include \"perturbed-swapped.hlsli\"",
                oldValue: "#include \"perturbed.interface.hlsli\""
            )
        );
        Assert.Equal(
            actual: File.ReadAllText(path: FixturePath(fileName: "perturbed-swapped.hlsli")),
            expected: ReplaceFirstLine(
                line: $"// The declarations generated for shader interface 'perturbed' ({shaderInterface.Hash}), perturbed by hand: time and timeDelta read each other's offset.",
                text: declarations
            ).Replace(
                newValue: "float swapped;",
                oldValue: "[[vk::offset(24)]] float time;"
            ).Replace(
                newValue: "[[vk::offset(28)]] float time;",
                oldValue: "[[vk::offset(28)]] float timeDelta;"
            ).Replace(
                newValue: "[[vk::offset(24)]] float timeDelta;",
                oldValue: "float swapped;"
            )
        );
    }
    [InlineData("echo.pipeline.json")]
    [InlineData("perturbed.pipeline.json")]
    [Theory]
    public void The_echo_image_holds_one_pixel_per_member_and_the_canary_captures_that_extent(string document) {
        var pass = PassOf(document: document);
        var width = ShaderInterfaceEcho.Width(shaderInterface: pass.Parameters.Interface);
        var definition = ShaderPipelineLoader.ReadDefinition(
            name: "echo",
            path: FixturePath(fileName: document)
        );
        var image = Assert.Single(collection: definition.Resources);

        Assert.Equal(
            actual: image.Dimensions!.Resolve(
                frameHeight: 256,
                frameWidth: 256
            ),
            expected: (width, 1u)
        );

        using var canary = JsonDocument.Parse(json: File.ReadAllText(path: FixturePath(fileName: "canary.json")));

        foreach (var leg in ((string[])["positive", "discriminating"])) {
            foreach (var expectation in canary.RootElement.GetProperty(propertyName: leg).GetProperty(propertyName: "expect").EnumerateArray()) {
                if (expectation.GetProperty(propertyName: "type").GetString() == "imageRegion") {
                    Assert.Equal(
                        actual: expectation.GetProperty(propertyName: "extent").EnumerateArray().Select(selector: static value => value.GetUInt32()),
                        expected: [width, 1u]
                    );
                }
            }
        }
    }
    [Fact]
    public void The_sentinels_fill_every_member_word_and_leave_the_padding_zero() {
        var layout = PassOf(document: "echo.pipeline.json").Parameters;
        var block = new byte[layout.SizeBytes];

        block.AsSpan().Fill(value: 0xCD);
        ShaderInterfaceEcho.WriteSentinels(
            block: block,
            layout: layout
        );

        var words = new HashSet<uint>();

        foreach (var member in layout.Layout.PushedGroup!.BlockMembers) {
            var count = (member.Type.ComponentCount() * Math.Max(
                val1: 1u,
                val2: member.Length
            ));

            for (var word = 0u; (word < count); word++) {
                var index = ((member.Offset / 4) + word);
                var value = BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: ((int)(index * 4))));

                if (member.Name.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "_pad"
                )) {
                    Assert.Equal(
                        actual: value,
                        expected: 0u
                    );
                } else {
                    Assert.Equal(
                        actual: value,
                        expected: ShaderInterfaceEcho.Sentinel(word: index)
                    );
                    Assert.True(condition: words.Add(item: value));
                    Assert.True(condition: float.IsNormal(f: BitConverter.UInt32BitsToSingle(value: value)));
                }
            }
        }
    }
    [Fact]
    public void Reflection_holds_the_echo_to_its_layout_and_catches_the_perturbed_offset() {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the echo passes."
        );

        var cache = Directory.CreateTempSubdirectory(prefix: "puck-echo-");

        try {
            var loader = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache.FullName));

            foreach (var (document, exact) in ((ReadOnlySpan<(string, bool)>)[("echo.pipeline.json", true), ("perturbed.pipeline.json", false)])) {
                var result = loader.Load(
                    cancellationToken: TestContext.Current.CancellationToken,
                    name: "echo",
                    path: FixturePath(fileName: document)
                );

                Assert.True(
                    condition: (result.Status == ShaderPipelineLoadStatus.Compiled),
                    userMessage: result.Message
                );

                var pass = Assert.Single(collection: result.Pipeline!.Plan.Passes);
                var shader = result.Pipeline.Shaders[pass.Name];
                var spirv = pass.Parameters.Layout.PushedBlockMismatch(reflected: SpirvInterfaceReader.Read(module: shader.SpirvByStage[ShaderStage.Compute].Span));

                if (exact) {
                    Assert.Null(@object: spirv);
                } else {
                    Assert.Contains(
                        actualString: spirv,
                        expectedSubstring: "timeDelta@24"
                    );
                }

                if (OperatingSystem.IsWindows()) {
                    using var dxil = DxilInterfaceReader.Load(toolchain: new ShaderToolchain());
                    var reflected = pass.Parameters.Layout.PushedBlockMismatch(reflected: dxil.Read(container: shader.DxilByStage[ShaderStage.Compute].Span));

                    Assert.Equal(
                        actual: (reflected is null),
                        expected: exact
                    );
                }
            }
        } finally {
            cache.Delete(recursive: true);
        }
    }
}
