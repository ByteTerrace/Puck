using System.Text.Json;

namespace Puck.Shaders.Tests;

/// <summary>The <c>interface-echo</c> canary's fixtures echo every shipped interface family: each echo document's frame and
/// pass blocks are the blocks of the shipped interfaces it stands for, member for member at the same offsets and types, its
/// echo pass is what the generator makes of it, its perturbed twin differs from the generator's output only by the edits it
/// names, and its image holds one pixel per member. A shipped package or shader set whose frame data no echo stands for
/// turns the coverage law red. Where DXC is on the search path, compiler reflection holds each echo to its layout.</summary>
public sealed class InterfaceEchoCanaryFixtureTests {
    // The echo documents, one per row of the canary's world, in its row order.
    private static readonly string[] Echoes = ["ink-simulation", "ink-visualize", "ink-finish", "tint", "sdf-film-grain", "place", "overlay"];

    private static string FixturePath(string fileName) => RepositoryPaths.Resolve(relativePath: $"tests/Puck.World.Canaries/interface-echo/{fileName}");
    private static ShaderPipelinePlannedPass[] PassesOf(string path) =>
        [.. new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: Path.GetFileNameWithoutExtension(path: path),
            path: path
        )).Passes];
    private static ShaderPipelinePlannedPass EchoPass(string echo) => Assert.Single(collection: PassesOf(path: FixturePath(fileName: $"{echo}.graph.json")));
    private static ShaderInterfaceLayout PassOf(string relativePath, string pass) =>
        PassesOf(path: RepositoryPaths.Resolve(relativePath: relativePath)).Single(predicate: planned => (planned.Name == pass)).Parameters.Layout;
    private static ShaderInterfaceLayout PackageOf(string id) {
        Assert.True(condition: RenderGraphPackageCatalog.Shipped.TryGet(
            id: id,
            package: out var package
        ));

        return ShaderPipelineParameterLayout.ForPackage(
            config: package.Config,
            members: package.Members,
            package: package.Id
        ).Layout;
    }
    private static string ShaderSetPath(string id) {
        Assert.True(condition: ShaderSetCatalog.Shipped.TryGetPath(
            id: id,
            path: out var path
        ));

        return path;
    }

    // What each echo stands for, by name: the shipped interfaces whose blocks it reads.
    private static readonly (string Echo, string Target, Func<ShaderInterfaceLayout> Layout)[] Targets = [
        ("ink-simulation", "ink.graph.json simulation", static () => PassOf(pass: "simulation", relativePath: "src/Puck.World/Assets/pipelines/ink.graph.json")),
        ("ink-visualize", "ink.graph.json visualize", static () => PassOf(pass: "visualize", relativePath: "src/Puck.World/Assets/pipelines/ink.graph.json")),
        ("ink-finish", "ink.graph.json finish", static () => PassOf(pass: "finish", relativePath: "src/Puck.World/Assets/pipelines/ink.graph.json")),
        ("ink-finish", "moth.hlsl", static () => PassOf(pass: "moth", relativePath: "src/Puck.World/Assets/pipelines/moth.hlsl")),
        ("tint", "the pipeline-package canary's tint", static () => PassOf(pass: "tint", relativePath: "tests/Puck.World.Canaries/pipeline-package/tint.graph.json")),
        ("sdf-film-grain", "shader set sdf-film-grain", static () => new ShaderInterfaceLayout(shaderInterface: ShaderSetManifest.ReadFrameInterface(manifestPath: ShaderSetPath(id: "sdf-film-grain")))),
        ("sdf-film-grain", "package post.sdf-film-grain", static () => PackageOf(id: (RenderGraphPackageCatalog.PostProcessPrefix + "sdf-film-grain"))),
        ("place", "package place", static () => PackageOf(id: RenderGraphPackageCatalog.Place)),
        ("overlay", "package overlay", static () => PackageOf(id: RenderGraphPackageCatalog.Overlay)),
        // Each source conversion package binds its region and image beside a pass block of the extent alone, ink finish's.
        .. RenderGraphPackageCatalog.SourceConversions.Select(selector: static id => ("ink-finish", $"package {id}", ((Func<ShaderInterfaceLayout>)(() => PackageOf(id: id))))),
    ];

    public static TheoryData<string> EchoNames() => [.. Echoes];
    public static TheoryData<string, string> TargetNames() => [.. Targets.Select(selector: static target => (target.Echo, target.Target))];

    private static ShaderInterfaceGroupLayout[] Blocks(ShaderInterfaceLayout layout) =>
        [.. layout.Groups.Where(predicate: static group => (group.BlockMembers.Count != 0))];
    // The last block's last member that is not padding, whose first word the perturbation retargets.
    private static (uint Set, uint Word) LastMemberWord(ShaderInterfaceLayout layout) {
        var group = Blocks(layout: layout)[^1];
        var member = group.BlockMembers.Last(predicate: static member => !member.Name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "_pad"
        ));

        return (group.Set, (member.Offset / 4));
    }
    private static string Perturb(string echo, ShaderInterface shaderInterface, ShaderInterfaceLayout layout) {
        var generated = ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface);

        var (set, word) = LastMemberWord(layout: layout);
        var lastLine = $"    echo[uint2({(ShaderInterfaceEcho.Width(shaderInterface: shaderInterface) - 1)}, 0)] = ";
        var start = generated.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: lastLine
        );
        var expected = $"0x{ShaderInterfaceEcho.Sentinel(set: set, word: word):X8}u";
        var at = generated.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: start,
            value: expected
        );

        Assert.True(condition: (start >= 0));
        Assert.True(condition: (at > start));

        var perturbed = string.Concat(
            str0: generated[..at],
            str1: $"0x{ShaderInterfaceEcho.Sentinel(set: set, word: (word + 1)):X8}u",
            str2: generated[(at + expected.Length)..]
        );

        return ($"// The echo pass of shader interface '{echo}-perturbed' ({shaderInterface.Hash}), generated from the interface and perturbed by hand: its last member's first word expects the sentinel of the word after it."
            + perturbed[perturbed.IndexOf(value: '\n')..]);
    }

    /// <summary>Each echo's frame and pass blocks are its targets' blocks: the same groups in the same sets and sizes,
    /// and the same members at the same offsets, types and lengths under the same names in order.</summary>
    [MemberData(memberName: nameof(TargetNames))]
    [Theory]
    public void Each_echo_reads_the_blocks_of_the_shipped_interfaces_it_stands_for(string echo, string target) {
        var expected = Blocks(layout: Targets.Single(predicate: row => ((row.Echo == echo) && (row.Target == target))).Layout());
        var actual = Blocks(layout: EchoPass(echo: echo).Parameters.Layout);

        Assert.Equal(
            actual: actual.Select(selector: static group => (group.Group, group.Set, group.BlockSizeBytes)),
            expected: expected.Select(selector: static group => (group.Group, group.Set, group.BlockSizeBytes))
        );

        for (var index = 0; (index < expected.Length); index++) {
            Assert.Equal(
                actual: actual[index].BlockMembers.Select(selector: static member => (member.Offset, member.Type, member.Length)),
                expected: expected[index].BlockMembers.Select(selector: static member => (member.Offset, member.Type, member.Length))
            );

            Assert.Equal(
                actual: actual[index].BlockMembers.Select(selector: static member => member.Name),
                expected: expected[index].BlockMembers.Select(selector: static member => member.Name)
            );
        }
    }
    /// <summary>Every shipped package with frame data of its own, a config or declared members, and every shipped shader
    /// set is a target of some echo.</summary>
    [Fact]
    public void Every_shipped_package_and_shader_set_with_frame_data_is_echoed() {
        var targets = Targets.Select(selector: static target => target.Target).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var package in RenderGraphPackageCatalog.Shipped.Packages) {
            if (((package.Config?.Count ?? 0) == 0) && (package.Members.Count == 0)) {
                continue;
            }

            Assert.Contains(
                collection: targets,
                expected: $"package {package.Id}"
            );
        }

        Assert.NotEmpty(collection: ShaderSetCatalog.Shipped.Ids);

        foreach (var id in ShaderSetCatalog.Shipped.Ids) {
            Assert.Contains(
                collection: targets,
                expected: $"shader set {id}"
            );
        }
    }
    /// <summary>Each echo pass is the generated echo of its pass interface.</summary>
    [MemberData(memberName: nameof(EchoNames))]
    [Theory]
    public void Each_echo_pass_is_the_generated_echo_of_its_pass_interface(string echo) {
        Assert.Equal(
            actual: File.ReadAllText(path: FixturePath(fileName: $"{echo}.echo.hlsl")),
            expected: ShaderInterfaceEcho.Generate(shaderInterface: EchoPass(echo: echo).Parameters.Interface)
        );
    }
    /// <summary>Each perturbed echo differs from the generator's output only by its header and its last member's first
    /// word expecting the sentinel of the word after it.</summary>
    [MemberData(memberName: nameof(EchoNames))]
    [Theory]
    public void Each_perturbed_echo_differs_from_the_generator_only_by_its_named_edits(string echo) {
        var parameters = EchoPass(echo: $"{echo}-perturbed").Parameters;

        Assert.Equal(
            actual: File.ReadAllText(path: FixturePath(fileName: $"{echo}-perturbed.echo.hlsl")),
            expected: Perturb(
                echo: echo,
                layout: parameters.Layout,
                shaderInterface: parameters.Interface
            )
        );
    }
    /// <summary>Each echo image holds one pixel per member, and the canary reads each capture at that extent, the
    /// discriminating leg's partial region holding every pixel but the last.</summary>
    [MemberData(memberName: nameof(EchoNames))]
    [Theory]
    public void Each_echo_image_holds_one_pixel_per_member_and_the_canary_captures_that_extent(string echo) {
        var width = ShaderInterfaceEcho.Width(shaderInterface: EchoPass(echo: echo).Parameters.Interface);

        foreach (var name in ((string[])[echo, $"{echo}-perturbed"])) {
            var image = Assert.Single(collection: ShaderPipelineLoader.ReadDefinition(
                name: name,
                path: FixturePath(fileName: $"{name}.graph.json")
            ).Resources);

            Assert.Equal(
                actual: image.Dimensions!.Resolve(
                    frameHeight: 256,
                    frameWidth: 256
                ),
                expected: (width, 1u)
            );
            Assert.Equal(
                actual: ShaderInterfaceEcho.Width(shaderInterface: EchoPass(echo: name).Parameters.Interface),
                expected: width
            );
        }

        using var canary = JsonDocument.Parse(json: File.ReadAllText(path: FixturePath(fileName: "canary.json")));
        var regions = 0;

        foreach (var leg in ((string[])["positive", "discriminating"])) {
            foreach (var expectation in canary.RootElement.GetProperty(propertyName: leg).GetProperty(propertyName: "expect").EnumerateArray()) {
                if ((expectation.GetProperty(propertyName: "type").GetString() != "imageRegion") || (expectation.GetProperty(propertyName: "capture").GetString() != $"{echo}.png")) {
                    continue;
                }

                regions++;
                Assert.Equal(
                    actual: expectation.GetProperty(propertyName: "extent").EnumerateArray().Select(selector: static value => value.GetUInt32()),
                    expected: [width, 1u]
                );

                var right = expectation.GetProperty(propertyName: "region")[2].GetDouble();

                if (right < 1) {
                    // A pixel is read when its centre lies inside the region: every centre but the last one's.
                    Assert.InRange(
                        actual: right,
                        high: ((width - 0.5) / width),
                        low: ((width - 1.5) / width)
                    );
                }
            }
        }

        Assert.Equal(
            actual: regions,
            expected: 3
        );
    }
    /// <summary>Where DXC is on the search path, each echo compiles, and SPIR-V (and on Windows DXIL) reflection reads its
    /// blocks exactly as its layout places them.</summary>
    [MemberData(memberName: nameof(EchoNames))]
    [Theory]
    public void Reflection_holds_each_echo_and_its_perturbed_twin_to_their_layout(string echo) {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the echo passes."
        );

        var cache = Directory.CreateTempSubdirectory(prefix: "puck-interface-echo-");

        try {
            var loader = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache.FullName));

            foreach (var name in ((string[])[echo, $"{echo}-perturbed"])) {
                var result = loader.Load(
                    cancellationToken: TestContext.Current.CancellationToken,
                    name: name,
                    path: FixturePath(fileName: $"{name}.graph.json")
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
            }
        } finally {
            cache.Delete(recursive: true);
        }
    }
}
