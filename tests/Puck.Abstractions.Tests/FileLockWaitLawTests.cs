using Puck.Confinement;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

// Both openers must keep one exclusive holder, stop at expiry/cancellation, and acquire after release.
public sealed class FileLockWaitLawTests {
    [Fact]
    public async Task TheAsyncWaitUsesTheSameExpiryCancellationAndNotificationPolicy() {
        using var scratch = new TemporaryDirectory(prefix: "puck-lock-wait-async-");
        var path = Path.Combine(path1: scratch.RootPath, path2: "lock");
        using var holder = FileLock.TryAcquire(path: path)!;
        var announcements = 0;

        Assert.Null(@object: await FileLock.AcquireAsync(path: path, expired: () => true, cancellationToken: TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource();

        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(testCode: () => FileLock.AcquireAsync(path: path, cancellationToken: canceled.Token));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 10));
        var pending = FileLock.AcquireAsync(path: path, waiting: () => announcements++, cancellationToken: deadline.Token);

        try {
            Assert.Equal(actual: announcements, expected: 1);
            Assert.False(condition: pending.IsCompleted);
            holder.Dispose();
            using var acquired = await pending;

            Assert.NotNull(@object: acquired);
            Assert.Equal(actual: announcements, expected: 1);
            Assert.Null(@object: FileLock.TryAcquire(path: path));
        } finally {
            deadline.Cancel();
            try { using var abandoned = await pending; } catch (OperationCanceledException) { }
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ContentionExpiresCancelsAndAcquiresAfterRelease(bool confined) {
        using var scratch = new TemporaryDirectory(prefix: "puck-lock-wait-");
        using var directory = ConfinedDirectory.OpenAnchor(path: scratch.RootPath, create: false)!;
        var path = Path.Combine(path1: scratch.RootPath, path2: "lock");

        FileStream? TryAcquire() => (confined ? directory.TryAcquireLock(name: "lock") : FileLock.TryAcquire(path: path));
        FileStream? Acquire(bool expired, CancellationToken token) => (confined
            ? directory.AcquireLock(cancellationToken: token, name: "lock", timeout: (expired ? TimeSpan.Zero : Timeout.InfiniteTimeSpan))
            : FileLock.Acquire(path: path, expired: () => expired, cancellationToken: token));

        using var holder = TryAcquire()!;

        Assert.NotNull(@object: holder);
        Assert.Null(@object: TryAcquire());
        if (confined) { Assert.Throws<IOException>(testCode: () => Acquire(expired: true, token: TestContext.Current.CancellationToken)); } else { Assert.Null(@object: Acquire(expired: true, token: TestContext.Current.CancellationToken)); }

        using var canceled = new CancellationTokenSource();

        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(testCode: () => Acquire(expired: false, token: canceled.Token));

        var waiting = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 10));
        var pending = Task.Run(() => FileLockWait.Acquire(
            open: TryAcquire, waiting: () => waiting.SetResult(), cancellationToken: deadline.Token), deadline.Token);

        try {
            await waiting.Task.WaitAsync(cancellationToken: deadline.Token);
            Assert.False(condition: pending.IsCompleted);
            holder.Dispose();
            using var acquired = await pending;

            Assert.NotNull(@object: acquired);
            Assert.Null(@object: TryAcquire());
        } finally {
            deadline.Cancel();
            try { using var abandoned = await pending; } catch (OperationCanceledException) { }
        }
        // The public wrapper also tries before its deadline, so an immediately available lock wins even at zero.
        using var immediate = Acquire(expired: true, token: TestContext.Current.CancellationToken);

        Assert.NotNull(@object: immediate);
    }
}
