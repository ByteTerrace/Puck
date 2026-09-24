using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
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
    private const string ForeignSchema = "puck.custom.v1";

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
    private static Task SendAsync(Stream input, JsonObject message) => LspFraming.WriteAsync(
        cancellationToken: TestContext.Current.CancellationToken,
        json: message.ToJsonString(),
        stream: input
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
    // A diagnosis can hold work it cannot abandon midway, such as a compile cache's write, and a host exits as soon as
    // the session returns; so the session closing must wait for the diagnosis rather than leave it running.
    [Fact]
    public async Task TheStdioHostDoesNotReturnWhileADiagnosisRuns() {
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var returned = 0;
        var server = new PuckLanguageServer(
            diagnoseDocument: (_, _, _) => {
                entered.Set();
                gate.Wait(cancellationToken: TestContext.Current.CancellationToken);
                Volatile.Write(location: ref returned, value: 1);

                return true;
            },
            vocabularyResolver: InertVocabulary.ResolverFor(schema: ForeignSchema)
        );
        using var client = new AnonymousPipeServerStream(direction: PipeDirection.Out);
        using var input = new AnonymousPipeClientStream(direction: PipeDirection.In, pipeHandleAsString: client.GetClientHandleAsString());
        using var output = new MemoryStream();
        var session = server.RunAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            input: input,
            output: output
        );

        try {
            await SendAsync(input: client, message: LanguageServerClient.Notification(
                method: "textDocument/didOpen",
                @params: new JsonObject {
                    ["textDocument"] = new JsonObject {
                        ["uri"] = LanguageServerClient.DefaultUri,
                        ["languageId"] = "puck",
                        ["version"] = 1,
                        ["text"] = $"schema: \"{ForeignSchema}\"\n",
                    },
                }
            ));
            Assert.True(condition: entered.Wait(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromMinutes(minutes: 1)));
            await SendAsync(input: client, message: LanguageServerClient.Notification(method: "exit"));

            while (server.IsRunning) {
                await Task.Delay(cancellationToken: TestContext.Current.CancellationToken, millisecondsDelay: 10);
            }

            // The session has closed; however long this wait allows, the session must not return with the diagnosis
            // still held.
            _ = await Task.WhenAny(task1: session, task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, millisecondsDelay: 250));
            Assert.False(condition: session.IsCompleted);
        } finally {
            gate.Set();
        }

        await session;
        Assert.Equal(actual: Volatile.Read(location: ref returned), expected: 1);
        // Ends the input, so the reader the session left behind finishes before its stream is disposed.
        client.Dispose();
    }
}
