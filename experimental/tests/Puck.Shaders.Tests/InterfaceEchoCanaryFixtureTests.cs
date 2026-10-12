using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Puck.SdfVm;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>The <c>interface-echo</c> canary's fixtures echo every shipped interface family: each echo document's frame and
/// pass blocks are the blocks of the shipped interfaces it stands for, member for member at the same offsets and types, its
/// echo pass is what the generator makes of it, its perturbed twin differs from the generator's output only by the edits it
/// names, and its image holds one pixel per member. A shipped package whose frame data no echo stands for turns the
/// coverage law red; the SDF engine's <c>sdf.bricks</c> interface, which the engine declares in
/// <see cref="SdfWorldInterfaces"/> rather than the catalog, is a target by name. Where DXC is on the search path, compiler reflection holds each echo to its layout.</summary>
public sealed class InterfaceEchoCanaryFixtureTests {
    // The echo documents, one per row of the canary's world, in its row order.
    private static readonly string[] Echoes = ["ink-simulation", "ink-visualize", "ink-finish", "tint", "sdf-film-grain", "place", "overlay", "sdf-world", "source", "indirect"];

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
        Assert.True(condition: RenderGraphPackageCatalog.Engine.TryGet(
            id: id,
            package: out var package
        ));

        return ShaderPipelineParameterLayout.ForPackage(
            config: package.Config,
            members: package.Members,
            package: package.Id
        ).Layout;
    }

    // What each echo stands for, by name: the shipped interfaces whose blocks it reads.
    private static readonly (string Echo, string Target, Func<ShaderInterfaceLayout> Layout)[] Targets = [
        ("ink-simulation", "ink.graph.json simulation", static () => PassOf(pass: "simulation", relativePath: "src/Puck.World/Assets/pipelines/ink.graph.json")),
        ("ink-visualize", "ink.graph.json visualize", static () => PassOf(pass: "visualize", relativePath: "src/Puck.World/Assets/pipelines/ink.graph.json")),
        ("ink-finish", "ink.graph.json finish", static () => PassOf(pass: "finish", relativePath: "src/Puck.World/Assets/pipelines/ink.graph.json")),
        ("ink-finish", "moth.hlsl", static () => PassOf(pass: "moth", relativePath: "src/Puck.World/Assets/pipelines/moth.hlsl")),
        ("tint", "the pipeline-package canary's tint", static () => PassOf(pass: "tint", relativePath: "tests/Puck.World.Canaries/pipeline-package/tint.graph.json")),
        ("sdf-film-grain", "package sdf.film-grain", static () => PackageOf(id: RenderGraphPackageCatalog.SdfFilmGrain)),
        ("place", "package place", static () => PackageOf(id: RenderGraphPackageCatalog.Place)),
        ("overlay", "package overlay", static () => PackageOf(id: RenderGraphPackageCatalog.Overlay)),
        // Each source conversion package binds its region or its imported image, its image and the work counters beside a
        // pass block of the extent and its work counter row.
        .. RenderGraphPackageCatalog.SourceConversions.Concat(second: RenderGraphPackageCatalog.ImageConversions).Select(selector: static id => ("source", $"package {id}", ((Func<ShaderInterfaceLayout>)(() => PackageOf(id: id))))),
        // The SDF engine's two pass interfaces: every per-view dispatch's, and the brick baker's, whose pass block is its
        // slice extent alone, ink finish's.
        ("sdf-world", $"package {RenderGraphPackageCatalog.SdfWorld}", static () => PackageOf(id: RenderGraphPackageCatalog.SdfWorld)),
        ("ink-finish", $"package {RenderGraphPackageCatalog.SdfBricks}", static () => SdfWorldInterfaces.BrickBakeLayout),
        // The indirect cache's classify and trace passes: the per-view pass block with the cache's own values.
        ("indirect", $"package {RenderGraphPackageCatalog.Indirect}", static () => PackageOf(id: RenderGraphPackageCatalog.Indirect)),
    ];

    public static TheoryData<string> EchoNames() => [.. Echoes];
    public static TheoryData<string, string> TargetNames() => [.. Targets.Select(selector: static target => (target.Echo, target.Target))];

    private static ShaderInterfaceGroupLayout[] Blocks(ShaderInterfaceLayout layout) =>
        [.. layout.Groups.Where(predicate: static group => (group.BlockMembers.Count != 0))];

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
    /// <summary>Every shipped package with frame data of its own, a config or declared members, is a target of some
    /// echo.</summary>
    [Fact]
    public void Every_shipped_package_with_frame_data_is_echoed() {
        var targets = Targets.Select(selector: static target => target.Target).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var package in RenderGraphPackageCatalog.Engine.Packages) {
            if (((package.Config?.Count ?? 0) == 0) && (package.Members.Count == 0)) {
                continue;
            }

            Assert.Contains(
                collection: targets,
                expected: $"package {package.Id}"
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
            expected: ShaderInterfaceEcho.GeneratePerturbed(shaderInterface: parameters.Interface)
        );
    }
    /// <summary>The discriminator changes exactly the last pixel's first sentinel to the next word's;
    /// every other pixel and word retains the positive echo's check.</summary>
    [MemberData(memberName: nameof(EchoNames))]
    [Theory]
    public void Perturbation_changes_only_the_last_members_first_word_to_the_next_sentinel(string echo) {
        var shaderInterface = EchoPass(echo: echo).Parameters.Interface;
        var members = Blocks(layout: shaderInterface.Layout()).SelectMany(selector: static group => group.BlockMembers
            .Where(predicate: static member => !member.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "_pad"))
            .Select(selector: member => (group.Set, Member: member))).ToArray();
        var positive = ShaderInterfaceEcho.Generate(shaderInterface: shaderInterface).Split(separator: '\n');
        var perturbed = ShaderInterfaceEcho.GeneratePerturbed(shaderInterface: shaderInterface).Split(separator: '\n');

        Assert.Equal(expected: positive.Length, actual: perturbed.Length);
        var changed = Assert.Single(collection: Enumerable.Range(start: 1, count: (positive.Length - 1)),
            predicate: index => (positive[index] != perturbed[index]));

        Assert.StartsWith(expectedStartString: $"    echo[uint2({(members.Length - 1)}, 0)] = ", actualString: positive[changed]);
        const string Literal = @"0x([0-9A-F]{8})u";
        var before = Regex.Matches(input: positive[changed], pattern: Literal).Select(selector: static match =>
            uint.Parse(s: match.Groups[1].Value, style: NumberStyles.AllowHexSpecifier, provider: CultureInfo.InvariantCulture)).ToArray();
        var after = Regex.Matches(input: perturbed[changed], pattern: Literal).Select(selector: static match =>
            uint.Parse(s: match.Groups[1].Value, style: NumberStyles.AllowHexSpecifier, provider: CultureInfo.InvariantCulture)).ToArray();
        var last = members[^1];

        Assert.Equal(expected: 0x40000000u | (((last.Member.Offset / 4) + 2u) << 12) | (last.Set << 8) | 0xA5u, actual: after[0]);
        Assert.NotEqual(expected: before[0], actual: after[0]);
        Assert.Equal(expected: before.Skip(count: 1), actual: after.Skip(count: 1));
        Assert.Equal(expected: Regex.Replace(input: positive[changed], pattern: Literal, replacement: "sentinel"),
            actual: Regex.Replace(input: perturbed[changed], pattern: Literal, replacement: "sentinel"));
    }
    /// <summary>Each echo image holds one pixel per member, and the canary reads each capture at that extent, the
    /// discriminating leg's partial region holding every pixel but the last.</summary>
    [MemberData(memberName: nameof(EchoNames))]
    [Theory]
    public void Each_echo_image_holds_one_pixel_per_member_and_the_canary_captures_that_extent(string echo) {
        var shaderInterface = EchoPass(echo: echo).Parameters.Interface;
        var width = ((uint)Blocks(layout: shaderInterface.Layout()).Sum(selector: static group => group.BlockMembers.Count(predicate: static member =>
            !member.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "_pad"))));

        Assert.Equal(expected: width, actual: ShaderInterfaceEcho.Width(shaderInterface: shaderInterface));

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

        using var cache = new TemporaryDirectory(prefix: "puck-interface-echo-");

        var loader = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache.RootPath));

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
    }
}
