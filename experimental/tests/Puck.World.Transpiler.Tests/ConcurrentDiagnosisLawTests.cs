using System.Text;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The stdio language server diagnoses on the thread pool while its read loop compiles for hover and
/// completion, so every cache both reach is shared between threads: the composed-image cache, the compile cache
/// (<see cref="Composition.WorldCompileCache"/>, whose own races are held in
/// <see cref="WorldCompileCacheConcurrencyLawTests"/>), and the vocabulary and schema tables. These laws run the two
/// side by side, many times over, and hold every result to what the same work produces alone; and they hold the host
/// to ending a session only once the diagnosis it started has returned.</summary>
public sealed class ConcurrentDiagnosisLawTests {
    private static readonly string[] Sources = [
        "worlds/parlor/chess.puck",
        "worlds/parlor/hearts.puck",
        "src/Puck.World.Transpiler/Samples/state-declarations.synthetic.world.puck",
        "src/Puck.World.Transpiler/Samples/comprehensive.synthetic.world.puck",
    ];

    // What a diagnosis reports, and what the loop's own compile (imports ignored, as hover and completion compile)
    // lowers the source to.
    private static (string Diagnosis, string Lowered) Run(string path, string text) => (
        string.Join(separator: "\n", values: WorldSourceDiagnostics.Diagnose(cancellationToken: TestContext.Current.CancellationToken, source: text, sourcePath: path).Select(selector: static diagnostic => diagnostic.Format())),
        (WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, imports: ImportHandling.Ignore, source: text, sourcePath: path).Json?.ToJsonString() ?? "")
    );

    [Fact]
    public void DiagnosesAndCompilesSideBySideAgreeWithTheSameWorkAlone() {
        var inputs = Sources.Select(selector: static relative => {
            var path = RepositoryPaths.Resolve(relativePath: relative);

            return (Path: path, Text: File.ReadAllText(path: path));
        }).ToArray();
        var alone = inputs.Select(selector: static input => Run(path: input.Path, text: input.Text)).ToArray();
        var rounds = (inputs.Length * 6);
        var mismatches = new StringBuilder();
        var runs = 0;

        Parallel.For(
            body: round => {
                var index = (round % inputs.Length);
                var result = Run(path: inputs[index].Path, text: inputs[index].Text);

                _ = Interlocked.Increment(location: ref runs);

                if (result != alone[index]) {
                    lock (mismatches) {
                        _ = mismatches.AppendLine(value: $"{Sources[index]} (round {round}) differs from its run alone.");
                    }
                }
            },
            fromInclusive: 0,
            parallelOptions: new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken, MaxDegreeOfParallelism = 8 },
            toExclusive: rounds
        );

        Assert.Equal(actual: runs, expected: rounds);
        Assert.True(condition: (mismatches.Length == 0), userMessage: mismatches.ToString());
    }
}
