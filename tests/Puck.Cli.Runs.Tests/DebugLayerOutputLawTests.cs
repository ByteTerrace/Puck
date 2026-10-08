using Puck.Cli.Parity;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// Laws for the one validation-layer rule every verb that boots a World with <c>--debug-layers</c> shares
/// (<see cref="DebugLayerOutput.Verdict"/>): a leg booted with its backend's validation layer fails on a Vulkan validation line,
/// on any Direct3D 12 debug-layer line and on the statement that the layer is not live, naming the first such line,
/// and fails when its World never said its backend's layer is live; the loader's general notices and the layer's own
/// liveness line pass; a leg booted without the layer has no such invariant; and the one message the layer raises by
/// design is left out in one place, the Direct3D 12 drain,
/// so the rule keeps no allowlist of its own. <c>puck canary</c>, <c>puck parity</c> and <c>puck qualify</c> boot a leg
/// under the layer with the same arguments, and <c>puck parity</c> fails a run whose leg reported a message.
/// </summary>
public sealed class DebugLayerOutputLawTests {
    private const string Direct3D12Live = "[d3d12-debug] live D3D12 WARNING: Live ID3D12Resource at 0x0000, Refcount: 1";
    private const string Direct3D12Loaded = "[d3d12] debug layer live: the device reports through its info queue";
    private const string Direct3D12Message = "[d3d12-debug] D3D12_MESSAGE_SEVERITY_ERROR: CreateGraphicsPipelineState: the root signature does not match";
    private const string Direct3D12NotLoaded = "[d3d12] debug layer requested but not loaded: the device has no info queue, so nothing is validated";
    private const string LoaderNotice = "[vulkan-debug] general Loader Message: loaderAddLayerProperties: layer json file has no instance extensions";
    private const string VulkanLive = "[vulkan] validation layer live: the instance reports through its debug messenger";
    private const string VulkanNotLive = "[vulkan] validation layer requested but not live: the instance has no debug messenger, so nothing is reported";
    private const string VulkanValidation = "[vulkan-debug] validation VUID-vkCmdDraw-None-08600: the bound descriptor set was never written";

