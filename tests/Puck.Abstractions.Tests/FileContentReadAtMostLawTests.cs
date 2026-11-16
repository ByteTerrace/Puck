using Puck.Confinement;

namespace Puck.Abstractions.Tests;

// FileContent.ReadAtMost is the one bounded read: the reads decide the content's length, never the stream's reported
// Length, so a file that grows after its length was taken can neither allocate past the ceiling nor return a prefix.
public sealed class FileContentReadAtMostLawTests {
    // A stream whose reported length is a lie: small when first asked, enormous afterwards, as a file another writer
    // extends between a length check and a sized allocation reports itself.
    private sealed class GrowingStream(byte[] content, long firstLength) : MemoryStream(buffer: content) {
        private int m_asked;

        public override long Length => ((Interlocked.Increment(location: ref m_asked) == 1) ? firstLength : (1L << 40));
    }

    [Fact]
    public void AStreamThatGrowsPastTheCeilingAfterItsLengthWasTakenIsRefusedWithoutAnAllocationPastTheCeiling() {
        using var stream = new GrowingStream(content: new byte[100], firstLength: 10);

        Assert.Null(@object: FileContent.ReadAtMost(maximumBytes: 64, stream: stream));
    }
    [Fact]
    public void AStreamThatGrowsWithinTheCeilingReturnsEveryByteRatherThanTheReportedPrefix() {
        var content = Enumerable.Range(count: 40, start: 0).Select(selector: static index => ((byte)index)).ToArray();
        using var stream = new GrowingStream(content: content, firstLength: 10);

        Assert.Equal(expected: content, actual: FileContent.ReadAtMost(maximumBytes: 64, stream: stream));
    }
    [Fact]
    public async Task ContentExactlyAtTheCeilingIsReadAndOneBytePastItIsRefusedOnBothPaths() {
        using var exact = new MemoryStream(buffer: new byte[64]);
        using var over = new MemoryStream(buffer: new byte[65]);
        using var exactAsync = new MemoryStream(buffer: new byte[64]);
        using var overAsync = new MemoryStream(buffer: new byte[65]);
        using var empty = new MemoryStream();

        Assert.Equal(expected: 64, actual: FileContent.ReadAtMost(maximumBytes: 64, stream: exact)!.Length);
        Assert.Null(@object: FileContent.ReadAtMost(maximumBytes: 64, stream: over));
        Assert.Equal(expected: 64, actual: (await FileContent.ReadAtMostAsync(cancellationToken: TestContext.Current.CancellationToken, maximumBytes: 64, stream: exactAsync))!.Length);
        Assert.Null(@object: await FileContent.ReadAtMostAsync(cancellationToken: TestContext.Current.CancellationToken, maximumBytes: 64, stream: overAsync));
        Assert.Empty(collection: FileContent.ReadAtMost(maximumBytes: 0, stream: empty)!);
    }
}
