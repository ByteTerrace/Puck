using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The shared state directory tears a law down in one fixed order: its disposal shuts down what the law gave it, within
/// a bound, and a passing verdict then waits for any handle that shutdown left open, deletes, and fails the law naming a
/// file something wrote after the owners returned. Each law drives the verdict itself through
/// <see cref="TemporaryDirectory.Conclude"/>, after <see cref="TemporaryDirectory.Dispose"/>, so it observes the delete
/// inside its own body. Each claim has a control that shows the fixture does not fail a clean teardown.
/// </summary>
public sealed class TemporaryDirectoryLawTests {
    private const string Late = "late.bin";

    // Long enough that a loaded machine starts the teardown worker and reaches its first step well inside it.
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(value: 3);

    // An owner whose disposal returns at once and leaves a worker running, the way a background build that was cancelled
    // but not joined does: the worker holds a file open and releases it after a while or, given a signal, writes to it
    // once the signal is set and then releases it. The signalled write lands after the owners returned and before the
    // handle closes, and the directory reads the file only once the handle has closed, so that read always sees it.
    private sealed class Straggler(FileStream held, ManualResetEventSlim? write = null) : IDisposable {
        public void Dispose() => _ = Task.Factory.StartNew(
            action: () => {
                if (write is null) {
                    Thread.Sleep(millisecondsTimeout: 300);
                } else if (write.Wait(timeout: TestLiveness.Bound)) {
                    held.Write(buffer: new byte[64]);
                    held.Flush();
                }

                held.Dispose();
            },
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );
    }
    // An owner whose disposal is asynchronous: it finishes after a delay, and only then has it released its marker.
    private sealed class AsynchronousOwner(string marker) : IAsyncDisposable, IDisposable {
        public string Observed { get; private set; } = "never disposed";
        public bool SynchronousDisposalCalled { get; private set; }

        public void Dispose() => SynchronousDisposalCalled = true;
        public async ValueTask DisposeAsync() {
            await Task.Delay(delay: TimeSpan.FromMilliseconds(value: 200));
            Observed = $"finished:{File.Exists(path: marker)}";
        }
    }
    private sealed class Recorder(List<string> order, string name, string marker) : IDisposable {
        public void Dispose() => order.Add(item: $"{name}:{File.Exists(path: marker)}");
    }
    private sealed class BlockingOwner : IDisposable {
        public ManualResetEventSlim Entered { get; } = new(initialState: false);
        public ManualResetEventSlim Release { get; } = new(initialState: false);
        public ManualResetEventSlim Returned { get; } = new(initialState: false);

        public void Dispose() {
            Entered.Set();
            Release.Wait();
            Returned.Set();
        }
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ABlockingOwnerCannotHangTeardownOrLetItDeleteItsFiles(bool nested) {
        var state = new TemporaryDirectory(prefix: "puck-fixture-blocked-", teardownBound: TeardownBound);
        var order = new List<string>();
        var marker = state.WriteText(name: "marker.txt", text: "x");

        _ = state.Own(owner: new Recorder(marker: marker, name: "dependency", order: order));
        var owner = new BlockingOwner();
        TemporaryDirectory? child = null;

        if (nested) {
            child = state.Own(owner: new TemporaryDirectory(prefix: "puck-fixture-blocked-child-"));
            _ = child.Own(owner: owner);
        } else {
            _ = state.Own(owner: owner);
        }
        var teardown = Task.Factory.StartNew(
            function: () => Record.Exception(testCode: state.Dispose),
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

        try {
            Assert.True(condition: owner.Entered.Wait(cancellationToken: TestContext.Current.CancellationToken, timeout: TestLiveness.Bound), userMessage: "the blocking owner was not reached");
            var finished = await Task.WhenAny(task1: teardown, task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TestLiveness.Bound));

            Assert.Same(actual: finished, expected: teardown);
            var failure = Assert.IsType<TimeoutException>(@object: await teardown);

            Assert.Contains(expectedSubstring: (nested ? nameof(TemporaryDirectory) : nameof(BlockingOwner)), actualString: failure.Message);
            Assert.True(condition: File.Exists(path: marker));
            Assert.Empty(collection: order);

            // A passing verdict after a teardown that timed out deletes nothing either.
            state.Conclude(passed: true);
            Assert.True(condition: File.Exists(path: marker));
        } finally {
            owner.Release.Set();
            Assert.True(condition: owner.Returned.Wait(cancellationToken: TestContext.Current.CancellationToken, timeout: TestLiveness.Bound));
            _ = await teardown;
            if ((child is not null) && Directory.Exists(path: child.RootPath)) {
                Directory.Delete(path: child.RootPath, recursive: true);
            }
            if (Directory.Exists(path: state.RootPath)) {
                Directory.Delete(path: state.RootPath, recursive: true);
            }
        }
    }

    private static FileStream Hold(TemporaryDirectory state, FileShare share = FileShare.Read) => new(
        access: FileAccess.Write,
        mode: FileMode.Create,
        path: state.PathOf(name: Late),
        share: share
    );

