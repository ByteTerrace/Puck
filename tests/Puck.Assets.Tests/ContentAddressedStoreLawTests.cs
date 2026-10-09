using Puck.Testing;
using Xunit;

namespace Puck.Assets.Tests;

// A promotion out of tmp/ either lands the object or ref, or leaves tmp/ exactly as it found it: a failed
// File.Move never orphans the temp file it was about to promote. A store opened with a retention bound is a cache that
// keeps the objects most recently used; one opened without keeps everything.
public sealed class ContentAddressedStoreLawTests : IDisposable {
    private readonly TemporaryDirectory m_root = new(prefix: "puck-content-store-");

    public void Dispose() =>
        m_root.Dispose();

    private string[] TmpEntries() =>
        [.. Directory.EnumerateFileSystemEntries(path: Path.Combine(path1: m_root.RootPath, path2: "tmp"))];

    [Fact]
    public void AStoreWithRetentionKeepsTheObjectsMostRecentlyUsed() {
        var plain = new ContentAddressedStore(root: m_root.RootPath);
        var old = DateTime.UtcNow.AddHours(value: -1);
        var pins = new[] { "read"u8.ToArray(), "older"u8.ToArray(), "oldest"u8.ToArray() }.Select(selector: content => plain.Put(content: content)).ToArray();

        for (var index = 0; (index < pins.Length); index++) {
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: old.AddMinutes(value: -index), path: ContentAddressedStore.ObjectPath(pin: pins[index], root: m_root.RootPath));
        }

        var cache = new ContentAddressedStore(retention: Puck.Abstractions.CacheBound.Entries(maxEntries: 2), root: m_root.RootPath);

        // A read stamps the oldest-but-one object as used, so the write keeps it beside itself.
        Assert.True(condition: cache.TryGet(content: out _, pin: pins[0]));
        var written = cache.Put(content: "written"u8);

        Assert.True(condition: plain.Contains(pin: written));
        Assert.True(condition: plain.Contains(pin: pins[0]));
        Assert.False(condition: plain.Contains(pin: pins[1]));
        Assert.False(condition: plain.Contains(pin: pins[2]));
    }
    [Fact]
    public void AStoreWithoutRetentionKeepsEverything() {
        var store = new ContentAddressedStore(root: m_root.RootPath);
        var pins = Enumerable.Range(count: 5, start: 0).Select(selector: index => store.Put(content: new[] { ((byte)index) })).ToArray();

        Assert.Null(@object: store.Retention);
        Assert.All(collection: pins, action: pin => Assert.True(condition: store.Contains(pin: pin)));
    }
    [Fact]
    public void APutWhosePromotionFailsLeavesNoTemporaryFile() {
        var store = new ContentAddressedStore(root: m_root.RootPath);
        var content = "hello"u8.ToArray();
        var pin = ContentPin.Compute(content: content);
        var objectPath = ContentAddressedStore.ObjectPath(
            pin: pin,
            root: m_root.RootPath
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
        var store = new ContentAddressedStore(root: m_root.RootPath);
        var hash = ContentPin.Compute(content: "hello"u8.ToArray());
        var refPath = Path.Combine(path1: m_root.RootPath, path2: "refs", path3: "worlds", path4: "demo");

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
