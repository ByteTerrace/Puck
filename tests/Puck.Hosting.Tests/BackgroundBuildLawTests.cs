using Puck.Testing;

namespace Puck.Hosting.Tests;

public sealed class BackgroundBuildLawTests {
    [Fact]
    public void CancelKeepsACompletedBuildUntilItsCallbacksFinish() {
        var build = new BackgroundBuild<object>();
        var candidate = new object();
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);
        using var discarded = new ManualResetEventSlim(initialState: false);
        var callbackFinished = 0;
        var finishedAtDiscard = 0;
        object? received = null;

        build.Start(build: token => {
            _ = token.Register(callback: () => {
                entered.Set();
                Assert.True(condition: release.Wait(timeout: TestLiveness.Bound));
                Volatile.Write(location: ref callbackFinished, value: 1);
            });

            return candidate;
        });
        TestLiveness.Until(step: () => build.IsCompleted, reason: () => "the candidate never built", wait: build.WaitFinished);

        try {
            build.Cancel(discard: value => {
                received = value;
                finishedAtDiscard = Volatile.Read(location: ref callbackFinished);
                discarded.Set();
            });
            Assert.False(condition: discarded.IsSet);
            Assert.True(condition: entered.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken), userMessage: "The cancellation callback never ran.");
        } finally {
            release.Set();
        }

        Assert.True(condition: discarded.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken), userMessage: "The canceled candidate was never discarded.");
        Assert.Same(actual: received, expected: candidate);
        Assert.Equal(actual: finishedAtDiscard, expected: 1);
    }
    [Fact]
    public void CancelAndWaitObservesThrowingCallbacksBeforeDiscarding() => OnOwnerThread(action: () => {
        var build = new BackgroundBuild<object>();
        var called = 0;
        var discarded = 0;

        build.Start(build: token => {
            _ = token.Register(callback: () => {
                Interlocked.Increment(location: ref called);

                throw new InvalidOperationException(message: "The cancellation callback failed.");
            });

            return new object();
        });
        TestLiveness.Until(step: () => build.IsCompleted, reason: () => "the candidate never built", wait: build.WaitFinished);
        build.CancelAndWait(discard: _ => discarded++);

        Assert.Equal(actual: called, expected: 1);
        Assert.Equal(actual: discarded, expected: 1);
    });
    [Fact]
    public void CancelAndWaitResumesAnAsyncBuildAwayFromTheOwner() => OnOwnerThread(action: () => {
        var build = new BackgroundBuild<object>();
        using var entered = new ManualResetEventSlim(initialState: false);
        var pending = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedOn = 0;
        var owner = Environment.CurrentManagedThreadId;

        build.Start(build: async token => {
            try {
                entered.Set();
                await pending.Task.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                return new object();
            } finally {
                resumedOn = Environment.CurrentManagedThreadId;
            }
        });
        Assert.True(condition: entered.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken), userMessage: "The asynchronous build never started.");
        build.CancelAndWait();

        Assert.NotEqual(actual: resumedOn, expected: 0);
        Assert.NotEqual(actual: resumedOn, expected: owner);
    });

    private static void OnOwnerThread(Action action) {
        var owner = Task.Factory.StartNew(
            action: action,
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

        Assert.True(condition: owner.Wait(timeout: TestLiveness.Bound), userMessage: "The owner's cancellation never finished.");
    }
}