    [Fact]
    public async Task ADeleteThatNeverSucceedsNamesItsLastErrorAndRemainingFile() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "an open handle blocks a delete only where the file system refuses it.");
        var state = new TemporaryDirectory(prefix: "puck-fixture-never-delete-", teardownBound: TeardownBound);
        using var held = Hold(state: state);
        // The directory reads each file once no writer holds it before it deletes anything, so its last error is that
        // read's refusal.
        var expected = Assert.Throws<IOException>(testCode: () => new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: state.PathOf(name: Late), share: FileShare.Read));
        var teardown = Task.Factory.StartNew(
            function: () => Record.Exception(testCode: () => {
                state.Dispose();
                state.Conclude(passed: true);
            }),
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

        try {
            var finished = await Task.WhenAny(task1: teardown, task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TestLiveness.Bound));

            Assert.Same(actual: finished, expected: teardown);
            var failure = Assert.IsType<TimeoutException>(@object: await teardown);

            Assert.Contains(expectedSubstring: expected.Message, actualString: failure.Message);
            Assert.Contains(expectedSubstring: $"Still present: {Late}", actualString: failure.Message);
        } finally {
            held.Dispose();
            _ = await teardown;
            if (Directory.Exists(path: state.RootPath)) {
                Directory.Delete(path: state.RootPath, recursive: true);
            }
        }
    }
    [Fact]
    public void AnOwnerWithAnAsynchronousDisposalIsAwaitedBeforeAnythingIsDeleted() {
        var state = new TemporaryDirectory(prefix: "puck-fixture-async-");
        var marker = state.WriteText(name: "marker.txt", text: "x");
        var owner = new AsynchronousOwner(marker: marker);

        _ = state.Own(owner: owner);
        state.Dispose();
        state.Conclude(passed: true);

        Assert.Equal(actual: owner.Observed, expected: "finished:True");
        Assert.False(condition: owner.SynchronousDisposalCalled);
        Assert.False(condition: Directory.Exists(path: state.RootPath));
    }
    [Fact]
    public void AFileHeldExclusivelyAndNeverWrittenIsNotAccusedOfBeingWritten() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "an open handle blocks a delete only where the file system refuses it.");
        var state = new TemporaryDirectory(prefix: "puck-fixture-exclusive-");

        _ = state.Own(owner: new Straggler(held: Hold(share: FileShare.None, state: state)));
        state.Dispose();
        state.Conclude(passed: true);

        Assert.False(condition: Directory.Exists(path: state.RootPath));
    }
    [Fact]
    public void OwnersAreDisposedLastRegisteredFirstAndBeforeAnythingIsDeleted() {
        var order = new List<string>();
        var state = new TemporaryDirectory(prefix: "puck-fixture-order-");
        var marker = state.WriteText(name: "marker.txt", text: "x");

        _ = state.Own(owner: new Recorder(marker: marker, name: "first", order: order));
        _ = state.Own(owner: new Recorder(marker: marker, name: "second", order: order));
        state.Dispose();
        state.Conclude(passed: true);

        Assert.Equal(actual: order, expected: ["second:True", "first:True"]);
        Assert.False(condition: Directory.Exists(path: state.RootPath));
    }
    [Fact]
    public void ADirectoryWaitsForAHandleAnOwnerLetsGoOfAfterItsDisposeReturns() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "an open handle blocks a delete only where the file system refuses it.");
        var state = new TemporaryDirectory(prefix: "puck-fixture-handle-");

        _ = state.Own(owner: new Straggler(held: Hold(state: state)));
        state.Dispose();
        state.Conclude(passed: true);

        Assert.False(condition: Directory.Exists(path: state.RootPath));
    }
    [Fact]
    public void AReaderThatSharesAFileDoesNotHoldUpThePassingDelete() {
        // An observer outside the law, such as an indexer or a scanner, holds a file open to read it and shares it for
        // reading, writing and deletion. It writes nothing, so the passing verdict reads the file beside it and deletes.
        var state = new TemporaryDirectory(prefix: "puck-fixture-reader-", teardownBound: TeardownBound);
        var path = state.WriteText(name: Late, text: "x");
        using var reader = new FileStream(
            access: FileAccess.Read,
            mode: FileMode.Open,
            path: path,
            share: (FileShare.ReadWrite | FileShare.Delete)
        );

        try {
            state.Dispose();
            state.Conclude(passed: true);

            Assert.False(condition: Directory.Exists(path: state.RootPath));
        } finally {
            reader.Dispose();
            if (Directory.Exists(path: state.RootPath)) {
                Directory.Delete(path: state.RootPath, recursive: true);
            }
        }
    }
    [Fact]
    public void ADirectoryNamesTheFileAnOwnersWorkerWroteAfterItsDisposeReturned() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "an open handle blocks a delete only where the file system refuses it.");
        var state = new TemporaryDirectory(prefix: "puck-fixture-stray-");
        using var write = new ManualResetEventSlim(initialState: false);

        _ = state.Own(owner: new Straggler(held: Hold(state: state), write: write));
        state.Dispose();
        write.Set();

        var failure = Assert.Throws<InvalidOperationException>(testCode: () => state.Conclude(passed: true));

        Assert.Contains(expectedSubstring: Late, actualString: failure.Message);
        Assert.False(condition: Directory.Exists(path: state.RootPath));
    }
}
