using System.Buffers.Binary;
using System.Text.Json;

namespace Puck.Shaders.Tests;

/// <summary>The <c>pipeline-echo</c> canary's fixtures stay what the generator makes of their interfaces: the echo pass is
/// generated, its perturbed twin differs from the generator's output only by the edits it names, and its output image
/// holds one pixel per member. Where DXC is on the search path, compiler reflection over both bytecodes is the check a
/// GPU run repeats: the echo reads its blocks exactly as laid out, and a load refuses declarations that do not.</summary>
public sealed class EchoCanaryFixtureTests {
    private static string FixturePath(string fileName) => RepositoryPaths.Resolve(relativePath: $"tests/Puck.World.Canaries/pipeline-echo/{fileName}");
    private static ShaderPipelinePlannedPass PassOf(string document) =>
        Assert.Single(collection: new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: Path.GetFileNameWithoutExtension(path: document),
            path: FixturePath(fileName: document)
        )).Passes);
    private static string ReplaceFirstLine(string text, string line) =>
        (line + text[text.IndexOf(value: '\n')..]);
    // The perturbation: time (frame group word 4) and timeDelta (word 5) each expect the other's sentinel.
    private static string SwapTimeSentinels(string text) {
        string Check(string member, uint word) => $"(asuint(frameGroup.{member}) == 0x{ShaderInterfaceEcho.Sentinel(set: 0, word: word):X8}u)";

        Assert.Contains(
            actualString: text,
            expectedSubstring: Check(member: "time", word: 4)
        );
        Assert.Contains(
            actualString: text,
            expectedSubstring: Check(member: "timeDelta", word: 5)
        );

        return text
            .Replace(newValue: "\0", oldValue: Check(member: "time", word: 4))
            .Replace(newValue: Check(member: "timeDelta", word: 4), oldValue: Check(member: "timeDelta", word: 5))
            .Replace(newValue: Check(member: "time", word: 5), oldValue: "\0");
    }

    [Fact]
    public void The_echo_pass_is_the_generated_echo_of_its_pass_interface() {
        var shaderInterface = PassOf(document: "echo.graph.json").Parameters.Interface;

        Assert.Equal(
            actual: File.ReadAllText(path: FixturePath(fileName: "frame.echo.hlsl")),
            expected: ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface)
        );
    }
    [Fact]
    public void The_perturbed_echo_differs_from_the_generator_only_by_its_named_edits() {
        var shaderInterface = PassOf(document: "perturbed.graph.json").Parameters.Interface;

        Assert.Equal(
            actual: File.ReadAllText(path: FixturePath(fileName: "perturbed.echo.hlsl")),
            expected: SwapTimeSentinels(text: ReplaceFirstLine(
                line: $"// The echo pass of shader interface 'perturbed' ({shaderInterface.Hash}), generated from the interface and perturbed by hand: time and timeDelta expect each other's sentinel.",
                text: ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface)
            ))
        );
    }
    [InlineData("echo.graph.json")]
    [InlineData("perturbed.graph.json")]
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
    public void The_sentinels_fill_every_member_word_of_every_block_and_leave_the_padding_zero() {
        var layout = PassOf(document: "echo.graph.json").Parameters.Layout;
        var words = new HashSet<uint>();
        var groups = layout.Groups.Where(predicate: static group => (group.BlockMembers.Count != 0)).ToArray();

        Assert.Equal(
            actual: groups.Select(selector: static group => group.Set),
            expected: [0u, 3u]
        );

        foreach (var group in groups) {
            var block = new byte[group.BlockSizeBytes];

            block.AsSpan().Fill(value: 0xCD);
            ShaderInterfaceEcho.WriteSentinels(
                block: block,
                group: group
            );

            foreach (var member in group.BlockMembers) {
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
                            expected: ShaderInterfaceEcho.Sentinel(
                                set: group.Set,
                                word: index
                            )
                        );
                        Assert.True(condition: words.Add(item: value));
                        Assert.True(condition: float.IsNormal(f: BitConverter.UInt32BitsToSingle(value: value)));
                    }
                }
            }
        }
    }
    [Fact]
    public void Reflection_holds_the_echo_to_its_layout_and_a_load_refuses_declarations_that_misplace_a_member() {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the echo passes."
        );

        var cache = Directory.CreateTempSubdirectory(prefix: "puck-echo-");

        try {
            var loader = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache.FullName));
            var result = loader.Load(
                cancellationToken: TestContext.Current.CancellationToken,
                name: "echo",
                path: FixturePath(fileName: "echo.graph.json")
            );

            Assert.True(
                condition: (result.Status == ShaderPipelineLoadStatus.Compiled),
                userMessage: result.Message
            );

            var pass = Assert.Single(collection: result.Pipeline!.Plan.Passes);
            var shader = result.Pipeline.Shaders[pass.Name];

            Assert.Null(@object: pass.Parameters.Layout.Mismatch(reflected: SpirvInterfaceReader.Read(module: shader.SpirvByStage[ShaderStage.Compute].Span)));
            if (OperatingSystem.IsWindows()) {
                using var dxil = DxilInterfaceReader.Load(toolchain: new ShaderToolchain());

                Assert.Null(@object: pass.Parameters.Layout.Mismatch(reflected: dxil.Read(container: shader.DxilByStage[ShaderStage.Compute].Span)));
            }

            // Declarations that misplace a member, the generator's with time and timeDelta at each other's offset, beside
            // an echo that includes them: the module reads the frame block somewhere the interface does not lay it out,
            // so the load refuses it by name before any device sees it.
            var shaderInterface = pass.Parameters.Interface;
            var perturbed = cache.CreateSubdirectory(path: "perturbed");

            File.Copy(
                destFileName: Path.Combine(
                    path1: perturbed.FullName,
                    path2: "echo.graph.json"
                ),
                sourceFileName: FixturePath(fileName: "echo.graph.json")
            );
            File.WriteAllText(
                contents: ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface).Replace(
                    newValue: "#include \"swapped.hlsli\"",
                    oldValue: $"#include \"{ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name)}\""
                ),
                path: Path.Combine(
                    path1: perturbed.FullName,
                    path2: "frame.echo.hlsl"
                )
            );
            File.WriteAllText(
                contents: ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface)
                    .Replace(newValue: "\0", oldValue: "[[vk::offset(16)]] float time;")
                    .Replace(newValue: "[[vk::offset(20)]] float time;", oldValue: "[[vk::offset(20)]] float timeDelta;")
                    .Replace(newValue: "[[vk::offset(16)]] float timeDelta;", oldValue: "\0"),
                path: Path.Combine(
                    path1: perturbed.FullName,
                    path2: "swapped.hlsli"
                )
            );

            var refused = loader.Load(
                cancellationToken: TestContext.Current.CancellationToken,
                name: "echo",
                path: Path.Combine(
                    path1: perturbed.FullName,
                    path2: "echo.graph.json"
                )
            );

            Assert.Equal(
                actual: refused.Status,
                expected: ShaderPipelineLoadStatus.Failed
            );
            Assert.StartsWith(
                actualString: refused.Message,
                expectedStartString: "[SHADERPIPE_INTERFACE] Pass 'frame' Compute: "
            );
            Assert.Contains(
                actualString: refused.Message,
                expectedSubstring: "timeDelta@16"
            );
        } finally {
            cache.Delete(recursive: true);
        }
    }
}
