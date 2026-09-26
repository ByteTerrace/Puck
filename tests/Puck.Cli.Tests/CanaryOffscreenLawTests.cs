using Puck.Assets;
using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves the offscreen canary shape without a GPU: the shipped pipeline manifests load and expand to one proof per
/// backend, the loader refuses an offscreen manifest that could skip a backend, <c>imageRegion</c> decides pixels
/// against author-derived bounds in both directions, and the runner classifies unsupported environments and
/// unresolved pipeline waits from a transcript.
/// </summary>
public sealed class CanaryOffscreenLawTests : IDisposable {
    private readonly string m_root = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-cli-tests-canary-offscreen-{Guid.NewGuid():N}"
    );

    public CanaryOffscreenLawTests() => Directory.CreateDirectory(path: m_root);

    private static CanaryImageRegionAssertion Region(string capture, double value, bool holds, CanaryImageReduce reduce = CanaryImageReduce.Every, int width = 32) => new(
        Bottom: 0.96875,
        Capture: capture,
        Height: 32,
        Holds: holds,
        Left: 0.03125,
        Maximum: [value, value, value, 1],
        Minimum: [value, value, value, 1],
        Name: "oracle",
        Reduce: reduce,
        Right: 0.96875,
        ToleranceCodes: 1,
        Top: 0.03125,
        Width: width
    );
    private static CanaryTranscript Transcript(string runDirectory, string[]? stdout = null, string[]? stderr = null) => new(
        RunDirectory: runDirectory,
        Stderr: (stderr ?? []),
        Stdout: (stdout ?? [])
    );
    private static CanaryLeg Leg(params CanaryAssertion[] assertions) => new(
        Assertions: assertions,
        Authorities: [],
        AuthorityWorldPath: null,
        Commands: [],
        Connect: false,
        Name: "positive",
        ScriptPath: "script.txt",
        WorldPath: "world.json"
    );
    private string Gray(string fileName, byte code, int width = 32, int height = 32, (int X, int Y, byte Code)? odd = null) {
        var rgba = new byte[((width * height) * 4)];

        for (var index = 0; (index < rgba.Length); index += 4) {
            rgba[index] = code;
            rgba[(index + 1)] = code;
            rgba[(index + 2)] = code;
            rgba[(index + 3)] = 255;
        }
        if (odd is { } pixel) {
            var offset = (((pixel.Y * width) + pixel.X) * 4);

            rgba[offset] = pixel.Code;
        }
        var path = Path.Combine(
            path1: m_root,
            path2: fileName
        );

        PngEncoder.Write(
            height: height,
            path: path,
            rgba: rgba,
            width: width
        );
        return path;
    }
    private string WriteManifestTree(string id, string manifestBody) {
        var directory = Path.Combine(
            path1: m_root,
            path2: "tests",
            path3: "Puck.World.Canaries"
        );

        directory = Path.Combine(
            path1: directory,
            path2: id
        );
        Directory.CreateDirectory(path: directory);
        foreach (var name in ((string[])["world.json", "positive.script.txt", "discriminating.script.txt"])) {
            File.WriteAllText(
                contents: ((name == "world.json")
                    ? "{}"
                    : "wire.errors\n"),
                path: Path.Combine(
                    path1: directory,
                    path2: name
                )
            );
        }
        File.WriteAllText(
            contents: manifestBody,
            path: Path.Combine(
                path1: directory,
                path2: "canary.json"
            )
        );
        return directory;
    }
    private static string Manifest(string id, string bootShape, string backends, string requirements) =>
        $$"""
        {
          "id": "{{id}}",
          "title": "a synthetic offscreen manifest",
          "binding": "a synthetic offscreen manifest",
          "bootShape": "{{bootShape}}",
          {{backends}}
          "requirements": {{requirements}},
          "timeoutSeconds": 10,
          "positive": {
            "world": "tests/Puck.World.Canaries/{{id}}/world.json",
            "script": "positive.script.txt",
            "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ],
            "expect": [ { "type": "line", "name": "clean-wire", "stream": "stdout", "match": "contains", "text": "[wire.errors: 0 rejected]", "present": true } ]
          },
          "discriminating": {
            "world": "tests/Puck.World.Canaries/{{id}}/world.json",
            "script": "discriminating.script.txt",
            "commands": [ { "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" } ],
            "expect": [ { "type": "line", "name": "clean-wire", "stream": "stdout", "match": "contains", "text": "[wire.errors: 0 rejected]", "present": true } ]
          }
        }
        """;
    private (bool Loaded, string Error) Load() {
        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out _,
            refused: out _,
            repositoryRoot: m_root,
            strict: true
        );

        return (loaded, error);
    }

    public void Dispose() {
        try {
            Directory.Delete(
                path: m_root,
                recursive: true
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    [Fact]
    public void TheShippedPipelineManifestsLoadAsOneProofPerBackend() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: repositoryRoot,
            strict: true
        );

        Assert.True(
            condition: loaded,
            userMessage: error
        );
        var offscreen = manifests.Where(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Offscreen)).ToArray();

        Assert.Contains(
            collection: offscreen,
            filter: static manifest => (manifest.Id == "pipeline-feedback")
        );
        Assert.Contains(
            collection: offscreen,
            filter: static manifest => (manifest.Id == "pipeline-ink")
        );
        Assert.All(
            collection: offscreen,
            action: static manifest => {
                Assert.Equal(
                    expected: ["vulkan", "directx"],
                    actual: manifest.Backends
                );
                Assert.False(condition: manifest.IsAutomatic);
            }
        );
        Assert.Equal(
            expected: ["pipeline-feedback on vulkan", "pipeline-feedback on directx"],
            actual: CanaryCommand.ExpandProofs(manifests: [.. offscreen.Where(predicate: static manifest => (manifest.Id == "pipeline-feedback"))]).Select(selector: static proof => proof.Label)
        );
    }
    [InlineData("pipeline-feedback/fixture.world.json", "feedback", 1f)]
    [InlineData("pipeline-feedback/wrong-history.world.json", "feedback", 1f)]
    [InlineData("pipeline-ink/fixture.world.json", "ink", 0f)]
    [Theory]
    public void EveryOffscreenFixtureWorldValidatesAsAnOffscreenPipelineHost(string relativePath, string pipeline, float timeScale) {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        var catalog = CliWorldVocabulary.EnsureInstalled();

        Assert.True(
            condition: Puck.World.WorldDefinitionLoader.TryLoadFile(
                catalog: catalog,
                catalogFingerprint: catalog.CompositionFingerprint,
                definition: out var definition,
                path: Path.Combine(
                    path1: repositoryRoot,
                    path2: "tests",
                    path3: "Puck.World.Canaries",
                    path4: relativePath
                ),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: Puck.World.WorldHostPresentation.Offscreen,
            actual: definition!.Host.Presentation
        );
        var row = Assert.Single(collection: (definition.Views.Graphs ?? []));

        Assert.Equal(
            expected: pipeline,
            actual: row.Name
        );
        Assert.Equal(
            expected: timeScale,
            actual: row.TimeScale
        );
    }
    [InlineData("offscreen", "", "[\"gpu\"]", "requires backends")]
    [InlineData("offscreen", "\"backends\": [\"vulkan\"],", "[\"gpu\"]", "cannot pass the two-backend gate")]
    [InlineData("offscreen", "\"backends\": [\"vulkan\", \"vulkan\"],", "[\"gpu\"]", "exactly once")]
    [InlineData("offscreen", "\"backends\": [\"vulkan\", \"directx\"],", "[]", "'gpu' requirement")]
    [InlineData("headless", "\"backends\": [\"vulkan\", \"directx\"],", "[]", "only bootShape 'offscreen' and 'windowed'")]
    [InlineData("windowed", "\"backends\": [\"directx\"],", "[\"gpu\"]", "cannot pass the two-backend gate")]
    [InlineData("windowed", "\"backends\": [\"vulkan\", \"directx\"],", "[]", "'gpu' requirement")]
    [Theory]
    public void AnOffscreenManifestThatCouldSkipABackendIsRefused(string bootShape, string backends, string requirements, string reason) {
        WriteManifestTree(
            id: "synthetic",
            manifestBody: Manifest(
                backends: backends,
                bootShape: bootShape,
                id: "synthetic",
                requirements: requirements
            )
        );
        var (loaded, error) = Load();

        Assert.False(condition: loaded);
        Assert.Contains(
            actualString: error,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: reason
        );
    }
    [InlineData("offscreen")]
    [InlineData("windowed")]
    [Theory]
    public void AManifestNamingBothBackendsLoadsAndRunsOnEach(string bootShape) {
        WriteManifestTree(
            id: "synthetic",
            manifestBody: Manifest(
                backends: "\"backends\": [\"directx\", \"vulkan\"],",
                bootShape: bootShape,
                id: "synthetic",
                requirements: "[\"gpu\"]"
            )
        );
        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: m_root,
            strict: true
        );

        Assert.True(
            condition: loaded,
            userMessage: error
        );
        Assert.Equal(
            expected: ["synthetic on directx", "synthetic on vulkan"],
            actual: CanaryCommand.ExpandProofs(manifests: manifests).Select(selector: static proof => proof.Label)
        );
    }
    /// <summary>A windowed manifest without backends keeps its one boot per leg, naming no backend.</summary>
    [Fact]
    public void AWindowedManifestWithoutBackendsRunsOnce() {
        WriteManifestTree(
            id: "synthetic",
            manifestBody: Manifest(
                backends: "",
                bootShape: "windowed",
                id: "synthetic",
                requirements: "[\"gpu\"]"
            )
        );
        var loaded = CanaryManifestLoader.TryLoadAll(
            error: out var error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: m_root,
            strict: true
        );

        Assert.True(
            condition: loaded,
            userMessage: error
        );
        Assert.Equal(
            expected: ["synthetic"],
            actual: CanaryCommand.ExpandProofs(manifests: manifests).Select(selector: static proof => proof.Label)
        );
    }
    [Fact]
    public void AnImageRegionDecidesTheArithmeticOracleInBothDirections() {
        // 1/16 of full scale is 15.9375 codes; UNORM rounding stores 16, one code inside the tolerance.
        Gray(
            code: 16,
            fileName: "n1.png"
        );
        Gray(
            code: 32,
            fileName: "n2.png"
        );
        // An interior pixel two codes off breaks "every" but not a region whose mean stays within tolerance.
        Gray(
            code: 16,
            fileName: "stray.png",
            odd: (5, 5, 18)
        );
        var transcript = Transcript(runDirectory: m_root);
        var results = CanaryAssertions.Evaluate(
            leg: Leg(
                Region(
                    capture: "n1.png",
                    holds: true,
                    value: 0.0625
                ),
                Region(
                    capture: "n2.png",
                    holds: true,
                    value: 0.125
                ),
                Region(
                    capture: "n2.png",
                    holds: false,
                    value: 0.0625
                ),
                Region(
                    capture: "stray.png",
                    holds: false,
                    value: 0.0625
                ),
                Region(
                    capture: "stray.png",
                    holds: true,
                    reduce: CanaryImageReduce.Mean,
                    value: 0.0625
                )
            ),
            primaryTranscript: transcript
        ).Results;

        Assert.All(
            collection: results,
            action: static result => Assert.True(
                condition: result.Passed,
                userMessage: result.Detail
            )
        );
        var inverted = CanaryAssertions.Evaluate(
            leg: Leg(Region(
                capture: "n2.png",
                holds: true,
                value: 0.0625
            )),
            primaryTranscript: transcript
        );

        Assert.False(condition: inverted.Passed);
    }
    [Fact]
    public void AMissingCaptureOrAWrongExtentFailsEitherDirection() {
        Gray(
            code: 16,
            fileName: "n1.png"
        );
        foreach (var holds in ((bool[])[true, false])) {
            var results = CanaryAssertions.Evaluate(
                leg: Leg(
                    Region(
                        capture: "absent.png",
                        holds: holds,
                        value: 0.0625
                    ),
                    Region(
                        capture: "n1.png",
                        holds: holds,
                        value: 0.0625,
                        width: 64
                    )
                ),
                primaryTranscript: Transcript(runDirectory: m_root)
            ).Results;

            Assert.All(
                collection: results,
                action: static result => Assert.False(condition: result.Passed)
            );
        }
    }
    [Fact]
    public void ARegionHoldingNoPixelCenterCountsNone() {
        Assert.Equal(
            expected: 0,
            actual: CanaryAssertions.RegionPixelCount(
                bottom: 0.51,
                height: 32,
                left: 0.49,
                right: 0.51,
                top: 0.49,
                width: 32
            )
        );
        Assert.Equal(
            expected: 900,
            actual: CanaryAssertions.RegionPixelCount(
                bottom: 0.96875,
                height: 32,
                left: 0.03125,
                right: 0.96875,
                top: 0.03125,
                width: 32
            )
        );
    }

    private static CanaryManifest AudioManifest(params string[] requirements) {
        var leg = new CanaryLeg(
            Assertions: [],
            Authorities: [],
            AuthorityWorldPath: null,
            Commands: [],
            Connect: false,
            Name: "positive",
            ScriptPath: "positive.script.txt",
            WorldPath: "world.json"
        );

        return new CanaryManifest(
            Backends: [],
            Binding: "audio",
            BootShape: CanaryBootShape.Windowed,
            DirectoryPath: "audio",
            Discriminating: (leg with { Name = "discriminating" }),
            Fixtures: [],
            Id: "audio",
            Positive: leg,
            Requirements: requirements,
            TimeoutSeconds: 10,
            Title: "audio"
        );
    }

    [Fact]
    public void TheRunnerNamesAnUnsupportedAudioEnvironmentOnlyFromItsOwnAudioStateEcho() {
        const string NoDefaultEndpoint = "[audio.state: device=silent frames=0 rebinds=3 fillFaults=0 sources=0 voices=0 peak=0 droppedTriggers=0 emitters=1 fault=render endpoint could not be opened: 0x80070490 a WASAPI call failed]";
        const string NoPlatformBackend = "[audio.state: device=unsupported frames=0 rebinds=0 fillFaults=0 sources=0 voices=0 peak=0 droppedTriggers=0 emitters=1 fault=none]";
        const string Playing = "[audio.state: device=playing frames=512 rebinds=0 fillFaults=0 sources=1 voices=1 peak=0 droppedTriggers=0 emitters=1 fault=none]";

        // A machine with no default render endpoint is unsupported, not failed.
        Assert.Equal(
            expected: NoDefaultEndpoint,
            actual: CanaryCommand.AudioUnsupportedReason(
                manifest: AudioManifest("gpu", "audio-output"),
                transcript: Transcript(
                    runDirectory: m_root,
                    stdout: [NoDefaultEndpoint]
                )
            )
        );
        // So is a platform with no render backend at all.
        Assert.Equal(
            expected: NoPlatformBackend,
            actual: CanaryCommand.AudioUnsupportedReason(
                manifest: AudioManifest("audio-output"),
                transcript: Transcript(
                    runDirectory: m_root,
                    stdout: [NoPlatformBackend]
                )
            )
        );
        // A device that opened but produced no signal is a real defect, never unsupported.
        Assert.Null(@object: CanaryCommand.AudioUnsupportedReason(
            manifest: AudioManifest("audio-output"),
            transcript: Transcript(
                runDirectory: m_root,
                stdout: [Playing]
            )
        ));
        // A leg that never declared the capability is judged on its own merits even with no device.
        Assert.Null(@object: CanaryCommand.AudioUnsupportedReason(
            manifest: AudioManifest("gpu"),
            transcript: Transcript(
                runDirectory: m_root,
                stdout: [NoDefaultEndpoint]
            )
        ));
        // A device lost mid-stream (device=rebinding) is a real defect, never unsupported — only a device that
        // never opened at all is.
        Assert.Null(@object: CanaryCommand.AudioUnsupportedReason(
            manifest: AudioManifest("audio-output"),
            transcript: Transcript(
                runDirectory: m_root,
                stdout: ["[audio.state: device=rebinding frames=100 rebinds=1 fillFaults=0 sources=0 voices=0 peak=0 droppedTriggers=0 emitters=1 fault=stream invalidated]"]
            )
        ));
    }
    [Fact]
    public void TheRunnerNamesAnUnsupportedEnvironmentOnlyFromItsAnnouncement() {
        const string Host = "[world.host: unsupported: vulkan device unavailable: no adapter]";

        Assert.Equal(
            expected: Host,
            actual: CanaryCommand.UnsupportedReason(
                exitCode: 2,
                transcript: Transcript(
                    runDirectory: m_root,
                    stderr: [Host]
                )
            )
        );
        // The exit code alone, or the line under another exit code, is not the announcement.
        Assert.Null(@object: CanaryCommand.UnsupportedReason(
            exitCode: 2,
            transcript: Transcript(runDirectory: m_root)
        ));
        Assert.Null(@object: CanaryCommand.UnsupportedReason(
            exitCode: 1,
            transcript: Transcript(
                runDirectory: m_root,
                stderr: [Host]
            )
        ));
        Assert.NotNull(@object: CanaryCommand.UnsupportedReason(
            exitCode: 0,
            transcript: Transcript(
                runDirectory: m_root,
                stderr: ["[pipeline: ink unsupported: The shader tool 'dxc' was not found.]"]
            )
        ));
        Assert.NotNull(@object: CanaryCommand.UnsupportedReason(
            exitCode: 0,
            transcript: Transcript(
                runDirectory: m_root,
                stderr: ["[pipeline: ink wait compiled unsupported: The shader tool 'dxc' was not found.]"]
            )
        ));
        Assert.Null(@object: CanaryCommand.UnsupportedReason(
            exitCode: 0,
            transcript: Transcript(
                runDirectory: m_root,
                stderr: ["[pipeline: ink wait compiled failed: failed pass 'visualize']"]
            )
        ));
    }
    [Fact]
    public void EveryArmedPipelineWaitMustResolveWithoutRunningOutItsDeadline() {
        var reached = CanaryCommand.PipelineWaitInvariants(transcript: Transcript(
            runDirectory: m_root,
            stderr: ["[pipeline: feedback wait installed reached]", "[pipeline: feedback wait submitted 2 failed: candidate refused]", "[pipeline: feedback wait counted 1 reached]", "[pipeline: feedback wait resized 32 32 reached]"],
            stdout: ["[pipeline.wait: feedback installed armed for at most 10s]", "[pipeline.wait: feedback submitted 2 armed for at most 10s]", "[pipeline.wait: feedback counted 1 armed for at most 10s]", "[pipeline.wait: feedback resized 32 32 armed for at most 10s]"]
        ));

        Assert.Equal(
            expected: 5,
            actual: reached.Count
        );

        Assert.All(
            collection: reached,
            action: static result => Assert.True(
                condition: result.Passed,
                userMessage: result.Detail
            )
        );
        var timedOut = CanaryCommand.PipelineWaitInvariants(transcript: Transcript(
            runDirectory: m_root,
            stderr: ["[pipeline: feedback wait submitted 2 timed out after 10s: frames=1 ready=true]"],
            stdout: ["[pipeline.wait: feedback submitted 2 armed for at most 10s]"]
        ));

        Assert.Contains(
            collection: timedOut,
            filter: static result => (!result.Passed && result.Detail.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "submitted 2: timed out"
            ))
        );
        var resizeTimedOut = CanaryCommand.PipelineWaitInvariants(transcript: Transcript(
            runDirectory: m_root,
            stderr: ["[pipeline: feedback wait resized 32 32 timed out after 10s: requested=64x64 extent=64x64]"],
            stdout: ["[pipeline.wait: feedback resized 32 32 armed for at most 10s]"]
        ));

        Assert.Contains(
            collection: resizeTimedOut,
            filter: static result => (!result.Passed && result.Detail.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "resized 32 32: timed out"
            ))
        );
        var unresolved = CanaryCommand.PipelineWaitInvariants(transcript: Transcript(
            runDirectory: m_root,
            stdout: ["[pipeline.wait: feedback captured armed for at most 10s]"]
        ));

        Assert.Contains(
            collection: unresolved,
            filter: static result => !result.Passed
        );
        Assert.Empty(collection: CanaryCommand.PipelineWaitInvariants(transcript: Transcript(runDirectory: m_root)));
    }
}
