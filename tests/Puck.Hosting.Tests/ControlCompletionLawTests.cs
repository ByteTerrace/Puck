namespace Puck.Hosting.Tests;

/// <summary>
/// A local control server's <see cref="LocalControlServer.Completion"/> completes only after every worker it owns has
/// returned: the accept loop and each connection's serving task with the session that task created. Disposing the
/// server begins the stop and leaves the wait to <see cref="LocalControlServer.Completion"/> and
/// <see cref="LocalControlServer.DisposeAsync"/>.
/// </summary>
public sealed class ControlCompletionLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // The session of a connection: its disposal, which the serving task performs as it ends, parks until released.
    private sealed class ParkedSession(TaskCompletionSource disposing, ManualResetEventSlim release) : IControlSession {
        public void Dispose() {
            disposing.TrySetResult();
            release.Wait();
        }
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => Task.FromResult(result: new ControlResponse(
            request.Id,
            "completed",
            (request.Command ?? "capture")
        ));
    }

    [Fact]
    public async Task CompletionWaitsForAServingTaskStillClosingItsSession() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var disposing = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(initialState: false);
        var server = new LocalControlServer(createSession: () => new ParkedSession(disposing: disposing, release: release));

        try {
            using var client = await LocalControlClient.ConnectAsync(
                attachmentPath: server.AttachmentPath,
                cancellationToken: Token
            );

            Assert.Equal(
                expected: "ping",
                actual: (await client.ExecuteAsync(
                    "exec",
                    "ping",
                    cancellationToken: Token
                )).Output
            );
            server.Dispose();
            await disposing.Task.WaitAsync(cancellationToken: Token);

            // Control: the stop has begun and its sockets and capability file are gone, so only the parked worker remains.
            Assert.False(condition: File.Exists(path: server.AttachmentPath));
            Assert.False(condition: server.Completion.IsCompleted, userMessage: "Completion reported a serving task that is still running as finished.");
            Assert.False(condition: server.DisposeAsync().AsTask().IsCompleted, userMessage: "DisposeAsync returned while a serving task was still running.");
        } finally {
            release.Set();
        }

        await server.Completion.WaitAsync(cancellationToken: Token);
        Assert.True(condition: server.Completion.IsCompletedSuccessfully);
    }
    [Fact]
    public async Task CompletionOfAServerNeverDisposedStaysPending() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var server = new LocalControlServer(createSession: () => throw new InvalidOperationException(message: "No connection is made."));

        try {
            await Task.Delay(
                cancellationToken: Token,
                delay: TimeSpan.FromMilliseconds(value: 100)
            );
            Assert.False(condition: server.Completion.IsCompleted);
        } finally {
            await server.DisposeAsync();
        }

        Assert.True(condition: server.Completion.IsCompletedSuccessfully);
    }
}
