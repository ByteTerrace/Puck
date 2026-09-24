using Puck.Cli.Bench;
using Puck.State;

using Xunit;

namespace Puck.Cli.Tests;

public sealed class StateEvidenceCommandTests {
    [InlineData("LLVM version 19.1.6\n", true, "19.1.6")]
    [InlineData("clang version 19.1.6\nLLVM version 19.1.60\n", true, "19.1.60")]
    [InlineData("LLVM version 19.1.6 release\n", false, "19.1.6 release")]
    [InlineData("llvm-mca 19.1.6\n", false, "")]
    [Theory]
    public void LlvmVersionIsReadAsOneExactToken(string output, bool expectedSuccess, string expectedVersion) {
        Assert.Equal(expectedSuccess, StateEvidenceCommand.TryReadLlvmVersion(output: output, version: out var version));
        Assert.Equal(actual: version, expected: expectedVersion);
    }
    [Fact]
    public void InstructionRowsAreReadWithoutMistakingSummaryOrResourceRowsForEvidence() {
        const string Output = """
            Iterations:        1
            Instructions:      2
            Total Cycles:      4

            Instruction Info:
            [1]: #uOps
            [2]: Latency
            [3]: RThroughput

            [1]    [2]    [3]    [4]    [5]    [6]    Instructions:
             1      1     0.25                        addq %rbx, %rax
             2      3     1.50                 U      callq 0

            Resource pressure per iteration:
            [0]    [1]
             -     2.00

            Resource pressure by instruction:
            [0]    [1]    Instructions:
            12.00  3.00   addq %rbx, %rax
            """;

        Assert.Equal(
            [
                new StateEvidenceCommand.McaRow(Latency: 1L, ReciprocalThroughput: 0.25m),
                new StateEvidenceCommand.McaRow(Latency: 3L, ReciprocalThroughput: 1.50m),
            ],
            StateEvidenceCommand.ParseRows(output: Output)
        );
    }
    [Fact]
    public void MalformedInstructionRowsDoNotBecomeZeroCostEvidence() {
        const string Output = """
            [1]    [2]    [3]    Instructions:
             1      ?     0.25   mystery
             1      2     ?      mystery
            """;

        Assert.Empty(collection: StateEvidenceCommand.ParseRows(output: Output));
    }
    [Fact]
    public void InventoryKeepsMissingArtifactsAndCalibrationExplicit() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-state-evidence-");

        try {
            var inventory = StateEvidenceCommand.BuildInventory(repositoryRoot: directory.FullName);

            Assert.Equal(ReferenceScheduleManifest.Digest, inventory.ManifestDigest);
            Assert.All(inventory.Kernels.SelectMany(selector: static kernel => kernel.Sources), static source => {
                Assert.Null(@object: source.ActualSha256);
                Assert.False(condition: source.Matches);
            });
            Assert.Contains(collection: inventory.Kernels, filter: static kernel => (kernel.UnresolvedTargets.Count > 0));
            Assert.All(inventory.MemoryGaps, static memoryClass => Assert.NotEmpty(collection: memoryClass.Value));
            Assert.Equal(
                ReferenceSchedule.Coverage.Select(selector: static entry => (entry.Vocabulary, entry.Registered, entry.Priced, entry.Unmodeled.Count)),
                inventory.Coverage.Select(selector: static entry => (entry.Vocabulary, entry.Registered, entry.Priced, entry.Unmodeled.Count))
            );
            Assert.Contains(collection: inventory.Coverage, filter: static entry => (entry.Unmodeled.Count > 0));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public async Task CaptureRefusesAnAbsentPinnedCompilerByNameRatherThanSubstitutingOne() {
        var root = StateEvidenceCommand.RepositoryRoot;

        Assert.NotNull(@object: root);

        var directory = Directory.CreateTempSubdirectory(prefix: "puck-state-capture-");
        string text;

        try {
            var (exitCode, _, refusal) = await ConsoleCapture.RunSplitAsync(run: () => ReferenceCapture.RunAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                clock: TimeProvider.System,
                directory: directory.FullName,
                options: new(
                    Ilc: Path.Combine(path1: directory.FullName, path2: "no-such-ilc.exe"),
                    LlvmMca: null,
                    LlvmObjdump: null,
                    NuGetPackages: null,
                    ReuseLowering: false
                ),
                repositoryRoot: root
            ));

            Assert.Equal(actual: exitCode, expected: 2);
            Assert.Empty(collection: Directory.GetFiles(path: directory.FullName));
            text = refusal;
        } finally {
            directory.Delete(recursive: true);
        }

        Assert.Contains(ReferenceScheduleManifest.Targets[0].Build.IlCompiler, text, StringComparison.Ordinal);
        Assert.Contains(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "no-such-ilc.exe");
        Assert.Contains(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "--ilc");
    }
}
