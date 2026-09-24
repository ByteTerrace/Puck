using Xunit;

namespace Puck.Assets.Tests;

// A promotion out of tmp/ either lands the object or ref, or leaves tmp/ exactly as it found it: a failed
// File.Move never orphans the temp file it was about to promote.
public sealed class ContentAddressedStoreLawTests : IDisposable {
    private readonly DirectoryInfo m_root = Directory.CreateTempSubdirectory(prefix: "puck-content-store-");

    public void Dispose() =>
        m_root.Delete(recursive: true);

    private string[] TmpEntries() =>
        [.. Directory.EnumerateFileSystemEntries(path: Path.Combine(path1: m_root.FullName, path2: "tmp"))];

    [Fact]
    public void APutWhosePromotionFailsLeavesNoTemporaryFile() {
        var store = new ContentAddressedStore(root: m_root.FullName);
        var content = "hello"u8.ToArray();
        var pin = ContentPin.Compute(content: content);
        var objectPath = ContentAddressedStore.ObjectPath(
            pin: pin,
            root: m_root.FullName
        );

        // A directory at the object's own path makes the final rename fail after the temp file is fully written;
        // File.Exists reports false for a directory, so this is not the concurrent-Put race Put already tolerates.
        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: objectPath)!);
        _ = Directory.CreateDirectory(path: objectPath);

        _ = Assert.ThrowsAny<Exception>(testCode: () => store.Put(content: content));

        Assert.True(condition: Directory.Exists(path: objectPath));
        Assert.Empty(collection: TmpEntries());
    }
    [Fact]
    public void ASetRefWhosePromotionFailsLeavesNoTemporaryFile() {
        var store = new ContentAddressedStore(root: m_root.FullName);
        var hash = ContentPin.Compute(content: "hello"u8.ToArray());
        var refPath = Path.Combine(path1: m_root.FullName, path2: "refs", path3: "worlds", path4: "demo");

        // A directory at the ref's own path makes the final rename fail after the temp file is fully written.
        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: refPath)!);
        _ = Directory.CreateDirectory(path: refPath);

        _ = Assert.ThrowsAny<Exception>(testCode: () => store.SetRef(
            category: "worlds",
            hash: hash,
            name: "demo"
        ));

        Assert.True(condition: Directory.Exists(path: refPath));
        Assert.Empty(collection: TmpEntries());
    }
}
