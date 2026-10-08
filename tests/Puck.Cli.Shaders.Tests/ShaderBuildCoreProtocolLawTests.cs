using System.Collections.Concurrent;
using System.Text;
using Puck.Shaders;
using Xunit;

namespace Puck.Cli.Shaders.Tests;

public sealed class ShaderBuildCoreProtocolLawTests {
    [Fact]
    public async Task AClosedCoreBudgetStillReceivesHostCancellation() {
        using var answers = new CoreAnswers();
        using var requests = new StringWriter();
        var cancelled = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new StreamShaderCoreBroker(answers: answers, requests: requests, cancel: () => cancelled.TrySetResult());
        var request = broker.RequestAsync(count: 1, cancellationToken: TestContext.Current.CancellationToken);

        answers.Send(line: "closed");
        _ = await Assert.ThrowsAsync<IOException>(testCode: () => request.WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 10), cancellationToken: TestContext.Current.CancellationToken));
        answers.Send(line: "cancel");
        await cancelled.Task.WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 10), cancellationToken: TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task AnAlreadyCancelledRequestTakesNoCoreAndWritesNoRequest() {
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();
        var fixedBudget = new FixedShaderCoreBroker(cores: 1);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => fixedBudget.RequestAsync(count: 1, cancellationToken: cancellation.Token));
        Assert.Equal(expected: 0, actual: fixedBudget.Held);

        using var answers = new CoreAnswers();
        using var requests = new StringWriter();
        var streamed = new StreamShaderCoreBroker(answers: answers, requests: requests);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => streamed.RequestAsync(count: 1, cancellationToken: cancellation.Token));
        Assert.Equal(expected: string.Empty, actual: requests.ToString());
    }
    [Fact]
    public async Task AFailedRequestWriteEndsEveryPendingRequest() {
        using var answers = new CoreAnswers();
        using var requests = new FailingRequests();
        var broker = new StreamShaderCoreBroker(answers: answers, requests: requests);
        var first = broker.RequestAsync(count: 1, cancellationToken: TestContext.Current.CancellationToken);

        requests.Fail = true;
        Task<int>? second = null;
        var writeFailure = Record.Exception(testCode: () => { second = broker.RequestAsync(count: 1, cancellationToken: TestContext.Current.CancellationToken); });

        Assert.Null(@object: writeFailure);
        _ = await Assert.ThrowsAsync<IOException>(testCode: () => first.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 10)));
        _ = await Assert.ThrowsAsync<IOException>(testCode: () => second!);
        _ = await Assert.ThrowsAsync<IOException>(testCode: () => broker.RequestAsync(count: 1, cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class CoreAnswers : TextReader {
        private readonly BlockingCollection<string> m_lines = [];

        public void Send(string line) => m_lines.Add(item: line);
        public override string? ReadLine() {
            try { return m_lines.Take(); } catch (InvalidOperationException) { return null; }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) { m_lines.CompleteAdding(); m_lines.Dispose(); }
            base.Dispose(disposing: disposing);
        }
    }
    private sealed class FailingRequests : TextWriter {
        public override Encoding Encoding => Encoding.UTF8;
        public bool Fail { get; set; }

        public override void WriteLine(string? value) {
            if (Fail) { throw new IOException(message: "deliberate broken request pipe"); }
        }
    }
}
