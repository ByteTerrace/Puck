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

    private static FileStream Hold(TemporaryDirectory state) => new(
        access: FileAccess.Write,
        mode: FileMode.Create,
        path: state.PathOf(name: Late),
        share: FileShare.Read
    );

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
