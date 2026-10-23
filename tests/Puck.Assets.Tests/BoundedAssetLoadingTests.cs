using Puck.Testing;
using Xunit;

namespace Puck.Assets.Tests;

public sealed class BoundedAssetLoadingTests {
    [Fact]
    public void FileSourceRefusesOversizedFilesBeforeBufferingAndAcceptsTheBoundary() {
        using var directory = new TemporaryDirectory(prefix: "security-content-source-");
        var path = directory.PathOf("module.wasm");
        var source = new FileSystemAssetSource();
        File.WriteAllBytes(path, new byte[8]);
        Assert.Equal(8, source.Read(path, maximumBytes: 8).Length);
        using (var stream = File.OpenWrite(path)) {
            stream.SetLength(16L * 1024 * 1024);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => source.Read(path, maximumBytes: 8));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, 0, 1024 * 1024);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(path, maximumBytes: -1));
    }

    [Fact]
    public void FullPinCacheSeparatesDigestsWhoseShortHashesCollide() {
        // Synthetic digests prove the cache's equality boundary without claiming a found SHA-256 collision.
        var firstDigest = new byte[32];
        var secondDigest = new byte[32];
        secondDigest[^1] = 1;
        Assert.Equal(AssetContentHash.FromDigest(firstDigest), AssetContentHash.FromDigest(secondDigest));
        var first = ContentPin.FromDigest(firstDigest);
        var second = ContentPin.FromDigest(secondDigest);
        var cache = new ContentAddressedLruCache<ContentPin, string>(capacity: 2);

        Assert.Equal("first", cache.GetOrAdd(first, () => "first"));
        Assert.Equal("second", cache.GetOrAdd(second, () => "second"));
        Assert.Equal("first", cache.GetOrAdd(first, () => throw new InvalidOperationException("Unexpected miss")));
        Assert.Equal("second", cache.GetOrAdd(second, () => throw new InvalidOperationException("Unexpected miss")));
    }
}
