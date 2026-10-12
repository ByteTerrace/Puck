using System.IO.Pipes;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
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

    private static Task SendAsync(Stream input, JsonObject message) => LspFraming.WriteAsync(
        cancellationToken: TestContext.Current.CancellationToken,
        json: message.ToJsonString(),
        stream: input
    );

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
