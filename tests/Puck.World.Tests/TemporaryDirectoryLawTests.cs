using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The shared state directory tears a law down in one fixed order: it disposes what the law gave it, waits for any
/// handle that disposal left open, deletes, and fails the law naming a file something wrote after the owners were
/// disposed. Each claim has a control that shows the fixture does not fail a clean teardown.
/// </summary>
public sealed class TemporaryDirectoryLawTests {
    private const string Late = "late.bin";

    // An owner whose disposal returns at once and leaves a worker running: the worker holds a file open and releases it
    // after a while, writing to it meanwhile when asked to, the way a background build that was cancelled but not joined
    // does.
    private sealed class Straggler(FileStream held, bool writes) : IDisposable {
        public void Dispose() => _ = Task.Run(function: async () => {
            var release = DateTime.UtcNow.AddMilliseconds(value: (writes ? 1200 : 300));

            while (DateTime.UtcNow < release) {
                await Task.Delay(delay: TimeSpan.FromMilliseconds(value: 25));
                if (writes) {
                    held.Write(buffer: new byte[64]);
                    held.Flush();
                }
            }

            held.Dispose();
        });
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
        var state = new TemporaryDirectory(prefix: "puck-fixture-blocked-", teardownBound: TimeSpan.FromMilliseconds(value: 200));
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
            Assert.True(condition: owner.Entered.Wait(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(value: 2)), userMessage: "the blocking owner was not reached");
            var finished = await Task.WhenAny(task1: teardown, task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TimeSpan.FromSeconds(value: 2)));

            Assert.Same(actual: finished, expected: teardown);
            var failure = Assert.IsType<TimeoutException>(@object: await teardown);

            Assert.Contains(expectedSubstring: (nested ? nameof(TemporaryDirectory) : nameof(BlockingOwner)), actualString: failure.Message);
            Assert.True(condition: File.Exists(path: marker));
            Assert.Empty(collection: order);
        } finally {
            owner.Release.Set();
            Assert.True(condition: owner.Returned.Wait(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(value: 2)));
            _ = await teardown;
            if ((child is not null) && Directory.Exists(path: child.RootPath)) {
                Directory.Delete(path: child.RootPath, recursive: true);
            }
            if (Directory.Exists(path: state.RootPath)) {
                Directory.Delete(path: state.RootPath, recursive: true);
            }
        }
    }

    private static FileStream Hold(TemporaryDirectory state) => new(
        access: FileAccess.Write,
        mode: FileMode.Create,
        path: state.PathOf(name: Late),
        share: FileShare.Read
    );

    [Fact]
    public async Task ADeleteThatNeverSucceedsNamesItsLastErrorAndRemainingFile() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "an open handle blocks a delete only where the file system refuses it.");
        var state = new TemporaryDirectory(prefix: "puck-fixture-never-delete-", teardownBound: TimeSpan.FromMilliseconds(value: 200));
        using var held = Hold(state: state);
        var expected = Assert.Throws<IOException>(testCode: () => Directory.Delete(path: state.RootPath, recursive: true));
        var teardown = Task.Factory.StartNew(
            function: () => Record.Exception(testCode: state.Dispose),
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

        try {
            var finished = await Task.WhenAny(task1: teardown, task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TimeSpan.FromSeconds(value: 2)));

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
    public void OwnersAreDisposedLastRegisteredFirstAndBeforeAnythingIsDeleted() {
        var order = new List<string>();
        var state = new TemporaryDirectory(prefix: "puck-fixture-order-");
        var marker = state.WriteText(name: "marker.txt", text: "x");

        _ = state.Own(owner: new Recorder(marker: marker, name: "first", order: order));
        _ = state.Own(owner: new Recorder(marker: marker, name: "second", order: order));
        state.Dispose();

        Assert.Equal(actual: order, expected: ["second:True", "first:True"]);
        Assert.False(condition: Directory.Exists(path: state.RootPath));
    }
    [Fact]
    public void ADirectoryWaitsForAHandleAnOwnerLetsGoOfAfterItsDisposeReturns() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "an open handle blocks a delete only where the file system refuses it.");
        var state = new TemporaryDirectory(prefix: "puck-fixture-handle-");

        _ = state.Own(owner: new Straggler(held: Hold(state: state), writes: false));
        state.Dispose();

        Assert.False(condition: Directory.Exists(path: state.RootPath));
    }
    [Fact]
    public void ADirectoryNamesTheFileAnOwnersWorkerWroteAfterItsDisposeReturned() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "an open handle blocks a delete only where the file system refuses it.");
        var state = new TemporaryDirectory(prefix: "puck-fixture-stray-");

        _ = state.Own(owner: new Straggler(held: Hold(state: state), writes: true));

        var failure = Assert.Throws<InvalidOperationException>(testCode: state.Dispose);

        Assert.Contains(expectedSubstring: Late, actualString: failure.Message);
        Assert.False(condition: Directory.Exists(path: state.RootPath));
    }
}
