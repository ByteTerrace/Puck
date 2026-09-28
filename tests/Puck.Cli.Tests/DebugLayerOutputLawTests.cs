using Puck.Cli.Canary;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for the runner's one validation-layer rule (<see cref="CanaryCommand.DebugLayerInvariant"/> over
/// <see cref="DebugLayerOutput"/>): a leg booted with its backend's validation layer fails on a Vulkan validation line,
/// on any Direct3D 12 debug-layer line and on the statement that the Direct3D 12 layer never loaded, naming the first
/// such line; the loader's general notices and the layer's own liveness line pass; a leg booted without the layer has
/// no such invariant; and the one message the layer raises by design is left out in one place, the Direct3D 12 drain,
/// so the rule keeps no allowlist of its own.
/// </summary>
public sealed class DebugLayerOutputLawTests {
    private const string Direct3D12Live = "[d3d12-debug] live D3D12 WARNING: Live ID3D12Resource at 0x0000, Refcount: 1";
    private const string Direct3D12Loaded = "[d3d12] debug layer live: the device reports through its info queue";
    private const string Direct3D12Message = "[d3d12-debug] D3D12_MESSAGE_SEVERITY_ERROR: CreateGraphicsPipelineState: the root signature does not match";
    private const string Direct3D12NotLoaded = "[d3d12] debug layer requested but not loaded: the device has no info queue, so nothing is validated";
    private const string LoaderNotice = "[vulkan-debug] general Loader Message: loaderAddLayerProperties: layer json file has no instance extensions";
    private const string VulkanValidation = "[vulkan-debug] validation VUID-vkCmdDraw-None-08600: the bound descriptor set was never written";

    private static CanaryAssertionResult Verdict(params string[] stderr) {
        var verdict = CanaryCommand.DebugLayerInvariant(debugLayers: true, stderr: ["[world] definition: fixture.world.json (--world)", .. stderr]);

        Assert.NotNull(@object: verdict);

        return verdict;
    }

    [Fact]
    public void AVulkanValidationLineFailsTheLegAndIsNamed() {
        var verdict = Verdict(LoaderNotice, VulkanValidation);

        Assert.False(condition: verdict.Passed);
        Assert.Contains(expectedSubstring: VulkanValidation, actualString: verdict.Detail);
    }
    [Fact]
    public void AnyDirect3D12DebugLineFailsTheLeg() {
        Assert.False(condition: Verdict(Direct3D12Loaded, Direct3D12Message).Passed);
        Assert.False(condition: Verdict(Direct3D12Loaded, Direct3D12Live).Passed);
    }
    [Fact]
    public void ALayerThatNeverLoadedFailsTheLegBecauseNothingWasValidated() {
        var verdict = Verdict(Direct3D12NotLoaded);

        Assert.False(condition: verdict.Passed);
        Assert.Contains(expectedSubstring: Direct3D12NotLoaded, actualString: verdict.Detail);
    }
    [Fact]
    public void TheVerdictNamesTheFirstFailingLine() {
        var verdict = Verdict(Direct3D12Loaded, Direct3D12Live, VulkanValidation, Direct3D12Message);

        Assert.Contains(expectedSubstring: Direct3D12Live, actualString: verdict.Detail);
        Assert.DoesNotContain(expectedSubstring: VulkanValidation, actualString: verdict.Detail);
    }
    [Fact]
    public void LoaderNoticesAndALiveLayerPass() {
        Assert.True(condition: Verdict(LoaderNotice, Direct3D12Loaded, "[vulkan-debug] performance Validation Performance Warning: a hint").Passed);
        Assert.True(condition: Verdict().Passed);
    }
    [Fact]
    public void ALegBootedWithoutTheLayerHasNoValidationInvariant() {
        Assert.Null(@object: CanaryCommand.DebugLayerInvariant(debugLayers: false, stderr: [VulkanValidation, Direct3D12Message, Direct3D12NotLoaded]));
    }
    /// <summary>The pipeline-library miss is the one message the layer raises by design. The Direct3D 12 drain leaves
    /// it out where it reads the info queue, the only place the repository names it, so it never reaches stderr, and
    /// the runner's rule fails every <c>[d3d12-debug]</c> line it does see, whatever the line says.</summary>
    [Fact]
    public void TheDesignedMissIsLeftOutInOnePlaceAndTheRuleKeepsNoAllowlist() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var naming = Directory.EnumerateFiles(path: Path.Combine(path1: repositoryRoot, path2: "src"), searchOption: SearchOption.AllDirectories, searchPattern: "*.cs")
            .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: repositoryRoot).Replace(newChar: '/', oldChar: '\\'))
            .Where(predicate: static path => (!path.Contains(comparisonType: StringComparison.Ordinal, value: "/obj/") && !path.Contains(comparisonType: StringComparison.Ordinal, value: "/bin/")))
            .Where(predicate: path => File.ReadAllText(path: Path.Combine(path1: repositoryRoot, path2: path)).Contains(comparisonType: StringComparison.Ordinal, value: "LOADPIPELINE_NAMENOTFOUND"))
            .ToArray();

        Assert.Equal(actual: naming, expected: ["src/Puck.DirectX/Interop/DirectXDeviceContext.cs"]);
        Assert.False(condition: Verdict("[d3d12-debug] D3D12_MESSAGE_SEVERITY_WARNING: LoadPipeline: the name was not found in the library").Passed);
    }
}
