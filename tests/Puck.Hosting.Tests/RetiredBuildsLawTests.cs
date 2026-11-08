using Puck.Testing;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="RetiredBuilds"/>: retiring a canceled build never waits for it, its result is discarded once it
/// finishes, and a drain waits out every build still running and reports a discard that failed.
/// </summary>
public sealed class RetiredBuildsLawTests {
    [Fact]
    public void ARetiredBuildReturnsAtOnceAndIsDiscardedOnceItFinishes() {
        var build = new BackgroundBuild<object>();
        var retired = new RetiredBuilds();
        var candidate = new object();
        using var creating = new ManualResetEventSlim(initialState: false);
        using var created = new ManualResetEventSlim(initialState: false);
        using var discarded = new ManualResetEventSlim(initialState: false);
        object? received = null;

        // A creation already in the driver, which a cancel cannot interrupt.
        build.Start(build: _ => {
            creating.Set();
            Assert.True(condition: created.Wait(timeout: TestLiveness.Bound));

            return candidate;
        });
        Assert.True(condition: creating.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken), userMessage: "The build never started.");

        try {
            retired.Retire(build: build.Detach(), discard: value => {
                received = value;
                discarded.Set();
            });

            Assert.False(condition: build.IsPending);
            Assert.False(condition: discarded.IsSet);
            Assert.Equal(expected: 1, actual: retired.Running);
        } finally {
            created.Set();
        }

        Assert.True(condition: discarded.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken), userMessage: "The retired result was never discarded.");
        retired.Drain();
        Assert.Same(expected: candidate, actual: received);
        Assert.Equal(expected: 0, actual: retired.Running);
    }
    [Fact]
    public async Task ADrainWaitsOutARunningBuildAndItsDiscard() {
        var build = new BackgroundBuild<object>();
        var retired = new RetiredBuilds();
        using var created = new ManualResetEventSlim(initialState: false);
        var discarded = 0;

        build.Start(build: _ => {
            Assert.True(condition: created.Wait(timeout: TestLiveness.Bound));

            return new object();
        });
        retired.Retire(build: build.Detach(), discard: _ => Volatile.Write(location: ref discarded, value: 1));

        var drain = Task.Run(action: retired.Drain, cancellationToken: TestContext.Current.CancellationToken);

        await Task.Delay(millisecondsDelay: 50, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: drain.IsCompleted, userMessage: "The drain returned before the build finished.");
        created.Set();
        await drain.WaitAsync(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected: 1, actual: Volatile.Read(location: ref discarded));
        Assert.Equal(expected: 0, actual: retired.Running);
    }
    [Fact]
    public void ADrainReportsAFailedDiscardOnceAndAnEmptyHandleIsIgnored() {
        var build = new BackgroundBuild<object>();
        var retired = new RetiredBuilds();

        build.Start(build: static _ => new object());
        TestLiveness.Until(step: () => build.IsCompleted, reason: () => "the candidate never built", wait: build.WaitFinished);
        retired.Retire(build: build.Detach(), discard: static _ => throw new InvalidOperationException(message: "The discard failed."));
        retired.Retire(build: build.Detach(), discard: static _ => throw new InvalidOperationException(message: "An empty handle has nothing to discard."));

        var failure = Assert.Throws<InvalidOperationException>(testCode: retired.Drain);

        Assert.Equal(expected: "The discard failed.", actual: failure.InnerException?.Message);
        retired.Drain();
    }
}