    // A Vulkan leg's verdict over a World that said its layer is live, then wrote the given lines.
    private static DebugLayerVerdict Verdict(params string[] stderr) => Verdict(backend: "vulkan", stderr: [VulkanLive, .. stderr]);
    private static DebugLayerVerdict Verdict(string backend, string[] stderr) {
        var verdict = DebugLayerOutput.Verdict(backend: backend, debugLayers: true, stderr: ["[world] definition: fixture.world.json (--world)", .. stderr]);

        Assert.NotNull(@object: verdict);

        return verdict.Value;
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
        Assert.Contains(expectedSubstring: VulkanNotLive, actualString: Verdict(backend: "vulkan", stderr: [VulkanNotLive]).Detail);
    }
    /// <summary>A leg booted under the layer passes only when its World said its own backend's layer is live: a leg
    /// whose live line is suppressed fails, naming the line it never saw, because a layer that silently failed to load
    /// reports nothing; another backend's live line does not stand in for it.</summary>
    [Fact]
    public void ALegWhoseLayerNeverSaidItWasLiveFails() {
        var suppressed = Verdict(backend: "vulkan", stderr: [LoaderNotice]);

        Assert.False(condition: suppressed.Passed);
        Assert.Contains(expectedSubstring: DebugLayerOutput.VulkanLivePrefix, actualString: suppressed.Detail);
        Assert.True(condition: Verdict(backend: "vulkan", stderr: [VulkanLive]).Passed);
        Assert.True(condition: Verdict(backend: "directx", stderr: [Direct3D12Loaded]).Passed);
        Assert.False(condition: Verdict(backend: "directx", stderr: [VulkanLive]).Passed);
        Assert.False(condition: Verdict(backend: "vulkan", stderr: [Direct3D12Loaded]).Passed);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => DebugLayerOutput.Verdict(backend: "metal", debugLayers: true, stderr: []));
    }
    /// <summary>The live and not-live prefixes the rule reads are the lines each backend writes.</summary>
    [Fact]
    public void TheLiveLinesAreTheOnesTheBackendsWrite() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var vulkan = File.ReadAllText(path: Path.Combine(path1: repositoryRoot, path2: "src/Puck.Vulkan/Interop/VulkanInstance.cs"));
        var direct3D12 = File.ReadAllText(path: Path.Combine(path1: repositoryRoot, path2: "src/Puck.DirectX/Interop/DirectXDeviceContext.cs"));

        Assert.Contains(actualString: vulkan, expectedSubstring: $"\"{DebugLayerOutput.VulkanLivePrefix}");
        Assert.Contains(actualString: vulkan, expectedSubstring: $"\"{DebugLayerOutput.VulkanNotLivePrefix}");
        Assert.Contains(actualString: direct3D12, expectedSubstring: $"\"{DebugLayerOutput.Direct3D12LivePrefix}");
        Assert.Contains(actualString: direct3D12, expectedSubstring: $"\"{DebugLayerOutput.Direct3D12NotLoadedPrefix}");
    }
    [Fact]
    public void TheVerdictNamesTheFirstFailingLine() {
        var verdict = Verdict(Direct3D12Loaded, Direct3D12Live, VulkanValidation, Direct3D12Message);

        Assert.Contains(expectedSubstring: Direct3D12Live, actualString: verdict.Detail);
        Assert.DoesNotContain(expectedSubstring: VulkanValidation, actualString: verdict.Detail);
    }
    [Fact]
    public void LoaderNoticesAndALiveLayerPass() {
        Assert.True(condition: Verdict(LoaderNotice, "[vulkan-debug] performance Validation Performance Warning: a hint").Passed);
        Assert.True(condition: Verdict().Passed);
    }
    [Fact]
    public void ALegBootedWithoutTheLayerHasNoValidationInvariant() {
        Assert.Null(@object: DebugLayerOutput.Verdict(backend: "vulkan", debugLayers: false, stderr: [VulkanValidation, Direct3D12Message, Direct3D12NotLoaded]));
    }
    [Fact]
    public void EveryVerbBootsALegUnderTheLayerWithTheSameArguments() {
        Assert.Equal(actual: DebugLayerOutput.Arguments(debugLayers: true), expected: ["--debug-layers"]);
        Assert.Empty(collection: DebugLayerOutput.Arguments(debugLayers: false));
    }
    /// <summary>A parity leg booted under the layer whose stderr carries an injected validation message prints
    /// <c>VALIDATION-FAIL</c> naming it, and fails a run whose captures held every verdict; a clean leg prints
    /// <c>VALIDATION-OK</c> and leaves the comparison's exit code as it was.</summary>
    [Fact]
    public void AParityLegThatReportsAValidationMessageFailsTheRun() {
        var failed = Verdict(LoaderNotice, VulkanValidation);
        var clean = Verdict(LoaderNotice);

        Assert.Equal(actual: ParityCommand.ValidationLine(backend: "vulkan", verdict: failed), expected: $"parity: vulkan VALIDATION-FAIL validation layer reported nothing (first: {VulkanValidation})");
        Assert.Equal(actual: ParityCommand.ValidationLine(backend: "directx", verdict: clean), expected: "parity: directx VALIDATION-OK validation layer live and reported nothing");
        Assert.Equal(actual: ParityCommand.Fold(compared: CliExit.Success, validated: failed.Passed), expected: CliExit.Failed);
        Assert.Equal(actual: ParityCommand.Fold(compared: CliExit.Success, validated: clean.Passed), expected: CliExit.Success);
        Assert.Equal(actual: ParityCommand.Fold(compared: CliExit.Refused, validated: failed.Passed), expected: CliExit.Refused);
        Assert.Equal(actual: ParityCommand.Fold(compared: CliExit.Failed, validated: clean.Passed), expected: CliExit.Failed);
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ARefusedParityLegStillReportsItsValidationVerdict(bool ran) {
        var process = (ran ? new CliProcessResult(
            ExitCode: 1,
            OutputLines: [new CliProcessOutputLine(ElapsedMilliseconds: 0, Line: VulkanValidation, Sequence: 0, Stream: CliProcessOutputStream.Stderr)],
            Stderr: VulkanValidation,
            Stdout: string.Empty,
            TimedOut: false
        ) : null);
        var exit = ParityCommand.EvaluateBackend(
            backend: "vulkan", bakes: true, captureDirectory: string.Empty, debugLayers: true,
            leg: CliExit.Refused, process: process, validation: out var validation
        );

        Assert.Equal(actual: exit, expected: CliExit.Refused);
        Assert.NotNull(@object: validation);
        Assert.False(condition: validation.Value.Passed);
        Assert.Contains(expectedSubstring: (ran ? VulkanValidation : "never said it was live"), actualString: validation.Value.Detail);
        Assert.StartsWith(expectedStartString: "parity: vulkan VALIDATION-FAIL ", actualString: ParityCommand.ValidationLine(backend: "vulkan", verdict: validation.Value));
    }
    [Fact]
    public void AParityLegCannotClaimLivenessFromStdout() {
        var process = new CliProcessResult(
            ExitCode: 1,
            OutputLines: [new CliProcessOutputLine(ElapsedMilliseconds: 0, Line: VulkanLive, Sequence: 0, Stream: CliProcessOutputStream.Stdout)],
            Stderr: string.Empty,
            Stdout: VulkanLive,
            TimedOut: false
        );

        _ = ParityCommand.EvaluateBackend(
            backend: "vulkan", bakes: true, captureDirectory: string.Empty, debugLayers: true,
            leg: CliExit.Refused, process: process, validation: out var validation
        );

        Assert.NotNull(@object: validation);
        Assert.False(condition: validation.Value.Passed);
        Assert.Contains(expectedSubstring: "never said it was live", actualString: validation.Value.Detail);
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
